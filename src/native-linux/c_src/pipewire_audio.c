#define _GNU_SOURCE
#include "pipewire_audio.h"
#include "fdk_aac_encoder.h"
#include <pipewire/pipewire.h>
#include <spa/param/audio/format-utils.h>
#include <spa/param/audio/raw-utils.h>
#include <spa/buffer/buffer.h>
#include <time.h>
#include <stdlib.h>
#include <string.h>
#include <stdio.h>
#include <pthread.h>

#define CHANNELS 2
#define SAMPLE_RATE 48000
#define SAMPLES_PER_FRAME 1024
#define FRAME_BYTES (SAMPLES_PER_FRAME * CHANNELS * sizeof(int16_t))
#define ACCUM_CAPACITY (16384 * CHANNELS) // ~340 ms buffer
#define ADTS_BUF_SIZE 2048

typedef struct {
    struct pw_thread_loop* loop;
    struct pw_context* context;
    struct pw_core* core;
    struct pw_stream* sys_stream;
    struct spa_hook sys_listener;

    struct pw_stream* mic_stream;
    struct spa_hook mic_listener;
    int record_mic;

    void* aac_enc;
    clippy_audio_adts_cb callback;
    void* user_data;

    pthread_mutex_t lock;
    int16_t accum[ACCUM_CAPACITY];
    size_t accum_count; // in int16 samples

    int64_t next_timestamp_ns;
    int64_t last_packet_time_ns;

    int active;
} PipeWireAudioState;

static int64_t get_monotonic_ns(void) {
    struct timespec ts;
    clock_gettime(CLOCK_MONOTONIC, &ts);
    return (int64_t)ts.tv_sec * 1000000000LL + ts.tv_nsec;
}

static void drain_accum_locked(PipeWireAudioState* s) {
    uint8_t adts_out[ADTS_BUF_SIZE];

    while (s->accum_count >= SAMPLES_PER_FRAME * CHANNELS) {
        int adts_len = 0;
        int res = fdk_aac_encode_frame(s->aac_enc, s->accum, SAMPLES_PER_FRAME * CHANNELS,
                                      adts_out, sizeof(adts_out), &adts_len);

        if (res == 0 && adts_len > 0 && s->callback) {
            s->callback(s->user_data, adts_out, (uint32_t)adts_len, s->next_timestamp_ns);
        }

        // Advance timestamp: 1024 samples @ 48000 Hz = 21,333,333 ns
        s->next_timestamp_ns += (int64_t)SAMPLES_PER_FRAME * 1000000000LL / SAMPLE_RATE;

        // Shift remaining samples
        size_t samples_left = s->accum_count - (SAMPLES_PER_FRAME * CHANNELS);
        if (samples_left > 0) {
            memmove(s->accum, s->accum + (SAMPLES_PER_FRAME * CHANNELS), samples_left * sizeof(int16_t));
        }
        s->accum_count = samples_left;
    }
}

static void append_samples(PipeWireAudioState* s, const int16_t* pcm, size_t sample_count) {
    pthread_mutex_lock(&s->lock);

    int64_t now_ns = get_monotonic_ns();
    if (s->next_timestamp_ns == 0) {
        s->next_timestamp_ns = now_ns;
        s->last_packet_time_ns = now_ns;
    } else {
        // Gap filling: if gap > 40ms, insert silence up to current time
        int64_t gap_ns = now_ns - s->last_packet_time_ns;
        if (gap_ns > 40000000LL) {
            size_t missing_samples = (size_t)((gap_ns * SAMPLE_RATE) / 1000000000LL) * CHANNELS;
            if (missing_samples > 0) {
                // Limit gap filling to max 1.0s to avoid huge spikes
                if (missing_samples > SAMPLE_RATE * CHANNELS) {
                    missing_samples = SAMPLE_RATE * CHANNELS;
                }
                while (missing_samples > 0) {
                    size_t chunk = missing_samples;
                    if (chunk > ACCUM_CAPACITY - s->accum_count) {
                        chunk = ACCUM_CAPACITY - s->accum_count;
                    }
                    if (chunk == 0) break;
                    memset(s->accum + s->accum_count, 0, chunk * sizeof(int16_t));
                    s->accum_count += chunk;
                    drain_accum_locked(s);
                    missing_samples -= chunk;
                }
            }
        }
        s->last_packet_time_ns = now_ns;
    }

    // Append incoming samples
    size_t space = ACCUM_CAPACITY - s->accum_count;
    size_t to_copy = sample_count < space ? sample_count : space;
    if (to_copy > 0) {
        memcpy(s->accum + s->accum_count, pcm, to_copy * sizeof(int16_t));
        s->accum_count += to_copy;
    }

    drain_accum_locked(s);
    pthread_mutex_unlock(&s->lock);
}

static void on_sys_audio_process(void* data) {
    PipeWireAudioState* s = (PipeWireAudioState*)data;
    if (!s || !s->active || !s->sys_stream) return;

    struct pw_buffer* b = pw_stream_dequeue_buffer(s->sys_stream);
    if (!b) return;

    struct spa_buffer* buf = b->buffer;
    if (buf && buf->n_datas > 0 && buf->datas[0].data) {
        uint32_t size = buf->datas[0].chunk->size;
        int16_t* pcm = (int16_t*)((uint8_t*)buf->datas[0].data + buf->datas[0].chunk->offset);
        size_t samples = size / sizeof(int16_t);
        if (samples > 0) {
            append_samples(s, pcm, samples);
        }
    }

    pw_stream_queue_buffer(s->sys_stream, b);
}

static void on_mic_audio_process(void* data) {
    PipeWireAudioState* s = (PipeWireAudioState*)data;
    if (!s || !s->active || !s->mic_stream) return;

    struct pw_buffer* b = pw_stream_dequeue_buffer(s->mic_stream);
    if (!b) return;

    struct spa_buffer* buf = b->buffer;
    if (buf && buf->n_datas > 0 && buf->datas[0].data) {
        uint32_t size = buf->datas[0].chunk->size;
        int16_t* mic_pcm = (int16_t*)((uint8_t*)buf->datas[0].data + buf->datas[0].chunk->offset);
        size_t mic_samples = size / sizeof(int16_t);

        // Mix mic into the latest samples in accumulator
        pthread_mutex_lock(&s->lock);
        size_t mix_count = mic_samples < s->accum_count ? mic_samples : s->accum_count;
        for (size_t i = 0; i < mix_count; i++) {
            int32_t mixed = (int32_t)s->accum[s->accum_count - mix_count + i] + (int32_t)mic_pcm[i];
            if (mixed > 32767) mixed = 32767;
            if (mixed < -32768) mixed = -32768;
            s->accum[s->accum_count - mix_count + i] = (int16_t)mixed;
        }
        pthread_mutex_unlock(&s->lock);
    }

    pw_stream_queue_buffer(s->mic_stream, b);
}

static void on_audio_state_changed(void* data, enum pw_stream_state old, enum pw_stream_state state, const char* error) {
    PipeWireAudioState* s = (PipeWireAudioState*)data;
    if (!s) return;
    if (state == PW_STREAM_STATE_STREAMING) {
        fprintf(stderr, "[libclippy_linux] Audio Stream state: STREAMING\n");
    } else if (state == PW_STREAM_STATE_ERROR) {
        fprintf(stderr, "[libclippy_linux] Audio Stream ERROR: %s\n", error ? error : "unknown");
        if (s->callback) {
            s->callback(s->user_data, NULL, 0, 0);
        }
    } else if (state == PW_STREAM_STATE_UNCONNECTED && old == PW_STREAM_STATE_STREAMING) {
        fprintf(stderr, "[libclippy_linux] Audio Stream UNCONNECTED (disconnected)\n");
        if (s->callback) {
            s->callback(s->user_data, NULL, 0, 0);
        }
    }
}

static const struct pw_stream_events sys_audio_events = {
    PW_VERSION_STREAM_EVENTS,
    .process = on_sys_audio_process,
    .state_changed = on_audio_state_changed,
};

static const struct pw_stream_events mic_audio_events = {
    PW_VERSION_STREAM_EVENTS,
    .process = on_mic_audio_process,
};

int clippy_pipewire_audio_start(const char* sink_name, int record_mic, const char* mic_name,
                                clippy_audio_adts_cb cb, void* user_data, void** out_audio) {
    if (!out_audio) return -1;
    *out_audio = NULL;

    PipeWireAudioState* s = (PipeWireAudioState*)calloc(1, sizeof(PipeWireAudioState));
    if (!s) return -2;

    s->record_mic = record_mic;
    s->callback = cb;
    s->user_data = user_data;
    pthread_mutex_init(&s->lock, NULL);

    // 1. Initialize FDK-AAC encoder
    int aac_res = fdk_aac_create(SAMPLE_RATE, CHANNELS, 192000, &s->aac_enc);
    if (aac_res != 0) {
        pthread_mutex_destroy(&s->lock);
        free(s);
        return -3;
    }

    // 2. Initialize PipeWire
    pw_init(NULL, NULL);

    s->loop = pw_thread_loop_new("clippy-audio-loop", NULL);
    if (!s->loop) {
        clippy_pipewire_audio_stop(s);
        return -4;
    }

    if (pw_thread_loop_start(s->loop) < 0) {
        clippy_pipewire_audio_stop(s);
        return -5;
    }

    pw_thread_loop_lock(s->loop);

    s->context = pw_context_new(pw_thread_loop_get_loop(s->loop), NULL, 0);
    if (!s->context) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_audio_stop(s);
        return -6;
    }

    s->core = pw_context_connect(s->context, NULL, 0);
    if (!s->core) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_audio_stop(s);
        return -7;
    }

    // Setup system capture stream
    struct pw_properties* sys_props = pw_properties_new(
        PW_KEY_MEDIA_TYPE, "Audio",
        PW_KEY_MEDIA_CATEGORY, "Capture",
        PW_KEY_MEDIA_ROLE, "Music",
        PW_KEY_STREAM_CAPTURE_SINK, "true",
        NULL
    );

    if (sink_name && strcmp(sink_name, "default") != 0 && strlen(sink_name) > 0) {
        char clean_sink[256];
        strncpy(clean_sink, sink_name, sizeof(clean_sink) - 1);
        clean_sink[sizeof(clean_sink) - 1] = '\0';
        char* mon = strstr(clean_sink, ".monitor");
        if (mon && *(mon + 8) == '\0') {
            *mon = '\0';
        }
        if (strlen(clean_sink) > 0) {
            pw_properties_set(sys_props, PW_KEY_TARGET_OBJECT, clean_sink);
        }
    }

    s->sys_stream = pw_stream_new(s->core, "Clippy Audio Capture", sys_props);
    if (!s->sys_stream) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_audio_stop(s);
        return -8;
    }

    pw_stream_add_listener(s->sys_stream, &s->sys_listener, &sys_audio_events, s);

    // Build S16_LE stereo format pod
    uint8_t buffer[1024];
    struct spa_pod_builder b = SPA_POD_BUILDER_INIT(buffer, sizeof(buffer));
    struct spa_audio_info_raw info;
    memset(&info, 0, sizeof(info));
    info.format = SPA_AUDIO_FORMAT_S16_LE;
    info.rate = SAMPLE_RATE;
    info.channels = CHANNELS;

    const struct spa_pod* params[1];
    params[0] = spa_format_audio_raw_build(&b, SPA_PARAM_EnumFormat, &info);

    int conn_res = pw_stream_connect(s->sys_stream,
                                     PW_DIRECTION_INPUT,
                                     PW_ID_ANY,
                                     PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS,
                                     params, 1);
    if (conn_res < 0) {
        pw_thread_loop_unlock(s->loop);
        clippy_pipewire_audio_stop(s);
        return -9;
    }

    // Setup optional mic stream
    if (record_mic) {
        struct pw_properties* mic_props = pw_properties_new(
            PW_KEY_MEDIA_TYPE, "Audio",
            PW_KEY_MEDIA_CATEGORY, "Capture",
            PW_KEY_MEDIA_ROLE, "Communication",
            NULL
        );
        if (mic_name && strcmp(mic_name, "default") != 0 && strlen(mic_name) > 0) {
            pw_properties_set(mic_props, PW_KEY_TARGET_OBJECT, mic_name);
        }

        s->mic_stream = pw_stream_new(s->core, "Clippy Mic Capture", mic_props);
        if (s->mic_stream) {
            pw_stream_add_listener(s->mic_stream, &s->mic_listener, &mic_audio_events, s);
            uint8_t mic_buf[1024];
            struct spa_pod_builder mic_b = SPA_POD_BUILDER_INIT(mic_buf, sizeof(mic_buf));
            const struct spa_pod* mic_params[1];
            mic_params[0] = spa_format_audio_raw_build(&mic_b, SPA_PARAM_EnumFormat, &info);
            pw_stream_connect(s->mic_stream, PW_DIRECTION_INPUT, PW_ID_ANY,
                              PW_STREAM_FLAG_AUTOCONNECT | PW_STREAM_FLAG_MAP_BUFFERS,
                              mic_params, 1);
        }
    }

    s->active = 1;
    pw_thread_loop_unlock(s->loop);

    *out_audio = s;
    return 0;
}

void clippy_pipewire_audio_stop(void* audio) {
    if (!audio) return;
    PipeWireAudioState* s = (PipeWireAudioState*)audio;

    if (s->loop) {
        pw_thread_loop_lock(s->loop);
        s->active = 0;
        if (s->mic_stream) {
            pw_stream_destroy(s->mic_stream);
            s->mic_stream = NULL;
        }
        if (s->sys_stream) {
            pw_stream_destroy(s->sys_stream);
            s->sys_stream = NULL;
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

    if (s->aac_enc) {
        fdk_aac_destroy(s->aac_enc);
        s->aac_enc = NULL;
    }

    pthread_mutex_destroy(&s->lock);
    free(s);
}
