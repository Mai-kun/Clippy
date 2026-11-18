use std::os::raw::{c_char, c_double, c_int, c_void};

pub type VideoPacketCallback = extern "C" fn(
    user_data: *mut c_void,
    data: *const u8,
    len: u32,
    is_keyframe: c_int,
    clock_sec: c_double,
);

pub type AudioAdtsCallback = extern "C" fn(
    user_data: *mut c_void,
    adts_data: *const u8,
    len: u32,
    timestamp_ns: i64,
);

extern "C" {
    pub fn nvenc_cuda_is_supported(is_hevc: c_int) -> c_int;

    pub fn clippy_pipewire_video_start(
        pw_fd: c_int,
        node_id: u32,
        width: c_int,
        height: c_int,
        fps: c_int,
        bitrate_bps: c_int,
        is_hevc: c_int,
        cb: VideoPacketCallback,
        user_data: *mut c_void,
        out_stream: *mut *mut c_void,
    ) -> c_int;

    pub fn clippy_pipewire_video_get_sequence_params(
        stream: *mut c_void,
        out_params: *mut *const u8,
        out_size: *mut u32,
    ) -> c_int;

    pub fn clippy_pipewire_video_stop(stream: *mut c_void);

    pub fn clippy_pipewire_audio_start(
        sink_name: *const c_char,
        record_mic: c_int,
        mic_name: *const c_char,
        cb: AudioAdtsCallback,
        user_data: *mut c_void,
        out_audio: *mut *mut c_void,
    ) -> c_int;

    pub fn clippy_pipewire_audio_stop(audio: *mut c_void);
}
