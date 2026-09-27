# Measures A/V sync of a recorded clip against a known source.
#   python av-sync.py <recorded.mp4> [source.mp4]
# The source has a full-screen white flash and a 1 kHz beep every 5 s, both starting at t=0 by
# construction. Detecting each independently in the recording gives the offset in ms; comparing the
# first and last marker pair shows whether sync drifts over the recording.
import subprocess
import sys
import wave
import struct
import math

rec = sys.argv[1]
src = sys.argv[2] if len(sys.argv) > 2 else None


def video_flash_times(path, threshold=200.0):
    """Mean luma of every frame; a flash is a frame far brighter than its neighbours."""
    # Only the central 20%x20% of the frame is analysed. The player window repaints its edges and
    # title bar, and the whole-frame mean turned those repaints into false flashes (measured: a
    # 2.083 s cadence instead of the source's 5 s, and nothing after ~21 s). The centre is pure
    # video content, so a flash there is unambiguous.
    vf = 'crop=iw/5:ih/5:iw*2/5:ih*2/5,scale=32:18,format=gray'
    out = subprocess.run(
        ['ffmpeg', '-v', 'error', '-i', path, '-vf', vf,
         '-f', 'rawvideo', '-pix_fmt', 'gray', '-'],
        capture_output=True).stdout
    n = 32 * 18
    luma = [sum(out[i:i + n]) / n for i in range(0, len(out) - n + 1, n)]
    fps = 60.0
    # A flash spans several captured frames, and at ~15 fps those frames are >3 apart in index, so an
    # index-based split shattered one flash into many. Split on TIME instead: a real flash is isolated,
    # anything within 0.5 s of the previous hit belongs to it.
    bright = [i for i, v in enumerate(luma) if v > threshold]
    hits = []
    for i in bright:
        if not hits or (i - hits[-1]) / fps > 0.5:
            hits.append(i)
    return [i / fps for i in hits], luma


def audio_beep_times(path, thresh_ratio=0.25):
    """RMS envelope; a beep is a burst well above the noise floor."""
    raw = subprocess.run(
        ['ffmpeg', '-v', 'error', '-i', path, '-ac', '1', '-ar', '8000', '-f', 's16le', '-'],
        capture_output=True).stdout
    samples = struct.unpack('<%dh' % (len(raw) // 2), raw)
    win = 400  # 50 ms at 8 kHz
    rms = []
    for i in range(0, len(samples) - win, win):
        chunk = samples[i:i + win]
        rms.append(math.sqrt(sum(s * s for s in chunk) / win))
    peak = max(rms) if rms else 1.0
    floor = sorted(rms)[len(rms) // 10] if rms else 0.0
    limit = max(floor * 3, peak * thresh_ratio)
    hits = []
    for i, r in enumerate(rms):
        if r > limit and (not hits or (i - hits[-1]) * 0.05 > 0.4):
            hits.append(i * 0.05)
    return hits, peak, floor


for path in [p for p in (src, rec) if p]:
    v, luma = video_flash_times(path)
    a, peak, floor = audio_beep_times(path)
    print(f'\n=== {path} ===')
    print(f'video frames analysed, luma p50={sorted(luma)[len(luma)//2]:.1f} max={max(luma):.1f}')
    print(f'flashes: {len(v)} -> {[round(t, 3) for t in v[:14]]}')
    print(f'audio rms peak={peak:.0f} floor={floor:.0f}')
    print(f'beeps:   {len(a)} -> {[round(t, 3) for t in a[:14]]}')
    if v and a:
        # Match by nearest neighbour, then report the offset at the start and at the end.
        pairs = []
        for t in v:
            best = min(a, key=lambda x: abs(x - t))
            pairs.append((t, best, (best - t) * 1000))
        print('flash->beep offsets (ms):')
        for t, b, off in pairs[:6]:
            print(f'  t={t:7.3f}  beep={b:7.3f}  offset={off:+8.1f} ms')
        print('  ...')
        for t, b, off in pairs[-4:]:
            print(f'  t={t:7.3f}  beep={b:7.3f}  offset={off:+8.1f} ms')
        offs = [o for _, _, o in pairs]
        print(f'first offset={offs[0]:+.1f} ms  last offset={offs[-1]:+.1f} ms  '
              f'drift over the recording={offs[-1] - offs[0]:+.1f} ms')
