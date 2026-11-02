"""ImagesToVideo: turn a list of images into a smooth slideshow video with pan & zoom and transitions."""

from .render import Settings, collect_images, render

__all__ = ["Settings", "collect_images", "render"]
__version__ = "1.0.0"
