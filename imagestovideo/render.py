"""Timeline and encoding: lays photos out with overlapping transitions and streams frames to FFmpeg."""

import random
import shutil
import subprocess
import sys
from dataclasses import dataclass, field
from pathlib import Path

import numpy as np

from .panzoom import AUTO_CYCLE, PRESET_NAMES, MovingPhoto
from .transitions import TRANSITIONS

IMAGE_EXTS = {".jpg", ".jpeg", ".png", ".webp", ".bmp", ".tif", ".tiff", ".gif"}


@dataclass
class Settings:
    size: tuple = (1920, 1080)
    fps: int = 30
    duration: float = 4.0            # seconds each photo is on screen, including its transitions
    transition_duration: float = 1.0
    transition: str = "auto"         # a name from TRANSITIONS, "auto" (cycle), "random" or "none"
    motion: str = "auto"             # a name from PRESET_NAMES or "auto"
    zoom: float = 0.15               # fraction of the picture cropped away at full zoom
    easing: str = "linear"
    fit: str = "auto"
    audio: str = None
    crf: int = 18
    seed: int = None
    order: str = "name"
    transitions_cycle: list = field(default_factory=lambda: ["fade", "cross_zoom", "wipe", "circle", "push", "fade_black"])


def collect_images(inputs, order="name"):
    files = []
    for item in inputs:
        p = Path(item)
        if p.is_dir():
            files += [f for f in p.iterdir() if f.suffix.lower() in IMAGE_EXTS]
        elif p.suffix.lower() in IMAGE_EXTS and p.exists():
            files.append(p)
        else:
            print(f"skipping {item}: not an image or folder", file=sys.stderr)
    if order == "date":
        files.sort(key=lambda f: f.stat().st_mtime)
    elif order == "name":
        files.sort(key=lambda f: f.name.lower())
    return files


def plan(files, s):
    """Pick a motion preset for every photo and a transition for every boundary."""
    rng = random.Random(s.seed)
    motions = [AUTO_CYCLE[i % len(AUTO_CYCLE)] if s.motion == "auto" else s.motion for i in range(len(files))]
    names = []
    for i in range(max(len(files) - 1, 0)):
        if s.transition == "auto":
            names.append(s.transitions_cycle[i % len(s.transitions_cycle)])
        elif s.transition == "random":
            names.append(rng.choice(list(TRANSITIONS)))
        else:
            names.append(s.transition)
    return motions, names


def ffmpeg_command(out, s, total_seconds):
    ff = shutil.which("ffmpeg")
    if not ff:
        raise SystemExit("ffmpeg was not found on PATH. Install it (e.g. `winget install Gyan.FFmpeg`).")
    w, h = s.size
    cmd = [ff, "-hide_banner", "-loglevel", "error", "-y",
           "-f", "rawvideo", "-pix_fmt", "rgb24", "-s", f"{w}x{h}", "-r", str(s.fps), "-i", "-"]
    if s.audio:
        fade_start = max(0.0, total_seconds - 2.0)
        cmd += ["-i", s.audio, "-map", "0:v", "-map", "1:a", "-c:a", "aac", "-b:a", "192k",
                "-af", f"afade=t=in:d=0.5,afade=t=out:st={fade_start:.2f}:d=2", "-shortest"]
    cmd += ["-c:v", "libx264", "-preset", "medium", "-crf", str(s.crf), "-pix_fmt", "yuv420p",
            "-movflags", "+faststart", str(out)]
    return cmd


def render(files, out, s, progress=True):
    if not files:
        raise SystemExit("No images found.")
    if s.transition == "none" or len(files) == 1:
        s.transition_duration = 0.0
    if s.transition_duration * 2 > s.duration:
        raise SystemExit("--transition-duration must be at most half of --duration.")
    motions, trans = plan(files, s)
    step = s.duration - s.transition_duration          # time between the starts of consecutive photos
    total = step * (len(files) - 1) + s.duration
    n_frames = int(round(total * s.fps))

    cache = {}

    def photo(i):
        if i not in cache:
            for k in [k for k in cache if k < i - 1]:  # only the current and next photo are needed
                del cache[k]
            cache[i] = MovingPhoto(files[i], s.size, motions[i], s.zoom, s.easing, s.fit)
        return cache[i]

    def clip_frame(i, t):
        return np.asarray(photo(i).frame((t - i * step) / s.duration))

    proc = subprocess.Popen(ffmpeg_command(out, s, total), stdin=subprocess.PIPE)
    try:
        for f in range(n_frames):
            t = f / s.fps
            i = min(int(t // step), len(files) - 1) if step > 0 else 0
            # Photo i starts at i*step; its first transition_duration seconds overlap the end of photo i-1.
            into = t - i * step
            if i > 0 and into < s.transition_duration:
                frame = TRANSITIONS[trans[i - 1]](clip_frame(i - 1, t), clip_frame(i, t), into / s.transition_duration)
            else:
                frame = clip_frame(i, t)
            proc.stdin.write(np.ascontiguousarray(frame, dtype=np.uint8).tobytes())
            if progress and (f % s.fps == 0 or f == n_frames - 1):
                print(f"\rrendering {f + 1}/{n_frames} frames ({100 * (f + 1) // n_frames}%)", end="", flush=True)
    finally:
        proc.stdin.close()
        code = proc.wait()
    if progress:
        print()
    if code != 0:
        raise SystemExit(f"ffmpeg failed with exit code {code}")
    return {"photos": len(files), "seconds": round(total, 2), "frames": n_frames,
            "motions": motions, "transitions": trans}


__all__ = ["Settings", "collect_images", "render", "PRESET_NAMES", "TRANSITIONS"]
