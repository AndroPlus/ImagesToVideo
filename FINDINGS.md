# Reference app: REA findings (clean-room notes)

This investigation was done by Claude (in [Claude Code](https://claude.com/claude-code)) using the
[REA MCP server](https://github.com/morluto/rea) and its `reverse-engineer-anything` skill. It looked at a
photo-to-video feature in an installed desktop video editor, **Wondershare Filmora 13.5.1.7566**, before
anything in this repo was written.

No code, shaders, presets or assets from Filmora were copied. These notes record the *behaviour* that was
observed, and ImagesToVideo implements its own version of that behaviour.

## How it was investigated

| Step | Tooling | Result |
|---|---|---|
| Find candidate apps | Windows package list and the uninstall registry | Photos-to-video apps installed: CapCut 9.4, Filmora 13.5 and Windows Photos. Filmora was chosen because it ships its pan & zoom and transition presets as separate files. |
| Locate the feature | File inventory of the install folder | `configs/PanZoom/*.conf`, `configs/PanZoom/default_setting.json` and `resources/wfx_effect/Transition/*/` |
| Find the native owner | String inventory of the DLLs that mention "PanZoom" | `FFCropAndZoom.dll` exports Qt classes such as `FFCropAndZoomManager::SetCropAndZoomEffect`, `ResetCropAndZoomEffect` and `FFCropAndZoomPanel` |
| Decompile it | REA `open_binary` / native providers | **Not available.** REA's native decompilers (Ghidra, IDA, Kuna) weren't configured on this machine, and Hopper is macOS-only. REA reported this through `binary_session`, and `open_binary` doesn't accept folders. The conclusions below therefore come from the shipped data files, not decompiled code. |

## Observations

### Pan & Zoom (the "Ken Burns" effect)

`default_setting.json` lists six presets: *Zoom to bottom right*, *Zoom in*, *Zoom out*, *Pan Right*, *Pan
Left* and *Zoom Original*. Each `.conf` holds four keyframe tracks joined by a separator. Their values line up
with a **crop rectangle (x, y, width, height) measured as fractions of the picture**, keyframed at the
start (t = 0) and the end (t = 1) of the clip. The times are in 100-ns ticks with 10,000,000 meaning the
whole clip, and every keyframe uses the same interpolation mode (1).

| Preset | Start (x, y, w, h) | End (x, y, w, h) |
|---|---|---|
| Zoom in | 0, 0, 1, 1 | 0.25, 0.25, 0.5, 0.5 |
| Zoom out | 0.25, 0.25, 0.5, 0.5 | 0, 0, 1, 1 |
| Pan left | 0.5, 0.25, 0.5, 0.5 | 0, 0.25, 0.5, 0.5 |
| Pan right | 0, 0.25, 0.5, 0.5 | 0.5, 0.25, 0.5, 0.5 |
| Zoom to bottom right | 0, 0, 1, 1 | 0.5, 0.5, 0.5, 0.5 |
| Zoom original | 0, 0, 1, 1 | 0, 0, 1, 1 |

So the reference presets zoom to 2x (a half-size rectangle) over one clip.

### Transitions

Each transition is a folder with an OpenGL fragment shader, an OpenCL kernel and a small manifest
(`GUID`, `OPENGL`, `OPENCL`, `TRANSITION: 1`, `ENCRYPT: "1"`). The shaders are **encrypted**, and they were
not read. The catalogue names are: fade, fade2, fade_black, linear wipe, push, CrossZoom, DoorWay,
Round_zoom_out, Pixelate, Pinwheel, Roll_clockwise, Skew_right_split, Twirl, Whirl 1/2, drop, erase and
Evaporate 1.

## What ImagesToVideo does with this

| Observation | ImagesToVideo design choice |
|---|---|
| Motion is a keyframed crop rectangle | `panzoom.py` uses the same model: `preset_rects()` returns (start, end) rectangles, and each frame resamples that rectangle with a sub-pixel box |
| Six presets with these directions | The same six shapes (`zoom_in`, `zoom_out`, `pan_left`, `pan_right`, `zoom_to_bottom_right`, `none`). `--motion auto` cycles through them so consecutive photos move differently |
| 2x zoom per clip | Too strong for a slideshow, so the amount is the `--zoom` setting (default 0.15, i.e. a 15% crop). `--zoom 0.5` reproduces the reference 2x |
| Linear-looking interpolation | Default `--easing linear`, plus `smooth` and `sine` options |
| GPU shader transitions | Seven transitions written from scratch in numpy: fade, fade_black, wipe, push, cross_zoom, circle and pixelate |
| (Not observed; an added design choice) | `--fit auto` puts portrait photos on a blurred background instead of cropping heads off, and `--audio` adds music with fades |

## Windows app

The reference app is native (C++/Qt), so the repo also ships a native Windows app in `windows/` (C# / WPF on
.NET 9). Its `Engine/` is a 1:1 port of the Python engine. It uses the same crop-rectangle pan & zoom presets and
zoom setting, the same seven transitions with the same maths, and the same timeline (photos overlap by the
transition length, and motion runs through the transitions). Rendered frames match the Python output. Both
stream raw frames into FFmpeg for H.264 encoding.
