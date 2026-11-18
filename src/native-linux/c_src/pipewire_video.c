#define _GNU_SOURCE
#include "pipewire_video.h"
#include "nvenc_cuda.h"
#include <pipewire/pipewire.h>
#include <spa/param/video/format-utils.h>
#include <spa/param/video/raw-utils.h>
#include <spa/buffer/buffer.h>
#include <sys/mman.h>
#include <time.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>

typedef struct {
    struct pw_thread_loop* loop;
    struct pw_context* context;
    struct pw_core* core;
    struct pw_stream* stream;
    struct spa_hook stream_listener;

    void* nvenc;
    int width;
    int height;
    int fps;
    double min_frame_interval;
    double last_frame_time;

    clippy_video_packet_cb callback;
    void* user_data;

    int active;
} PipeWireVideoStream;

static void on_video_process(void* data) {
    PipeWireVideoStream* s = (PipeWireVideoStream*)data;
    if (!s || !s->active || !s->stream) return;

    struct pw_buffer* b = pw_stream_dequeue_buffer(s->stream);
    if (!b) return;

    struct spa_buffer* buf = b->buffer;
    if (!buf || buf->n_datas == 0) {
        pw_stream_queue_buffer(s->stream, b);
        return;
    }

    struct timespec now;
    clock_gettime(CLOCK_MONOTONIC, &now);
    double cur_time = (double)now.tv_sec + (double)now.tv_nsec * 1e-9;

    // Throttle to target FPS
    if (s->last_frame_time > 0.0 && (cur_time - s->last_frame_time) < s->min_frame_interval) {
        pw_stream_queue_buffer(s->stream, b);
        return;
    }
    s->last_frame_time = cur_time;

    uint8_t* data_ptr = (uint8_t*)buf->datas[0].data;
    int mapped = 0;
    size_t map_size = 0;

    if (!data_ptr && buf->datas[0].fd >= 0) {
        map_size = buf->datas[0].maxsize > 0 ? buf->datas[0].maxsize : (size_t)s->width * s->height * 4;
        data_ptr = (uint8_t*)mmap(NULL, map_size, PROT_READ, MAP_SHARED, buf->datas[0].fd, buf->datas[0].mapoffset);
        if (data_ptr != MAP_FAILED) {
            mapped = 1;
        } else {
            data_ptr = NULL;
        }
    }

    if (data_ptr && s->nvenc) {
        uint32_t stride = buf->datas[0].chunk->stride;
        if (stride == 0) stride = s->width * 4;
        uint32_t offset = buf->datas[0].chunk->offset;

        const uint8_t* bitstream = NULL;
        uint32_t bitstream_len = 0;
        int is_keyframe = 0;

        int res = nvenc_cuda_encode(s->nvenc, data_ptr + offset, stride, 0,
                                   &bitstream, &bitstream_len, &is_keyframe);
        if (res == 0 && bitstream_len > 0 && s->callback) {
            s->callback(s->user_data, bitstream, bitstream_len, is_keyframe, cur_time);
            nvenc_cuda_unlock_bitstream(s->nvenc);
        }

        if (mapped) {
            munmap(data_ptr, map_size);
        }
    }

    pw_stream_queue_buffer(s->stream, b);
}

static void on_video_state_changed(void* data, enum pw_stream_state old, enum pw_stream_state state, const char* error) {
    PipeWireVideoStream* s = (PipeWireVideoStream*)data;
    if (!s) return;
    if (state == PW_STREAM_STATE_STREAMING) {
        fprintf(stderr, "[libclippy_linux] Video Stream state: STREAMING\n");
    } else if (state == PW_STREAM_STATE_ERROR) {
        fprintf(stderr, "[libclippy_linux] Video Stream ERROR: %s\n", error ? error : "unknown");
        if (s->callback) {
            s->callback(s->user_data, NULL, 0, 0, 0.0);
        }
    } else if (state == PW_STREAM_STATE_UNCONNECTED && old == PW_STREAM_STATE_STREAMING) {
        fprintf(stderr, "[libclippy_linux] Video Stream UNCONNECTED (disconnected)\n");
        if (s->callback) {
            s->callback(s->user_data, NULL, 0, 0, 0.0);
        }
    }
}

static const struct pw_stream_events video_stream_events = {
    PW_VERSION_STREAM_EVENTS,
    .process = on_video_process,
    .state_changed = on_video_state_changed,
};

int clippy_pipewire_video_start(int pw_fd, uint32_t node_id, int width, int height, int fps,
                                int bitrate_bps, int is_hevc, clippy_video_packet_cb cb,
                                void* user_data, void** out_stream) {
    if (!out_stream || pw_fd < 0 || width <= 0 || height <= 0) return -1;
    *out_stream = NULL;

    PipeWireVideoStream* s = (PipeWireVideoStream*)calloc(1, sizeof(PipeWireVideoStream));
    if (!s) return -2;

    s->width = width;
    s->height = height;
    s->fps = fps > 0 ? fps : 60;
    s->min_frame_interval = 1.0 / (s->fps + 2.0); // Allow slight jitter
    s->callback = cb;
    s->user_data = user_data;

    // 1. Initialize NVENC encoder
    int nv_res = nvenc_cuda_create(width, height, s->fps, bitrate_bps, is_hevc, &s->nvenc);
    if (nv_res != 0) {
        free(s);
        return -3; // NVENC init failure -> trigger fallback in C#
    }

    // 2. Initialize PipeWire
    pw_init(NULL, NULL);

    s->loop = pw_thread_loop_new("clippy-video-loop", NULL);
    if (!s->loop) {
        nvenc_cuda_destroy(s->nvenc);
        free(s);
        return -4;
    }

    if (pw_thread_loop_start(s->loop) < 0) {
        pw_thread_loop_destroy(s->loop);
        nvenc_cuda_destroy(s->nvenc);
        free(s);
        return -5;
    }

    pw_thread_loop_lock(s->loop);

    s->context = pw_context_new(pw_thread_loop_get_loop(s->loop), NULL, 0);
    if (!s->context) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_video_stop(s);
        return -6;
    }

    int fd = dup(pw_fd);
    if (fd < 0) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_video_stop(s);
        return -7;
    }

    s->core = pw_context_connect_fd(s->context, fd, NULL, 0);
    if (!s->core) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_video_stop(s);
        return -8;
    }

    char target_str[32];
    snprintf(target_str, sizeof(target_str), "%u", node_id);

    struct pw_properties* props = pw_properties_new(
        PW_KEY_MEDIA_TYPE, "Video",
        PW_KEY_MEDIA_CATEGORY, "Capture",
        PW_KEY_MEDIA_ROLE, "Screen",
        PW_KEY_TARGET_OBJECT, target_str,
        NULL
    );

    s->stream = pw_stream_new(s->core, "Clippy Screen Capture", props);
    if (!s->stream) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_video_stop(s);
        return -9;
    }

    pw_stream_add_listener(s->stream, &s->stream_listener, &video_stream_events, s);

    // Build format parameters: BGRx and BGRA
    uint8_t buffer1[512];
    struct spa_pod_builder b1 = SPA_POD_BUILDER_INIT(buffer1, sizeof(buffer1));
    struct spa_video_info_raw info1;
    memset(&info1, 0, sizeof(info1));
    info1.format = SPA_VIDEO_FORMAT_BGRx;
    info1.size.width = width;
    info1.size.height = height;
    info1.framerate.num = s->fps;
    info1.framerate.denom = 1;

    uint8_t buffer2[512];
    struct spa_pod_builder b2 = SPA_POD_BUILDER_INIT(buffer2, sizeof(buffer2));
    struct spa_video_info_raw info2;
    memset(&info2, 0, sizeof(info2));
    info2.format = SPA_VIDEO_FORMAT_BGRA;
    info2.size.width = width;
    info2.size.height = height;
    info2.framerate.num = s->fps;
    info2.framerate.denom = 1;

    const struct spa_pod* params[2];
    params[0] = spa_format_video_raw_build(&b1, SPA_PARAM_EnumFormat, &info1);
    params[1] = spa_format_video_raw_build(&b2, SPA_PARAM_EnumFormat, &info2);

    int conn_res = pw_stream_connect(s->stream,
                                     PW_DIRECTION_INPUT,
                                     node_id,
                                     PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS,
                                     params, 2);

    if (conn_res < 0) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_video_stop(s);
        return -9;
    }

    s->active = 1;
    pw_thread_loop_unlock(s->loop);

    *out_stream = s;
    return 0;
}

int clippy_pipewire_video_get_sequence_params(void* stream, const uint8_t** out_params, uint32_t* out_size) {
    if (!stream || !out_params || !out_size) return -1;
    PipeWireVideoStream* s = (PipeWireVideoStream*)stream;
    return nvenc_cuda_get_sequence_params(s->nvenc, out_params, out_size);
}

void clippy_pipewire_video_stop(void* stream) {
    if (!stream) return;
    PipeWireVideoStream* s = (PipeWireVideoStream*)stream;

    if (s->loop) {
        pw_thread_loop_lock(s->loop);
        s->active = 0;
        if (s->stream) {
            pw_stream_destroy(s->stream);
            s->stream = NULL;
        }
        if (s->core) {
            pw_core_disconnect(s->core);
            s->core = NULL;
        }
        if (s->context) {
            pw_context_destroy(s->context);
            s->context = NULL;
        }
        pw_thread_loop_unlock(s->loop);
        pw_thread_loop_stop(s->loop);
        pw_thread_loop_destroy(s->loop);
        s->loop = NULL;
    }

    if (s->nvenc) {
        nvenc_cuda_destroy(s->nvenc);
        s->nvenc = NULL;
    }

    free(s);
}
