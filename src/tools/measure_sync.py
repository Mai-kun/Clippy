#!/usr/bin/env python3
"""
Measure A/V sync of a Clippy export against a known marker source.

The source (avsrc.mp4) carries a full-screen white flash and a 1 kHz beep, both on the
same 5 s grid, so each detected pair gives an offset in ms and the difference between the
first and last pair shows drift.

Method notes, because getting these wrong is what made an earlier attempt useless:

  * Times come from the container's own per-frame PTS (ffprobe), never frame_index / fps.
    These exports are VFR, so any frame-rate estimate scales every number by a constant.
  * Flashes are grouped as RUNS OF ADJACENT bright frames, not by a time threshold. A 150 ms
    flash spans several captured frames at low frame rates, so a fixed merge window either
    shatters one flash into several (at low fps) or fuses two of them. Contiguity in frame
    index needs no magic number.

Usage:  python measure_sync.py <clip.mp4> [<other.mp4> ...]
"""

import json
import math
import struct
import subprocess
import sys

WHITE = 200.0
CENTRE = True
FLASH_MERGE_PTS = 1.0


def frame_pts(path):
    """PTS of every video frame, in seconds, in presentation order."""
    out = subprocess.run(
        ['ffprobe', '-v', 'error', '-select_streams', 'v:0',
         '-show_entries', 'frame=pts_time', '-of', 'csv=p=0', path],
        capture_output=True, text=True).stdout
    pts = []
    for line in out.split():
        try:
            pts.append(float(line.strip().rstrip(',')))
        except ValueError:
            pass
    return pts


def frame_count(path):
    out = subprocess.run(
        ['ffprobe', '-v', 'error', '-select_streams', 'v:0', '-count_frames',
         '-show_entries', 'stream=nb_read_frames', '-of', 'csv=p=0', path],
        capture_output=True, text=True).stdout.strip()
    try:
        return int(out)
    except ValueError:
        return -1


def luma_series(path):
    """Mean luma of each frame, downscaled, from a straight decode.

    -fps_mode passthrough is essential. Without it ffmpeg resamples the VFR stream to a constant
    frame rate and drops or duplicates frames, so the luma list silently stops lining up with the
    PTS list and every timestamp taken from it is wrong (measured: 995 frames decoded against
    1597 in the container, which is why the first run reported untrustworthy timings).
    """
    vf = 'crop=iw/5:ih/5:iw*2/5:ih*2/5,scale=32:18,format=gray' if CENTRE else 'scale=32:18,format=gray'
    raw = subprocess.run(
        ['ffmpeg', '-v', 'error', '-i', path, '-fps_mode', 'passthrough', '-vf', vf,
         '-f', 'rawvideo', '-pix_fmt', 'gray', '-'],
        capture_output=True).stdout
    n = 32 * 18
    return [sum(raw[i:i + n]) / n for i in range(0, len(raw) - n + 1, n)]


def flash_times(pts, luma, threshold=WHITE):
    """Start time of each white flash, found as runs of adjacent bright frames."""
    times, run_start = [], None
    for i, v in enumerate(luma):
        if v > threshold:
            if run_start is None:
                run_start = i
        elif run_start is not None:
            times.append(pts[run_start] if run_start < len(pts) else float('nan'))
            run_start = None
    if run_start is not None:
        times.append(pts[run_start] if run_start < len(pts) else float('nan'))

    merged = []
    for t in times:
        if merged and (t - merged[-1]) < FLASH_MERGE_PTS:
            continue
        merged.append(t)
    return merged


def beep_times(path, win_ms=50.0, ratio=0.35):
    """Start time of each audio burst, from the RMS envelope."""
    raw = subprocess.run(
        ['ffmpeg', '-v', 'error', '-i', path, '-ac', '1', '-ar', '8000', '-f', 's16le', '-'],
        capture_output=True).stdout
    samples = struct.unpack('<%dh' % (len(raw) // 2), raw)
    win = int(8000 * win_ms / 1000)
    if win <= 0 or len(samples) < win:
        return [], 0.0, 0.0
    rms = [math.sqrt(sum(s * s for s in samples[i:i + win]) / win)
           for i in range(0, len(samples) - win, win)]
    peak = max(rms) if rms else 1.0
    floor = sorted(rms)[len(rms) // 10] if rms else 0.0
    limit = max(floor * 3.0, peak * ratio)

    step = win_ms / 1000.0
    bursts, run_start = [], None
    for i, r in enumerate(rms):
        if r > limit:
            if run_start is None:
                run_start = i
        elif run_start is not None:
            bursts.append(run_start * step)
            run_start = None
    if run_start is not None:
        bursts.append(run_start * step)
    return bursts, peak, floor


def pair(flashes, beeps, max_gap=2.0):
    """Match each flash with the nearest UNUSED beep, one-to-one.

    Order-based matching bound unrelated events, and an unbounded nearest-neighbour search will
    happily bind a flash to a beep seconds away. max_gap is far larger than any real A/V offset
    and far smaller than the 5 s marker period.
    """
    used, out = set(), []
    for t in sorted(flashes):
        cands = [(abs(b - t), i, b) for i, b in enumerate(beeps) if i not in used]
        if not cands:
            break
        gap, i, b = min(cands)
        if gap > max_gap:
            continue
        used.add(i)
        out.append((t, b, (b - t) * 1000.0))
    return out


def analyse(path):
    print(f'=== {path} ===')
    pts = frame_pts(path)
    declared = frame_count(path)
    luma = luma_series(path)
    print(f'frames decoded by ffmpeg : {len(luma)}')
    print(f'frames counted by ffprobe: {declared}')
    print(f'pts entries              : {len(pts)}')
    if len(pts) != len(luma):
        print(f'  PTS/FRAMES DIFFER by {len(luma) - len(pts)} -- timings untrustworthy')
    else:
        print('  PTS and frame count match exactly')

    flashes = flash_times(pts, luma)
    beeps, peak, floor = beep_times(path)
    print(f'audio peak={peak:.0f} floor={floor:.0f}')
    print(f'flashes ({len(flashes)}): {[round(t, 3) for t in flashes[:12]]}')
    print(f'beeps   ({len(beeps)}): {[round(t, 3) for t in beeps[:12]]}')

    if len(flashes) > 1:
        d = [round(flashes[i] - flashes[i - 1], 3) for i in range(1, len(flashes))]
        print(f'flash spacing: {d[:11]}')
    if len(beeps) > 1:
        d = [round(beeps[i] - beeps[i - 1], 3) for i in range(1, len(beeps))]
        print(f'beep   spacing: {d[:11]}')

    pairs = pair(flashes, beeps)
    print(f'pairs: {len(pairs)} of {len(flashes)} flashes / {len(beeps)} beeps')
    for t, b, off in pairs:
        print(f'  flash={t:8.3f}  beep={b:8.3f}  offset={off:+8.1f} ms')
    offs = [o for _, _, o in pairs]
    if offs:
        print(f'offsets_ms: {[round(o, 1) for o in offs]}')
        print(f'mean offset: {sum(offs) / len(offs):+.1f} ms')
    if len(offs) >= 2:
        print(f'first={offs[0]:+.1f} last={offs[-1]:+.1f} drift={offs[-1] - offs[0]:+.1f} ms')
    else:
        print('fewer than 2 pairs: offset and drift are NOT measurable from this file')
    return pairs


if __name__ == '__main__':
    if len(sys.argv) < 2:
        print(__doc__)
        sys.exit(2)
    result = {p: analyse(p) for p in sys.argv[1:]}
    print()
    print(json.dumps({p: [round(o, 1) for _, _, o in v] for p, v in result.items()}))
