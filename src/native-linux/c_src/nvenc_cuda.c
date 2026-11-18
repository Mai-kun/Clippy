#define _GNU_SOURCE
#include "nvenc_cuda.h"
#include "nvEncodeAPI.h"
#include <stdio.h>
#include <stdlib.h>
#include <string.h>
#include <dlfcn.h>
#include <unistd.h>

typedef int CUresult;
typedef int CUdevice;
typedef void* CUcontext;
typedef void* CUstream;
typedef unsigned long long CUdeviceptr;

typedef enum {
    CU_MEMORYTYPE_HOST    = 1,
    CU_MEMORYTYPE_DEVICE  = 2,
    CU_MEMORYTYPE_ARRAY   = 3,
    CU_MEMORYTYPE_UNIFIED = 4
} CUmemorytype;

typedef struct {
    size_t srcXInBytes;
    size_t srcY;
    CUmemorytype srcMemoryType;
    const void *srcHost;
    CUdeviceptr srcDevice;
    void *srcArray;
    size_t srcPitch;

    size_t dstXInBytes;
    size_t dstY;
    CUmemorytype dstMemoryType;
    void *dstHost;
    CUdeviceptr dstDevice;
    void *dstArray;
    size_t dstPitch;

    size_t WidthInBytes;
    size_t Height;
} CUDA_MEMCPY2D;

typedef CUresult (*PFN_cuInit)(unsigned int Flags);
typedef CUresult (*PFN_cuDeviceGet)(CUdevice *device, int ordinal);
typedef CUresult (*PFN_cuCtxCreate)(CUcontext *pctx, unsigned int flags, CUdevice dev);
typedef CUresult (*PFN_cuCtxDestroy)(CUcontext ctx);
typedef CUresult (*PFN_cuCtxPushCurrent)(CUcontext ctx);
typedef CUresult (*PFN_cuCtxPopCurrent)(CUcontext *pctx);
typedef CUresult (*PFN_cuMemAllocPitch)(CUdeviceptr *dptr, size_t *pPitch, size_t WidthInBytes, size_t Height, unsigned int ElementSizeBytes);
typedef CUresult (*PFN_cuMemFree)(CUdeviceptr dptr);
typedef CUresult (*PFN_cuMemcpy2DAsync)(const CUDA_MEMCPY2D *pCopy, CUstream hStream);
typedef CUresult (*PFN_cuStreamCreate)(CUstream *phStream, unsigned int Flags);
typedef CUresult (*PFN_cuStreamDestroy)(CUstream hStream);
typedef CUresult (*PFN_cuStreamSynchronize)(CUstream hStream);

typedef NVENCSTATUS (NVENCAPI *NVENCCREATEINSTANCE)(NV_ENCODE_API_FUNCTION_LIST *);

typedef struct {
    void* libcuda;
    void* libnvenc;

    PFN_cuInit cuInit;
    PFN_cuDeviceGet cuDeviceGet;
    PFN_cuCtxCreate cuCtxCreate;
    PFN_cuCtxDestroy cuCtxDestroy;
    PFN_cuCtxPushCurrent cuCtxPushCurrent;
    PFN_cuCtxPopCurrent cuCtxPopCurrent;
    PFN_cuMemAllocPitch cuMemAllocPitch;
    PFN_cuMemFree cuMemFree;
    PFN_cuMemcpy2DAsync cuMemcpy2DAsync;
    PFN_cuStreamCreate cuStreamCreate;
    PFN_cuStreamDestroy cuStreamDestroy;
    PFN_cuStreamSynchronize cuStreamSynchronize;

    CUcontext cu_ctx;
    CUstream cu_stream;

    NV_ENCODE_API_FUNCTION_LIST nvenc;
    void* encoder;

    int width;
    int height;
    int fps;
    int is_hevc;

    CUdeviceptr d_input;
    size_t input_pitch;
    NV_ENC_REGISTERED_PTR reg_resource;
    NV_ENC_OUTPUT_PTR bitstream_buf;

    uint8_t seq_params[1024];
    uint32_t seq_params_size;
} NvencCudaState;

static void* resolve_sym(void* handle, const char* name1, const char* name2) {
    void* ptr = dlsym(handle, name1);
    if (!ptr && name2) {
        ptr = dlsym(handle, name2);
    }
    return ptr;
}

int nvenc_cuda_is_supported(int is_hevc) {
    void* libcuda = dlopen("libcuda.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!libcuda) return 0;

    void* libnvenc = dlopen("libnvidia-encode.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!libnvenc) {
        dlclose(libcuda);
        return 0;
    }

    PFN_cuInit cuInit = (PFN_cuInit)dlsym(libcuda, "cuInit");
    PFN_cuDeviceGet cuDeviceGet = (PFN_cuDeviceGet)dlsym(libcuda, "cuDeviceGet");
    PFN_cuCtxCreate cuCtxCreate = (PFN_cuCtxCreate)resolve_sym(libcuda, "cuCtxCreate_v2", "cuCtxCreate");
    PFN_cuCtxDestroy cuCtxDestroy = (PFN_cuCtxDestroy)resolve_sym(libcuda, "cuCtxDestroy_v2", "cuCtxDestroy");
    NVENCCREATEINSTANCE NvEncodeAPICreateInstance = 
        (NVENCCREATEINSTANCE)dlsym(libnvenc, "NvEncodeAPICreateInstance");

    if (!cuInit || !cuDeviceGet || !cuCtxCreate || !NvEncodeAPICreateInstance) {
        dlclose(libnvenc);
        dlclose(libcuda);
        return 0;
    }

    if (cuInit(0) != 0) {
        dlclose(libnvenc);
        dlclose(libcuda);
        return 0;
    }

    CUdevice dev = 0;
    if (cuDeviceGet(&dev, 0) != 0) {
        dlclose(libnvenc);
        dlclose(libcuda);
        return 0;
    }

    CUcontext ctx = NULL;
    if (cuCtxCreate(&ctx, 0, dev) != 0) {
        dlclose(libnvenc);
        dlclose(libcuda);
        return 0;
    }

    NV_ENCODE_API_FUNCTION_LIST api;
    memset(&api, 0, sizeof(api));
    api.version = NV_ENCODE_API_FUNCTION_LIST_VER;
    int supported = 0;

    if (NvEncodeAPICreateInstance(&api) == NV_ENC_SUCCESS) {
        NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS sp;
        memset(&sp, 0, sizeof(sp));
        sp.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
        sp.deviceType = NV_ENC_DEVICE_TYPE_CUDA;
        sp.device = ctx;
        sp.apiVersion = NVENCAPI_VERSION;

        void* enc = NULL;
        if (api.nvEncOpenEncodeSessionEx(&sp, &enc) == NV_ENC_SUCCESS) {
            uint32_t count = 0;
            const GUID* guid = is_hevc ? &NV_ENC_CODEC_HEVC_GUID : &NV_ENC_CODEC_H264_GUID;
            if (api.nvEncGetEncodePresetCount(enc, *guid, &count) == NV_ENC_SUCCESS && count > 0) {
                supported = 1;
            }
            api.nvEncDestroyEncoder(enc);
        }
    }

    if (cuCtxDestroy) cuCtxDestroy(ctx);
    dlclose(libnvenc);
    dlclose(libcuda);
    return supported;
}

int nvenc_cuda_create(int width, int height, int fps, int bitrate_bps, int is_hevc, void** out_ctx) {
    if (!out_ctx) return -1;
    *out_ctx = NULL;

    NvencCudaState* s = (NvencCudaState*)calloc(1, sizeof(NvencCudaState));
    if (!s) return -2;

    s->width = width;
    s->height = height;
    s->fps = fps > 0 ? fps : 60;
    s->is_hevc = is_hevc;

    s->libcuda = dlopen("libcuda.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!s->libcuda) {
        free(s);
        return -3;
    }

    s->libnvenc = dlopen("libnvidia-encode.so.1", RTLD_NOW | RTLD_LOCAL);
    if (!s->libnvenc) {
        dlclose(s->libcuda);
        free(s);
        return -4;
    }

    s->cuInit = (PFN_cuInit)dlsym(s->libcuda, "cuInit");
    s->cuDeviceGet = (PFN_cuDeviceGet)dlsym(s->libcuda, "cuDeviceGet");
    s->cuCtxCreate = (PFN_cuCtxCreate)resolve_sym(s->libcuda, "cuCtxCreate_v2", "cuCtxCreate");
    s->cuCtxDestroy = (PFN_cuCtxDestroy)resolve_sym(s->libcuda, "cuCtxDestroy_v2", "cuCtxDestroy");
    s->cuCtxPushCurrent = (PFN_cuCtxPushCurrent)resolve_sym(s->libcuda, "cuCtxPushCurrent_v2", "cuCtxPushCurrent");
    s->cuCtxPopCurrent = (PFN_cuCtxPopCurrent)resolve_sym(s->libcuda, "cuCtxPopCurrent_v2", "cuCtxPopCurrent");
    s->cuMemAllocPitch = (PFN_cuMemAllocPitch)resolve_sym(s->libcuda, "cuMemAllocPitch_v2", "cuMemAllocPitch");
    s->cuMemFree = (PFN_cuMemFree)resolve_sym(s->libcuda, "cuMemFree_v2", "cuMemFree");
    s->cuMemcpy2DAsync = (PFN_cuMemcpy2DAsync)resolve_sym(s->libcuda, "cuMemcpy2DAsync_v2", "cuMemcpy2DAsync");
    s->cuStreamCreate = (PFN_cuStreamCreate)dlsym(s->libcuda, "cuStreamCreate");
    s->cuStreamDestroy = (PFN_cuStreamDestroy)resolve_sym(s->libcuda, "cuStreamDestroy_v2", "cuStreamDestroy");
    s->cuStreamSynchronize = (PFN_cuStreamSynchronize)dlsym(s->libcuda, "cuStreamSynchronize");

    if (!s->cuInit || !s->cuDeviceGet || !s->cuCtxCreate || !s->cuMemAllocPitch || !s->cuMemcpy2DAsync) {
        nvenc_cuda_destroy(s);
        return -5;
    }

    if (s->cuInit(0) != 0) {
        nvenc_cuda_destroy(s);
        return -6;
    }

    CUdevice dev = 0;
    if (s->cuDeviceGet(&dev, 0) != 0) {
        nvenc_cuda_destroy(s);
        return -7;
    }

    if (s->cuCtxCreate(&s->cu_ctx, 0, dev) != 0) {
        nvenc_cuda_destroy(s);
        return -8;
    }

    if (s->cuStreamCreate(&s->cu_stream, 0) != 0) {
        nvenc_cuda_destroy(s);
        return -9;
    }

    NVENCCREATEINSTANCE NvEncodeAPICreateInstance = 
        (NVENCCREATEINSTANCE)dlsym(s->libnvenc, "NvEncodeAPICreateInstance");
    if (!NvEncodeAPICreateInstance) {
        nvenc_cuda_destroy(s);
        return -10;
    }

    s->nvenc.version = NV_ENCODE_API_FUNCTION_LIST_VER;
    if (NvEncodeAPICreateInstance(&s->nvenc) != NV_ENC_SUCCESS) {
        nvenc_cuda_destroy(s);
        return -11;
    }

    NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS sp;
    memset(&sp, 0, sizeof(sp));
    sp.version = NV_ENC_OPEN_ENCODE_SESSION_EX_PARAMS_VER;
    sp.deviceType = NV_ENC_DEVICE_TYPE_CUDA;
    sp.device = s->cu_ctx;
    sp.apiVersion = NVENCAPI_VERSION;

    if (s->nvenc.nvEncOpenEncodeSessionEx(&sp, &s->encoder) != NV_ENC_SUCCESS) {
        nvenc_cuda_destroy(s);
        return -12;
    }

    const GUID* codecGuid = is_hevc ? &NV_ENC_CODEC_HEVC_GUID : &NV_ENC_CODEC_H264_GUID;

    NV_ENC_INITIALIZE_PARAMS initParams;
    memset(&initParams, 0, sizeof(initParams));
    initParams.version = NV_ENC_INITIALIZE_PARAMS_VER;
    initParams.encodeGUID = *codecGuid;
    initParams.presetGUID = NV_ENC_PRESET_P1_GUID;
    initParams.encodeWidth = width;
    initParams.encodeHeight = height;
    initParams.darWidth = width;
    initParams.darHeight = height;
    initParams.frameRateNum = s->fps;
    initParams.frameRateDen = 1;
    initParams.enablePTD = 1;

    NV_ENC_PRESET_CONFIG presetCfg;
    memset(&presetCfg, 0, sizeof(presetCfg));
    presetCfg.version = NV_ENC_PRESET_CONFIG_VER;
    presetCfg.presetCfg.version = NV_ENC_CONFIG_VER;

    if (s->nvenc.nvEncGetEncodePresetConfigEx(s->encoder, *codecGuid, NV_ENC_PRESET_P1_GUID,
                                              NV_ENC_TUNING_INFO_LOW_LATENCY, &presetCfg) == NV_ENC_SUCCESS) {
        initParams.encodeConfig = &presetCfg.presetCfg;
        initParams.tuningInfo = NV_ENC_TUNING_INFO_LOW_LATENCY;
    }

    NV_ENC_CONFIG config;
    if (initParams.encodeConfig) {
        memcpy(&config, initParams.encodeConfig, sizeof(NV_ENC_CONFIG));
    } else {
        memset(&config, 0, sizeof(config));
        config.version = NV_ENC_CONFIG_VER;
    }

    config.gopLength = s->fps;
    config.frameIntervalP = 1; // IPPP, 0 B-frames
    config.rcParams.rateControlMode = NV_ENC_PARAMS_RC_VBR;
    config.rcParams.averageBitRate = bitrate_bps > 0 ? bitrate_bps : 6000000;
    config.rcParams.maxBitRate = (uint32_t)(config.rcParams.averageBitRate * 1.5);
    config.rcParams.enableMinQP = 1;
    config.rcParams.minQP.qpIntra = 18;
    config.rcParams.minQP.qpInterP = 20;
    config.rcParams.minQP.qpInterB = 20;

    if (is_hevc) {
        config.encodeCodecConfig.hevcConfig.idrPeriod = s->fps;
        config.encodeCodecConfig.hevcConfig.repeatSPSPPS = 1;
    } else {
        config.encodeCodecConfig.h264Config.idrPeriod = s->fps;
        config.encodeCodecConfig.h264Config.repeatSPSPPS = 1;
    }

    initParams.encodeConfig = &config;

    if (s->nvenc.nvEncInitializeEncoder(s->encoder, &initParams) != NV_ENC_SUCCESS) {
        nvenc_cuda_destroy(s);
        return -13;
    }

    // Allocate pitched input buffer in CUDA VRAM (ARGB/BGRx 4 bytes per pixel)
    if (s->cuMemAllocPitch(&s->d_input, &s->input_pitch, width * 4, height, 16) != 0) {
        nvenc_cuda_destroy(s);
        return -14;
    }

    NV_ENC_REGISTER_RESOURCE reg;
    memset(&reg, 0, sizeof(reg));
    reg.version = NV_ENC_REGISTER_RESOURCE_VER;
    reg.resourceType = NV_ENC_INPUT_RESOURCE_TYPE_CUDADEVICEPTR;
    reg.width = width;
    reg.height = height;
    reg.pitch = s->input_pitch;
    reg.resourceToRegister = (void*)s->d_input;
    reg.bufferFormat = NV_ENC_BUFFER_FORMAT_ARGB;
    reg.bufferUsage = NV_ENC_INPUT_IMAGE;

    if (s->nvenc.nvEncRegisterResource(s->encoder, &reg) != NV_ENC_SUCCESS) {
        nvenc_cuda_destroy(s);
        return -15;
    }
    s->reg_resource = reg.registeredResource;

    NV_ENC_CREATE_BITSTREAM_BUFFER bsb;
    memset(&bsb, 0, sizeof(bsb));
    bsb.version = NV_ENC_CREATE_BITSTREAM_BUFFER_VER;
    if (s->nvenc.nvEncCreateBitstreamBuffer(s->encoder, &bsb) != NV_ENC_SUCCESS) {
        nvenc_cuda_destroy(s);
        return -16;
    }
    s->bitstream_buf = bsb.bitstreamBuffer;

    // Cache sequence parameters (VPS, SPS, PPS)
    NV_ENC_SEQUENCE_PARAM_PAYLOAD seq;
    memset(&seq, 0, sizeof(seq));
    seq.version = NV_ENC_SEQUENCE_PARAM_PAYLOAD_VER;
    seq.spsppsBuffer = s->seq_params;
    seq.inBufferSize = sizeof(s->seq_params);
    uint32_t out_payload_size = 0;
    seq.outSPSPPSPayloadSize = &out_payload_size;
    if (s->nvenc.nvEncGetSequenceParams(s->encoder, &seq) == NV_ENC_SUCCESS) {
        s->seq_params_size = out_payload_size;
    }

    *out_ctx = s;
    return 0;
}

int nvenc_cuda_get_sequence_params(void* ctx, const uint8_t** out_params, uint32_t* out_size) {
    if (!ctx || !out_params || !out_size) return -1;
    NvencCudaState* s = (NvencCudaState*)ctx;
    *out_params = s->seq_params;
    *out_size = s->seq_params_size;
    return 0;
}

int nvenc_cuda_encode(void* ctx, const uint8_t* frame_data, uint32_t stride, int force_keyframe,
                      const uint8_t** out_bitstream, uint32_t* out_size, int* out_is_keyframe) {
    if (!ctx || !frame_data || !out_bitstream || !out_size || !out_is_keyframe) return -1;
    NvencCudaState* s = (NvencCudaState*)ctx;

    // 1. Copy host pixels to pitched CUDA VRAM asynchronously
    CUDA_MEMCPY2D cpy;
    memset(&cpy, 0, sizeof(cpy));
    cpy.srcMemoryType = CU_MEMORYTYPE_HOST;
    cpy.srcHost = frame_data;
    cpy.srcPitch = stride > 0 ? stride : s->width * 4;
    cpy.dstMemoryType = CU_MEMORYTYPE_DEVICE;
    cpy.dstDevice = s->d_input;
    cpy.dstPitch = s->input_pitch;
    cpy.WidthInBytes = s->width * 4;
    cpy.Height = s->height;

    if (s->cuMemcpy2DAsync(&cpy, s->cu_stream) != 0) return -2;
    if (s->cuStreamSynchronize(s->cu_stream) != 0) return -3;

    // 2. Map input resource into NVENC
    NV_ENC_MAP_INPUT_RESOURCE map;
    memset(&map, 0, sizeof(map));
    map.version = NV_ENC_MAP_INPUT_RESOURCE_VER;
    map.registeredResource = s->reg_resource;

    if (s->nvenc.nvEncMapInputResource(s->encoder, &map) != NV_ENC_SUCCESS) return -4;

    // 3. Submit encode picture
    NV_ENC_PIC_PARAMS pic;
    memset(&pic, 0, sizeof(pic));
    pic.version = NV_ENC_PIC_PARAMS_VER;
    pic.inputBuffer = map.mappedResource;
    pic.bufferFmt = NV_ENC_BUFFER_FORMAT_ARGB;
    pic.inputWidth = s->width;
    pic.inputHeight = s->height;
    pic.inputPitch = s->input_pitch;
    pic.outputBitstream = s->bitstream_buf;
    pic.pictureStruct = NV_ENC_PIC_STRUCT_FRAME;

    if (force_keyframe) {
        pic.encodePicFlags = NV_ENC_PIC_FLAG_FORCEIDR;
    }

    NVENCSTATUS encStatus = s->nvenc.nvEncEncodePicture(s->encoder, &pic);
    s->nvenc.nvEncUnmapInputResource(s->encoder, map.mappedResource);

    if (encStatus != NV_ENC_SUCCESS) return -5;

    // 4. Lock bitstream buffer and extract data
    NV_ENC_LOCK_BITSTREAM lock;
    memset(&lock, 0, sizeof(lock));
    lock.version = NV_ENC_LOCK_BITSTREAM_VER;
    lock.outputBitstream = s->bitstream_buf;
    lock.doNotWait = 0;

    if (s->nvenc.nvEncLockBitstream(s->encoder, &lock) != NV_ENC_SUCCESS) return -6;

    *out_bitstream = (const uint8_t*)lock.bitstreamBufferPtr;
    *out_size = lock.bitstreamSizeInBytes;
    *out_is_keyframe = (lock.pictureType == NV_ENC_PIC_TYPE_IDR || lock.pictureType == NV_ENC_PIC_TYPE_I);

    return 0;
}

void nvenc_cuda_unlock_bitstream(void* ctx) {
    if (!ctx) return;
    NvencCudaState* s = (NvencCudaState*)ctx;
    if (s->encoder && s->bitstream_buf) {
        s->nvenc.nvEncUnlockBitstream(s->encoder, s->bitstream_buf);
    }
}

void nvenc_cuda_destroy(void* ctx) {
    if (!ctx) return;
    NvencCudaState* s = (NvencCudaState*)ctx;

    if (s->encoder) {
        if (s->bitstream_buf) {
            s->nvenc.nvEncDestroyBitstreamBuffer(s->encoder, s->bitstream_buf);
            s->bitstream_buf = NULL;
        }
        if (s->reg_resource) {
            s->nvenc.nvEncUnregisterResource(s->encoder, s->reg_resource);
            s->reg_resource = NULL;
        }
        s->nvenc.nvEncDestroyEncoder(s->encoder);
        s->encoder = NULL;
    }

    if (s->d_input && s->cuMemFree) {
        s->cuMemFree(s->d_input);
        s->d_input = 0;
    }

    if (s->cu_stream && s->cuStreamDestroy) {
        s->cuStreamDestroy(s->cu_stream);
        s->cu_stream = NULL;
    }

    if (s->cu_ctx && s->cuCtxDestroy) {
        s->cuCtxDestroy(s->cu_ctx);
        s->cu_ctx = NULL;
    }

    if (s->libnvenc) {
        dlclose(s->libnvenc);
        s->libnvenc = NULL;
    }

    if (s->libcuda) {
        dlclose(s->libcuda);
        s->libcuda = NULL;
    }

    free(s);
}
