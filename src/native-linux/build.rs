fn main() {
    println!("cargo:rerun-if-changed=c_src/nvenc_cuda.h");
    println!("cargo:rerun-if-changed=c_src/nvenc_cuda.c");
    println!("cargo:rerun-if-changed=c_src/pipewire_video.h");
    println!("cargo:rerun-if-changed=c_src/pipewire_video.c");
    println!("cargo:rerun-if-changed=c_src/pipewire_audio.h");
    println!("cargo:rerun-if-changed=c_src/pipewire_audio.c");
    println!("cargo:rerun-if-changed=c_src/fdk_aac_encoder.h");
    println!("cargo:rerun-if-changed=c_src/fdk_aac_encoder.c");

    let mut build = cc::Build::new();
    build
        .file("c_src/nvenc_cuda.c")
        .file("c_src/pipewire_video.c")
        .file("c_src/pipewire_audio.c")
        .file("c_src/fdk_aac_encoder.c")
        .include("c_src")
        .include("../native")
        .include("/usr/include/pipewire-0.3")
        .include("/usr/include/spa-0.2")
        .include("/usr/include/fdk-aac")
        .define("_GNU_SOURCE", None)
        .define("_REENTRANT", None)
        .opt_level(3)
        .flag("-fPIC")
        .warnings(false);

    build.compile("clippy_c_helper");

    println!("cargo:rustc-link-lib=pipewire-0.3");
    println!("cargo:rustc-link-lib=fdk-aac");
    println!("cargo:rustc-link-lib=dl");
    println!("cargo:rustc-link-lib=pthread");
    println!("cargo:rustc-link-lib=m");
}
