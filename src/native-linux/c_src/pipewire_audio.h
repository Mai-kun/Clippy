#ifndef PIPEWIRE_AUDIO_H
#define PIPEWIRE_AUDIO_H

#include <stdint.h>
#include <stddef.h>

#ifdef __cplusplus
extern "C" {
#endif

typedef void (*clippy_audio_adts_cb)(void* user_data, const uint8_t* adts_data, uint32_t len, int64_t timestamp_ns);

int clippy_pipewire_audio_start(const char* sink_name, int record_mic, const char* mic_name,
                                clippy_audio_adts_cb cb, void* user_data, void** out_audio);

void clippy_pipewire_audio_stop(void* audio);

#ifdef __cplusplus
}
#endif

#endif // PIPEWIRE_AUDIO_H
