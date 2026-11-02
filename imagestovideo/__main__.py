"""Command line: python -m imagestovideo PHOTOS... -o video.mp4"""

import argparse
import re
import time

from .panzoom import EASINGS, PRESET_NAMES
from .render import Settings, collect_images, render
from .transitions import TRANSITIONS


def size_arg(text):
    m = re.fullmatch(r"(\d+)[xX](\d+)", text)
    if not m:
        raise argparse.ArgumentTypeError("use WIDTHxHEIGHT, e.g. 1920x1080")
    w, h = int(m.group(1)), int(m.group(2))
    if w % 2 or h % 2:
        raise argparse.ArgumentTypeError("width and height must be even (H.264 requirement)")
    return w, h


def main(argv=None):
    ap = argparse.ArgumentParser(prog="imagestovideo", description="Turn a list of images into a beautiful video.")
    ap.add_argument("inputs", nargs="+", help="image files and/or folders of images")
    ap.add_argument("-o", "--output", default="slideshow.mp4", help="output video file (default: slideshow.mp4)")
    ap.add_argument("--size", type=size_arg, default=(1920, 1080), help="output size, default 1920x1080 (use 1080x1920 for vertical)")
    ap.add_argument("--fps", type=int, default=30)
    ap.add_argument("--duration", type=float, default=4.0, help="seconds per photo (default 4)")
    ap.add_argument("--transition", default="auto", choices=["auto", "random", "none", *TRANSITIONS],
                    help="transition between photos (default: auto, which cycles through several)")
    ap.add_argument("--transition-duration", type=float, default=1.0, help="seconds (default 1)")
    ap.add_argument("--motion", default="auto", choices=["auto", *PRESET_NAMES],
                    help="pan & zoom preset (default: auto, a different move for each photo)")
    ap.add_argument("--zoom", type=float, default=0.15, help="how far pan & zoom moves in, 0-0.5 (default 0.15)")
    ap.add_argument("--easing", default="linear", choices=list(EASINGS))
    ap.add_argument("--fit", default="auto", choices=["auto", "cover", "blur"],
                    help="cover crops to fill; blur shows the whole photo on a blurred background; auto picks per photo")
    ap.add_argument("--audio", help="optional music file; fades in and out and is trimmed to the video")
    ap.add_argument("--order", default="name", choices=["name", "date", "given"], help="photo order (default: name)")
    ap.add_argument("--crf", type=int, default=18, help="x264 quality, lower is better (default 18)")
    ap.add_argument("--seed", type=int, help="seed for --transition random")
    a = ap.parse_args(argv)

    if not 0 <= a.zoom <= 0.5:
        ap.error("--zoom must be between 0 and 0.5")
    files = collect_images(a.inputs, a.order)
    if not files:
        ap.error("no images found in the given files or folders")
    s = Settings(size=a.size, fps=a.fps, duration=a.duration, transition=a.transition,
                 transition_duration=a.transition_duration, motion=a.motion, zoom=a.zoom, easing=a.easing,
                 fit=a.fit, audio=a.audio, crf=a.crf, seed=a.seed, order=a.order)
    print(f"{len(files)} photos -> {a.output} ({s.size[0]}x{s.size[1]} @ {s.fps} fps)")
    t0 = time.time()
    info = render(files, a.output, s)
    print(f"done: {info['seconds']} s of video in {time.time() - t0:.1f} s")


if __name__ == "__main__":
    main()
