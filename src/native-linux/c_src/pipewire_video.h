#ifndef PIPEWIRE_VIDEO_H
#define PIPEWIRE_VIDEO_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*clippy_video_packet_cb)(void* user_data, const uint8_t* data, uint32_t len, int is_keyframe, double clock_sec);

int clippy_pipewire_video_start(int pw_fd, uint32_t node_id, int width, int height, int fps,
                                int bitrate_bps, int is_hevc, clippy_video_packet_cb cb,
                                void* user_data, void** out_stream);

int clippy_pipewire_video_get_sequence_params(void* stream, const uint8_t** out_params, uint32_t* out_size);

void clippy_pipewire_video_stop(void* stream);

#ifdef __cplusplus
}
#endif

#endif // PIPEWIRE_VIDEO_H
