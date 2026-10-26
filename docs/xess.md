# XeSS on Intel Arc (Windows)

Status, findings and plan for Intel XeSS in the Windows port, measured on a Core Ultra X7
358H with an Arc B390 iGPU (shared LPDDR5X). Implementation details of the XeSS integration
itself are in [upscaler.md](upscaler.md#intel-xess-2026-10-18). Wider Arc performance work is
in [intel_opt.md](intel_opt.md).

## What the port uses

| Part of Intel's SDK | API | In the port |
|---|---|---|
| XeSS Super Resolution (XeSS-SR, the XeSS 2 upscaler) | D3D12, D3D11, **Vulkan** | **Yes**: `vk_xess.{h,cpp}`, `upscaler=xess` |
| XeSS Frame Generation (XeSS-FG) | **D3D12 only** | No (see [Frame generation](#frame-generation-later)) |
| Xe Low Latency (XeLL) | **D3D12 only** | No |

- SDK: XeSS SDK **3.0.2** (`tools/fetch_xess_sdk.ps1`, checksum-pinned, installed into the
  gitignored `gpu/third_party/xess-sdk/`). Its upscaling library is `libxess.dll`
  **2.0.2**, which the game log reports as "XeSS 2.0.2". That is the version of the SR
  library, not of the SDK.
- Inputs:
  - colour: HDR scene colour before the game's post-processing at Native AA, or the
    tonemapped frame on scaled presets;
  - depth;
  - motion vectors at render size, previous minus current, in pixels, without jitter;
  - Halton jitter.
- The HUD is composed after XeSS and never enters its history.

## How to use it (1080p output)

1. In the launcher, choose **XeSS** and a preset (Quality = 1.5x, i.e. 1280x720 for 1080p).
2. Check the game log for:
   - `Output 1920x1080: scene 1280x720` (the game itself renders at the reduced size);
   - `Upscaler: XeSS 2.0.2, <quality>, 1920x1080 output, ...`.
3. Changing the preset in the in-game menu needs a restart (the menu offers it). The restart
   recomputes the scene size (`BB_AUTO_RENDER_RES=1`).

`live_resolution=1` in `bbport.ini` (or `BB_LIVE_RES=1`) brings back live preset changes,
but that path is slower than native on Arc (below).

## Findings (2026-10-26)

All measurements are from the same heavy view (the demanding-area save checkpoint), with
scripted launches on mains power. The numbers are per launch (two 5 s windows each).
Launch-to-launch variance is large on this machine, so compare runs taken close together.

### 1. On Windows, XeSS never got a reduced-resolution game (fixed)

The port has two ways to render a scaled preset:

- **Startup patch:** the game's render size is patched at launch, so every pass runs at
  e.g. 1280x720 and XeSS fills 1920x1080.
- **Live path:** the game keeps its 1920x1080 buffers. Only scene draws go to reduced
  "proxy" images, which are resolved back to 1080p for the game's own compute and
  post-processing passes.

Two problems meant Windows always used the live path:

- **`run.bat` bug.** The size query
  `for /f ... in ('"%PYTHON%" scripts\patches.py --print-scaled --settings "%BB_CONFIG%" 2^>nul')`
  contains two quoted paths. cmd strips the first and last quote of such a command, so the
  query failed silently and returned nothing. This affected every output size: 1440p and 4K
  presets also ran on the live path. Fixed by an outer pair of quotes, with the redirect
  outside them:
  `('""%PYTHON%" ... "%BB_CONFIG%"" 2^>nul')`.
- **1080p was excluded by design** (`patches.py scaled_sizes` returned nothing for 1080p
  output). A reducing preset at 1080p is now patched like any other output. Native 1080p,
  TAA and "upscaler off" are not patched. `--print-scaled` now honours the launcher's
  `BB_UPSCALER` / `BB_UPSCALE_PRESET` overrides, so turning the upscaler off in the
  launcher can never leave a reduced scene without an upscaler.

Result, XeSS Quality at 1080p output:

| | FPS | GPU ms/frame | scene draws at reduced size |
|---|---|---|---|
| native, no upscaler | 46.9 | 21.3 | — |
| XeSS, live path (before) | 40.9 | 24.5 | 56 of 134 |
| **XeSS, startup patch (now)** | **57.1** | **17.5** | all |

On the live path, XeSS was slower than native. Post-processing and the game's compute passes
stayed at 1080p and paid for proxy resolves (about 1.4 ms per frame for scene colour alone).
The extra resample also washed out the image (lower contrast, haze).

The XeSS dispatch itself costs about **2.4-2.7 ms per frame** (1280x720 → 1920x1080).

### 2. Jitter sign: correct as it is

Flipping the jitter sign live (toggle bit 26, `BB_TOGGLE_FILE`) while standing still gives a
visibly blurrier and hazier image, so the default convention is right. The frame mean rises
from 35.1 to 38.4; native is 34.8. A Laplacian "sharpness" number is not comparable between
native and any temporal AA: native's dithering and grass shimmer inflate it about 10x.

### 3. Ghosting when moving: no motion vectors for characters (open)

- **The setting is off.** `object_motion` defaults to off in `bbport.ini`, and the
  launcher's "Object motion vectors" switch can only force it off (`BB_OBJECT_MOTION=0`),
  never on.
- **Effect.** Without object motion, every pixel gets camera motion only. When the camera
  orbits the player, the character stays put on screen, but the vectors say it moves.
  XeSS then blends stale history and leaves trails or copies of the character (three
  copies in a pan capture).
- **Why it stays off.** With object motion on, the G-buffer costs far more on Arc. Work on
  10-26 (below) cut the overhead roughly in half, but not yet enough for a default.

#### Object motion cost on Arc (10-26)

Same view, XeSS Quality (720p scene), `BB_PIPELINE_CACHE=0`, consecutive launches:

| | FPS | GPU ms/frame | pass begins/frame |
|---|---|---|---|
| object motion off | 40.9 | 24.5 | 212 |
| on, old code | 22.8 | 43.8 | 223 |
| **on, new code** | **29.2** | **34.3** | 214 |

What was measured, and what changed:

- **Not render-pass splits.** Motion and non-motion G-buffer draws do use different
  attachment sets, but splits stayed about 37 per frame with object motion on (8-9 more
  pass begins, about 27 more attachment reloads). That is worth about 1-2 ms, not the
  ~19 ms overhead. It is still fixed: with `VK_EXT_dynamic_rendering_unused_attachments`
  (supported by the Arc driver), G-buffer pipelines that do not write motion declare the
  motion slot with an undefined format, and the rasterizer binds the motion target for them
  too (`ObjectMotion::UniformAttachment`). `BB_MOTION_UNIFORM=0` restores the old
  behaviour.
- **Per-vertex atomics: the main cost, fixed.** The motion vertex shader stored each clip
  position with four device-scope `OpAtomicExchange`s, about 8.5 M atomics per frame here,
  all going to shared memory. Repeated invocations of one indexed vertex write identical
  values, so it is now one aligned 16-byte store (`emit_spirv_special.cpp`). That cut about
  9.5 ms per frame. `BB_MOTION_ATOMIC=1` restores the atomics.
- **Remaining overhead: the motion pipeline variants themselves.** About 5 ms per frame,
  more in "slow" launches. Turning the history writes off live (toggle bit 29) changes
  nothing. The extra time sits in two shader pairs: `vs 9da6aacb` + `ps bb10e440`
  (0.7 → 3.9 ms) and `vs c829ee58` + `ps dbb222eb` (0.3 → 2.4 ms). Every covered pixel also
  writes a 16-byte RGBA32F motion value. The format cannot simply shrink: the compose pass
  compares the stored depth (alpha) with the scene depth at a tolerance of about 2e-7.
- **Cache:** the store change alters the motion shaders, so `PipelineKeyVersion` is now 6.
  The first launch clears the pipeline cache ("296 stale pipelines ... cleared (1059 files)")
  and rebuilds it during play.

Next ideas for the remaining cost:

- write motion only from the variant's real draws, and use an R32 depth plus RG16F motion
  pair instead of RGBA32F;
- check whether the motion variant disables early depth testing or vertex reuse on Intel
  (pipeline statistics, `BB_GPU_PROFILE=3`);
- gate the large skeletons by bone-palette change, like the small ones, so standing
  characters skip the motion variant.

### 4. Residual softness and haze with the startup patch (open)

The image is clean but slightly softer and hazier than native. To investigate:

- exposure handling of the HDR input (`exposureScale`, auto-exposure);
- which post effects run at 720p (DOF, bloom);
- the RCAS sharpening default (0.30).

### 5. Start-up reliability (open, not XeSS-specific)

About 3 of 25 scripted launches on 10-26 failed:

- a black screen right after `pad opened`, with no frames presented;
- or guest fault `0x20b56d2` at the title. The same fault is in crash logs from 10-09.

The scratchpad `session.sh` detects a title hang, saves the log and per-thread CPU, and
retries.

## Plan

1. **Cheap object motion vectors** (in progress). Done: uniform attachment set, and plain
   stores instead of atomics (overhead about 19 → 5-10 ms). Next: the remaining
   variant cost (ideas above). Then enable object motion by default whenever a temporal
   upscaler is active, and give the launcher switch an "on" state. Until then it can be
   switched on in the in-game menu ("Character motion vectors"; needs a restart).
2. **Haze and softness:** dump XeSS inputs and output (`BB_DUMP_TRIGGER`) at the startup
   patch and compare them with native. Check exposure, then sharpening.
3. **Start-up hang/crash:** reproduce with the hang detector, then compare thread states.
4. **Frame generation:** see below.

## Frame generation (later)

XeSS-FG and XeLL ship only for D3D12 in SDK 3.0.2 (`xefg_swapchain_d3d12.h`,
`xell_d3d12.h`; not installed by `fetch_xess_sdk.ps1`, which only takes the SR parts).
Intel's driver-level frame generation options are understood to act on games that already
integrate XeSS (not verified here), so the port needs its own integration. A route that keeps
the Vulkan renderer:

1. Render and upscale in Vulkan as now. Export the final scene image (pre-HUD), the HUD
   image, depth and motion vectors through `VK_KHR_external_memory_win32`, with
   `VK_KHR_external_semaphore_win32` for synchronisation.
2. Present through the D3D12 swap chain that XeSS-FG wraps (the API in
   `xefg_swapchain_d3d12.h`). Each frame, pass it the shared resources and the camera
   data. The port already composes the HUD separately, which FG needs for
   UI-free interpolation.
3. Add XeLL on the same D3D12 queue to keep latency in check, and pace the guest at half the
   output rate.

Prerequisites:

- correct object motion vectors (plan item 1): FG interpolates with them, so camera-only
  vectors would warp characters;
- a stable base frame rate (frame generation multiplies the frame rate but not the
  responsiveness). The startup-patched XeSS Quality path reaches about 57 FPS in the heavy
  view now.
