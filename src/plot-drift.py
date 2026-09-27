# Plots PTS - captureInstant for every frame of a run, and answers flat-vs-growing.
#   python plot-drift.py <clip.mp4>
import csv
import subprocess
import sys

video = sys.argv[1]
timing = video.rsplit('.', 1)[0] + '.timing.csv'

out = subprocess.run(
    ['ffprobe', '-v', 'error', '-select_streams', 'v:0',
     '-show_entries', 'frame=pts_time', '-of', 'csv=p=0', video],
    capture_output=True, text=True, check=True).stdout.split()
pts = [float(x) for x in out if x.strip()]

with open(timing, newline='') as fh:
    rows = list(csv.DictReader(fh))

n = min(len(pts), len(rows))
print(f'captured={len(rows)}  encoded={len(pts)}  plotted={n}')
if len(rows) != len(pts):
    print('WARNING: count mismatch, frames were lost')

# Normalise both series to their first frame; ffmpeg's PTS starts at its own zero.
sw0 = float(rows[0]['swCaptureMs'])
pts0 = pts[0]
delta = [(pts[i] - (float(rows[i]['swCaptureMs']) - sw0) / 1000) * 1000 for i in range(n)]

lo, hi = min(delta), max(delta)
print(f'delta ms: min={lo:.2f} max={hi:.2f} first={delta[0]:.2f} last={delta[-1]:.2f}')
span = hi - lo
print(f'peak-to-peak = {span:.2f} ms')

# Flat or growing: compare the first tenth against the last tenth, and fit a slope.
k = max(1, n // 10)
head = sum(delta[:k]) / k
tail = sum(delta[-k:]) / k
print(f'mean first 10% = {head:.2f} ms   mean last 10% = {tail:.2f} ms   growth = {tail - head:+.2f} ms')

xs = [i for i in range(n)]
mx, my = sum(xs) / n, sum(delta) / n
num = sum((xs[i] - mx) * (delta[i] - my) for i in range(n))
den = sum((xs[i] - mx) ** 2 for i in range(n))
slope = num / den if den else 0.0
print(f'linear fit slope = {slope * 1000:+.2f} us/frame  ({slope * 60 * 1000:+.0f} us/min)')

# ASCII plot, x = frame, y = delta.
W, H = 110, 24
print(f'\nPTS - captureInstant (ms), y axis {lo:.1f} .. {hi:.1f}, x axis frame 1 .. {n}')
for row in range(H):
    y = hi - (row + 0.5) * span / H
    cells = []
    for c in range(W):
        lo_i = c * n // W
        hi_i = max(lo_i + 1, (c + 1) * n // W)
        v = sum(delta[lo_i:hi_i]) / (hi_i - lo_i)
        cells.append('#' if abs(v - y) < span / H else ('+' if abs(v - y) < span / H * 1.5 else ' '))
    print(f'{y:8.1f} |{"".join(cells)}|')

# Per-second mean, to see structure inside the band.
print('\nper-second mean delta (s: ms):')
for s in range(0, n // 44 + 1):
    a = s * 44
    b = min(n, a + 44)
    if a >= b:
        break
    print(f'  {s:3d}s: {sum(delta[a:b]) / (b - a):7.2f}')
