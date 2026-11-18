pub mod ffi;

use std::os::raw::{c_char, c_int, c_void};
use ffi::*;

#[no_mangle]
pub extern "C" fn clippy_is_supported(is_hevc: c_int) -> c_int {
    unsafe { nvenc_cuda_is_supported(is_hevc) }
}

#[no_mangle]
pub extern "C" fn clippy_video_start(
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
) -> c_int {
    unsafe {
        clippy_pipewire_video_start(
            pw_fd,
            node_id,
            width,
            height,
            fps,
            bitrate_bps,
            is_hevc,
            cb,
            user_data,
            out_stream,
        )
    }
}

#[no_mangle]
pub extern "C" fn clippy_video_get_sequence_params(
    stream: *mut c_void,
    out_params: *mut *const u8,
    out_size: *mut u32,
) -> c_int {
    unsafe { clippy_pipewire_video_get_sequence_params(stream, out_params, out_size) }
}

#[no_mangle]
pub extern "C" fn clippy_video_stop(stream: *mut c_void) {
    if !stream.is_null() {
        unsafe { clippy_pipewire_video_stop(stream) };
    }
}

#[no_mangle]
pub extern "C" fn clippy_audio_start(
    sink_name: *const c_char,
    record_mic: c_int,
    mic_name: *const c_char,
    cb: AudioAdtsCallback,
    user_data: *mut c_void,
    out_audio: *mut *mut c_void,
) -> c_int {
    unsafe {
        clippy_pipewire_audio_start(
            sink_name,
            record_mic,
            mic_name,
            cb,
            user_data,
            out_audio,
        )
    }
}

#[no_mangle]
pub extern "C" fn clippy_audio_stop(audio: *mut c_void) {
    if !audio.is_null() {
        unsafe { clippy_pipewire_audio_stop(audio) };
    }
}
