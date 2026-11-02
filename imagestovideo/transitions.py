"""Transitions between two frames. Each function takes the outgoing frame a, the incoming frame b
(uint8 arrays of shape HxWx3) and progress p in [0, 1], and returns the blended frame.

The catalogue mirrors the kinds of transitions found in the reference app (fade, fade to black, linear
wipe, push, cross zoom, round reveal, pixelate). The maths here is original; none of the reference
app's shaders were read or copied (they ship encrypted).
"""

import numpy as np
from PIL import Image


def _smooth(p):
    return p * p * (3 - 2 * p)


def _mix(a, b, w):
    """Blend with weight w (scalar or HxW mask) toward b."""
    if np.ndim(w) == 2:
        w = w[..., None]
    out = a.astype(np.float32) * (1 - w) + b.astype(np.float32) * w
    return out.clip(0, 255).astype(np.uint8)


def fade(a, b, p):
    return _mix(a, b, _smooth(p))


def fade_black(a, b, p):
    if p < 0.5:
        return _mix(a, np.zeros_like(a), _smooth(p * 2))
    return _mix(np.zeros_like(b), b, _smooth(p * 2 - 1))


def wipe(a, b, p, feather=0.06):
    """Soft-edged wipe from left to right."""
    w = a.shape[1]
    x = np.linspace(0, 1, w, dtype=np.float32)
    edge = _smooth(p) * (1 + feather) - feather
    mask = np.clip((edge + feather - x) / feather, 0, 1)
    return _mix(a, b, np.broadcast_to(mask, a.shape[:2]))


def push(a, b, p):
    """The incoming picture pushes the outgoing one off to the left."""
    w = a.shape[1]
    shift = int(round(_smooth(p) * w))
    out = np.empty_like(a)
    out[:, : w - shift] = a[:, shift:]
    out[:, w - shift:] = b[:, : shift]
    return out


def _zoom(frame, factor):
    """Scale about the centre by factor (>= 1), keeping the frame size."""
    h, w = frame.shape[:2]
    cw, ch = w / factor, h / factor
    box = ((w - cw) / 2, (h - ch) / 2, (w + cw) / 2, (h + ch) / 2)
    return np.asarray(Image.fromarray(frame).resize((w, h), Image.BILINEAR, box=box))


def cross_zoom(a, b, p, strength=0.35):
    """Outgoing frame rushes toward the viewer while the incoming one settles from the same zoom."""
    s = _smooth(p)
    za = _zoom(a, 1 + strength * s)
    zb = _zoom(b, 1 + strength * (1 - s))
    return _mix(za, zb, s)


def circle(a, b, p, feather=0.04):
    """Incoming picture revealed through a growing soft-edged circle from the centre."""
    h, w = a.shape[:2]
    yy, xx = np.ogrid[:h, :w]
    d = np.sqrt(((xx - w / 2) / h) ** 2 + ((yy - h / 2) / h) ** 2)  # radius in units of frame height
    radius = _smooth(p) * (np.hypot(w / h, 1) / 2 + feather)
    mask = np.clip((radius - d) / feather, 0, 1).astype(np.float32)
    return _mix(a, b, mask)


def pixelate(a, b, p, max_block=48):
    """Blocks grow on the way out, swap pictures at the midpoint, then shrink back."""
    h, w = a.shape[:2]
    src = a if p < 0.5 else b
    level = 1 - abs(p * 2 - 1)  # 0 -> 1 -> 0
    block = max(1, int(round(1 + level * (max_block - 1))))
    small = Image.fromarray(src).resize((max(1, w // block), max(1, h // block)), Image.BILINEAR)
    return np.asarray(small.resize((w, h), Image.NEAREST))


TRANSITIONS = {
    "fade": fade,
    "fade_black": fade_black,
    "wipe": wipe,
    "push": push,
    "cross_zoom": cross_zoom,
    "circle": circle,
    "pixelate": pixelate,
}
