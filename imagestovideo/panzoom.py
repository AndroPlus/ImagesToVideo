"""Pan & Zoom ("Ken Burns") motion.

Model (from the REA investigation, see FINDINGS.md): a preset is a crop rectangle (x, y, w, h), in
fractions of the picture, keyframed at the start and the end of the clip. Each frame shows that rectangle
scaled to the output size. The presets below keep the same shapes and directions, scaled by a
configurable zoom amount instead of the fixed 2x the reference app uses.
"""

import math

from PIL import Image, ImageFilter, ImageOps

PRESET_NAMES = ("zoom_in", "zoom_out", "pan_left", "pan_right", "zoom_to_bottom_right", "none")
# Order used by --motion auto, so consecutive photos move differently.
AUTO_CYCLE = ("zoom_in", "pan_right", "zoom_out", "pan_left", "zoom_to_bottom_right")


def preset_rects(name, z):
    """(start, end) rectangles for a preset, with z = fraction of the picture cropped away at full zoom."""
    full, s = (0.0, 0.0, 1.0, 1.0), 1.0 - z
    centre = (z / 2, z / 2, s, s)
    return {
        "zoom_in": (full, centre),
        "zoom_out": (centre, full),
        "pan_left": ((z, z / 2, s, s), (0.0, z / 2, s, s)),
        "pan_right": ((0.0, z / 2, s, s), (z, z / 2, s, s)),
        "zoom_to_bottom_right": (full, (z, z, s, s)),
        "none": (full, full),
    }[name]


EASINGS = {
    "linear": lambda t: t,
    "smooth": lambda t: t * t * (3 - 2 * t),
    "sine": lambda t: 0.5 - 0.5 * math.cos(math.pi * t),
}


def fit_to_canvas(img, size, fit):
    """Return an RGB image with exactly the output aspect ratio.

    cover: crop to fill. blur: show the whole picture over a blurred, darkened copy of itself.
    auto: cover unless the aspect ratios differ by more than 25% (portrait photos in a landscape video).
    """
    img = ImageOps.exif_transpose(img).convert("RGB")
    w, h = size
    target, actual = w / h, img.width / img.height
    if fit == "auto":
        fit = "blur" if max(target, actual) / min(target, actual) > 1.25 else "cover"
    if fit == "cover":
        return ImageOps.fit(img, size, Image.LANCZOS, centering=(0.5, 0.45))
    bg = ImageOps.fit(img, (max(w // 8, 1), max(h // 8, 1)), Image.BILINEAR)
    bg = bg.filter(ImageFilter.GaussianBlur(6)).resize(size, Image.BILINEAR)
    bg = Image.eval(bg, lambda v: int(v * 0.55))
    fg = ImageOps.contain(img, size, Image.LANCZOS)
    bg.paste(fg, ((w - fg.width) // 2, (h - fg.height) // 2))
    return bg


class MovingPhoto:
    """One photo prepared for animation: a canvas large enough that the most zoomed-in rectangle still
    has at least output resolution, so motion stays sharp."""

    def __init__(self, path, out_size, preset, zoom, easing, fit):
        self.out_size = out_size
        self.start, self.end = preset_rects(preset, zoom)
        self.ease = EASINGS[easing]
        scale = 1.0 / (1.0 - zoom) if preset != "none" else 1.0
        canvas_size = (math.ceil(out_size[0] * scale), math.ceil(out_size[1] * scale))
        with Image.open(path) as img:
            self.canvas = fit_to_canvas(img, canvas_size, fit)

    def frame(self, t):
        """Frame at normalised clip time t in [0, 1]."""
        k = self.ease(min(max(t, 0.0), 1.0))
        x, y, w, h = (a + (b - a) * k for a, b in zip(self.start, self.end))
        cw, ch = self.canvas.size
        box = (x * cw, y * ch, (x + w) * cw, (y + h) * ch)
        # A float box gives sub-pixel positioning, which keeps slow pans from stepping.
        return self.canvas.resize(self.out_size, Image.BICUBIC, box=box)
