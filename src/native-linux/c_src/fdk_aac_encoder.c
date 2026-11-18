#define _GNU_SOURCE
#include "fdk_aac_encoder.h"
#include <fdk-aac/aacenc_lib.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    HANDLE_AACENCODER handle;
    int channels;
    int sample_rate;
} FdkAacState;

int fdk_aac_create(int sample_rate, int channels, int bitrate, void** out_ctx) {
    if (!out_ctx) return -1;
    *out_ctx = NULL;

    FdkAacState* s = (FdkAacState*)calloc(1, sizeof(FdkAacState));
    if (!s) return -2;

    s->sample_rate = sample_rate > 0 ? sample_rate : 48000;
    s->channels = channels > 0 ? channels : 2;

    CHANNEL_MODE mode = MODE_2;
    if (channels == 1) mode = MODE_1;
    else if (channels == 2) mode = MODE_2;

    if (aacEncOpen(&s->handle, 0, channels) != AACENC_OK) {
        free(s);
        return -3;
    }

    if (aacEncoder_SetParam(s->handle, AACENC_AOT, AOT_AAC_LC) != AACENC_OK) goto fail;
    if (aacEncoder_SetParam(s->handle, AACENC_SAMPLERATE, s->sample_rate) != AACENC_OK) goto fail;
    if (aacEncoder_SetParam(s->handle, AACENC_CHANNELMODE, mode) != AACENC_OK) goto fail;
    if (aacEncoder_SetParam(s->handle, AACENC_BITRATE, bitrate > 0 ? bitrate : 192000) != AACENC_OK) goto fail;
    if (aacEncoder_SetParam(s->handle, AACENC_TRANSMUX, TT_MP4_ADTS) != AACENC_OK) goto fail;
    if (aacEncoder_SetParam(s->handle, AACENC_AFTERBURNER, 1) != AACENC_OK) goto fail;

    if (aacEncEncode(s->handle, NULL, NULL, NULL, NULL) != AACENC_OK) goto fail;

    *out_ctx = s;
    return 0;

fail:
    if (s->handle) aacEncClose(&s->handle);
    free(s);
    return -4;
}

int fdk_aac_encode_frame(void* ctx, const int16_t* pcm_samples, int num_samples,
                         uint8_t* out_adts, int out_max_size, int* out_adts_len) {
    if (!ctx || !pcm_samples || !out_adts || !out_adts_len) return -1;
    FdkAacState* s = (FdkAacState*)ctx;

    *out_adts_len = 0;

    int input_identifier = IN_AUDIO_DATA;
    int input_elem_size = sizeof(int16_t);
    int input_buffer_size = num_samples * sizeof(int16_t);
    void* input_ptr = (void*)pcm_samples;

    AACENC_BufDesc in_buf;
    memset(&in_buf, 0, sizeof(in_buf));
    in_buf.numBufs = 1;
    in_buf.bufs = &input_ptr;
    in_buf.bufferIdentifiers = &input_identifier;
    in_buf.bufSizes = &input_buffer_size;
    in_buf.bufElSizes = &input_elem_size;

    int output_identifier = OUT_BITSTREAM_DATA;
    int output_elem_size = 1;
    int output_buffer_size = out_max_size;
    void* output_ptr = out_adts;

    AACENC_BufDesc out_buf;
    memset(&out_buf, 0, sizeof(out_buf));
    out_buf.numBufs = 1;
    out_buf.bufs = &output_ptr;
    out_buf.bufferIdentifiers = &output_identifier;
    out_buf.bufSizes = &output_buffer_size;
    out_buf.bufElSizes = &output_elem_size;

    AACENC_InArgs in_args;
    memset(&in_args, 0, sizeof(in_args));
    in_args.numInSamples = num_samples;

    AACENC_OutArgs out_args;
    memset(&out_args, 0, sizeof(out_args));

    AACENC_ERROR err = aacEncEncode(s->handle, &in_buf, &out_buf, &in_args, &out_args);
    if (err != AACENC_OK) {
        return -2;
    }

    *out_adts_len = out_args.numOutBytes;
    return 0;
}

void fdk_aac_destroy(void* ctx) {
    if (!ctx) return;
    FdkAacState* s = (FdkAacState*)ctx;
    if (s->handle) {
        aacEncClose(&s->handle);
        s->handle = NULL;
    }
    free(s);
}
