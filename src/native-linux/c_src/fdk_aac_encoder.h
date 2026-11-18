#ifndef FDK_AAC_ENCODER_H
#define FDK_AAC_ENCODER_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

int fdk_aac_create(int sample_rate, int channels, int bitrate, void** out_ctx);

int fdk_aac_encode_frame(void* ctx, const int16_t* pcm_samples, int num_samples,
                         uint8_t* out_adts, int out_max_size, int* out_adts_len);

void fdk_aac_destroy(void* ctx);

#ifdef __cplusplus
}
#endif

#endif // FDK_AAC_ENCODER_H
