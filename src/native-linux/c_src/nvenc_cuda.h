#ifndef NVENC_CUDA_H
#define NVENC_CUDA_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

int nvenc_cuda_is_supported(int is_hevc);

int nvenc_cuda_create(int width, int height, int fps, int bitrate_bps, int is_hevc, void** out_ctx);

int nvenc_cuda_get_sequence_params(void* ctx, const uint8_t** out_params, uint32_t* out_size);

int nvenc_cuda_encode(void* ctx, const uint8_t* frame_data, uint32_t stride, int force_keyframe,
                      const uint8_t** out_bitstream, uint32_t* out_size, int* out_is_keyframe);

void nvenc_cuda_unlock_bitstream(void* ctx);

void nvenc_cuda_destroy(void* ctx);

#ifdef __cplusplus
}
#endif

#endif // NVENC_CUDA_H
