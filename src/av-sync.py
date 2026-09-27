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
import os

def pair_markers(flashes, beeps, max_gap=2.0):
    """Pair each flash with the nearest UNUSED beep, one-to-one.

    Matching by index paired unrelated events, and a plain nearest-neighbour search with no
    threshold will happily bind a flash to a beep seconds away. max_gap must be far larger than
    any real A/V offset and far smaller than the marker period, so a genuine pair is always
    accepted and an impossible one is always rejected.
    """
    used = set()
    pairs = []
    for t in sorted(flashes):
        candidates = [(abs(b - t), i, b) for i, b in enumerate(beeps) if i not in used]
        if not candidates:
            break
        gap, i, b = min(candidates)
        if gap > max_gap:
            continue
        used.add(i)
        pairs.append((t, b, (b - t) * 1000))
    return pairs


if os.environ.get('AV_SYNC_SELFTEST') == '1':
    # Known 50 ms shift on every pair. Any deviation means the pairing is broken.
    fl = [0.0, 5.0, 10.0, 15.0]
    be = [0.05, 5.05, 10.05, 15.05]
    pr = pair_markers(fl, be)
    assert len(pr) == 4, f'expected 4 pairs, got {len(pr)}'
    assert all(abs(o - 50.0) < 1e-6 for _, _, o in pr), pr
    # A flash 3 s from any beep must be rejected, not bound to the wrong marker.
    assert len(pair_markers([0.0, 3.0], be)) == 1, 'far flash was not rejected'
    assert len(pair_markers(fl, be, max_gap=0.1)) == 4, 'tight threshold dropped real pairs'
    # Unordered input must still pair correctly.
    assert pair_markers([15.0, 0.0, 10.0, 5.0], be) == pr, 'pairing depends on input order'
    print('av-sync pairing self-test: OK (4 pairs, +50.0 ms each, far pair rejected)')
    raise SystemExit(0)


rec = sys.argv[1]
src = sys.argv[2] if len(sys.argv) > 2 else None


def _frame_rate(path, frame_count):
    """Actual average frame rate, from the container. Falls back to 60 only if the probe fails."""
    out = subprocess.run(
        ['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries',
         'stream=avg_frame_rate', '-of', 'csv=p=0', path],
        capture_output=True, text=True).stdout.strip()
    if out and '/' in out:
        num, den = out.split('/')
        if float(den) > 0:
            return float(num) / float(den)
    # Last resort: frames divided by the stream duration we already know we read.
    dur = subprocess.run(
        ['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries',
         'stream=duration', '-of', 'csv=p=0', path],
        capture_output=True, text=True).stdout.strip()
    if dur and float(dur) > 0:
        return frame_count / float(dur)
    return 60.0


def frame_pts(path):
    """Presentation timestamp of every video frame, in the container's own timebase.

    A VFR capture has no single frame rate, so any estimate -- hardcoded, average, or
    frames/duration -- scales every measured time by a constant and silently reports the wrong
    offset. The PTS of each frame is already stored; read it and no approximation is needed.
    """
    out = subprocess.run(
        ['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-show_entries',
         'frame=pts_time', '-of', 'csv=p=0', path],
        capture_output=True, text=True).stdout
    pts = []
    for line in out.split():
        try:
            pts.append(float(line.strip().rstrip(',')))
        except ValueError:
            pass
    return pts


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
    # Frames come out of ffmpeg in presentation order, so frame i of the raw stream is frame i of
    # the probe listing. Guard the lengths rather than trust them.
    pts = frame_pts(path)
    if len(pts) < len(luma):
        pts = pts + [pts[-1] if pts else 0.0] * (len(luma) - len(pts))
    else:
        pts = pts[:len(luma)]
    # A flash spans several captured frames, and those frames are >1 apart in index, so an
    # index-based split shattered one flash into many. Split on TIME instead, using the real PTS.
    hits = []
    for i, v in enumerate(luma):
        if v > threshold and (not hits or pts[i] - pts[hits[-1]] > 0.5):
            hits.append(i)
    return [pts[i] for i in hits], luma


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
        # hits must hold INDICES here, like the video path does. It used to hold times
        # (i * 0.05), so this test compared a window index against a time in seconds; the
        # difference was always large, the merge never fired, and one 150 ms beep came back as
        # ~7 separate "beeps" 50 ms apart.
        if r > limit and (not hits or (i - hits[-1]) * 0.05 > 0.4):
            hits.append(i)
    return [i * 0.05 for i in hits], peak, floor


for path in [p for p in (src, rec) if p]:
    v, luma = video_flash_times(path)
    a, peak, floor = audio_beep_times(path)
    print(f'\n=== {path} ===')
    print(f'video frames analysed, luma p50={sorted(luma)[len(luma)//2]:.1f} max={max(luma):.1f}')
    print(f'flashes: {len(v)} -> {[round(t, 3) for t in v[:14]]}')
    print(f'audio rms peak={peak:.0f} floor={floor:.0f}')
    print(f'beeps:   {len(a)} -> {[round(t, 3) for t in a[:14]]}')
    if v and a:
        # One-to-one by time, with a gap threshold. Order-based matching paired unrelated events.
        pairs = pair_markers(v, a)
        print(f'pairs: {len(pairs)} of {len(v)} flashes / {len(a)} beeps')
        print('flash->beep offsets (ms):')
        for t, b, off in pairs[:6]:
            print(f'  t={t:7.3f}  beep={b:7.3f}  offset={off:+8.1f} ms')
        if len(pairs) > 6:
            print('  ...')
            for t, b, off in pairs[-4:]:
                print(f'  t={t:7.3f}  beep={b:7.3f}  offset={off:+8.1f} ms')
        offs = [o for _, _, o in pairs]
        if len(offs) >= 2:
            print(f'first offset={offs[0]:+.1f} ms  last offset={offs[-1]:+.1f} ms  '
                  f'drift over the recording={offs[-1] - offs[0]:+.1f} ms')
        else:
            print('fewer than 2 pairs: drift is not measurable')

