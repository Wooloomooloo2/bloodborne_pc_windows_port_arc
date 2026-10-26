# Intel Arc optimisation notes

Findings from profiling Bloodborne on an Intel Arc iGPU, aimed at locked 60 fps at native
1080p without upscaling. Upscaling (XeSS) and frame generation come after that, as extras.

## Test system

| | |
|---|---|
| CPU | Intel Core Ultra X7 358H (16 logical CPUs) |
| GPU | Intel Arc B390 iGPU (Xe3, 12 Xe cores), driver 32.0.101.9033 |
| Memory | Shared LPDDR5X (GPU and CPU share bandwidth) |
| OS | Windows 11 Pro 26300 |
| Power | **Balanced** scheme during the measurements below (CPU at about 1.7 GHz effective) |

For comparison, the PS4 GPU is a 1.84 TFLOPS GCN with 176 GB/s of dedicated GDDR5. The B390
has more compute and a newer architecture. Its weak point is memory bandwidth, which it shares
with the CPU.

## Method (reproducible)

- **Test scene:** a save checkpoint in one of the game's heaviest areas,
  `user/savedata-backup-20261022-220836-demanding-area/SPRJ0005/` (copy it over
  `user/savedata/1/CUSA00207/SPRJ0005/`). The heaviest view is the direction the save loads
  facing. The opposite direction is light (about 59 fps).
- **Settings:** 1080p native, `upscaler=off`, 60 FPS patch, `BB_FRAMES_AHEAD=2`, full screen,
  FIFO present (Mailbox is not supported here), Intel 12th Gen+ SFX workaround.
- **Unattended runs:** `BB_PAD_FILE=out/pad` gives scripted input. The title sequence from
  `tools/restart.sh` is: wait for "pad opened" and three `Frame stats` lines, then `cross`,
  wait 6 s, `cross`, then wait for more than 600 draws/frame. A full camera turn is about
  1.5 s of `rx=255`.
- **Instrumentation:**
  - `BB_FRAME_STATS=1`: FPS, pacing, GPU-thread cost per draw, waits, host copies, write
    faults and shader compiles, every few seconds.
  - `BB_GPU_PROFILE=1`: GPU ms/frame per render pass and dispatch, from timestamps.
  - `BB_GPU_PROFILE=2` (new): also timestamps **inside** render passes, at every change of
    shader pair, which splits a pass by the shaders drawing in it. Timestamp period on the B390
    is 52 ns. Fine once averaged.
  - `BB_CAPTURE_TRIGGER` + `BB_CAPTURE_DIR`: one-frame capture of every pass and draw.
    `BB_CAPTURE_VS=<hash>` (new) adds topology, cull, depth and blend state and the
    index/instance counts of that vertex shader's draws.
  - Per-thread CPU (via `GetThreadDescription` names) and per-engine GPU utilisation come
    from Windows performance counters, outside the game.
  - Translated shaders are in `user/cache/CUSA03173/0x<hash>_<perm>.spv`.
    `spirv-dis` (MSYS2) disassembles them. `BB_DUMP_SHADERS=1` with `BB_PIPELINE_CACHE=0`
    dumps them freshly to `user/shader/dumps`.

## Baseline (2026-10-22, heavy view)

**32–35 fps.** With FIFO at 60 Hz, a frame that misses 16.7 ms waits for the next vblank, so
it locks to 33.3 ms. Pacing median 33.2 ms, p99 34.5 ms. 0 shader compiles during play, so
the stutter is not compilation.

### The game is GPU-bound

- GPU 3D engine **92–97% busy** (peaks at 100%). The compute and copy engines sit at 0%:
  all work goes on the 3D queue.
- **GPU time 28.8–31.0 ms/frame.** The 60 fps budget is 16.7 ms, so the frame needs about 45%
  less GPU time.
- The CPU waits on the GPU: the draw pipe's stage A spends 59% of its waits in `OnSubmit`
  (frames-ahead limit), and the GPU command thread reports 46% "blocked on GPU ticks".

### GPU time by stage (`BB_GPU_PROFILE=1`, average of 14 × 5 s windows)

| Stage | ms/frame | Share |
|---|---|---|
| **G-buffer geometry** (6 MRT: 4× RGBA8, R11G11B10F, RGBA16F + D32S8, 1080p) | **19.2** | **62%** |
| Deferred lighting (`vs 8747a367`) | 1.5 | 5% |
| Full-res post / composite / UI | 1.2 | 4% |
| Square targets (shadow/env maps, 2048²) | 0.8 | 2.5% |
| Game compute dispatches | 0.7 | 2% |
| HLE copy shaders (80 dispatches/frame) | 0.6 | 2% |
| GPU idle between submissions | 0.6 | 2% |
| Transparents / particles / fog (RGBA16F + depth) | 0.6 | 2% |
| Half-res post, depth-only passes | 0.5 | 1.5% |
| Labels below the profiler's print cut-off | 5.3 | 17% |

### Inside the G-buffer (`BB_GPU_PROFILE=2`)

The G-buffer is almost entirely **one render pass**: capture pass #151, **331 draws, 8.36 M
indices**. One shader pair in it dominates the whole frame:

| Shader pair | ms/frame | Runs/frame |
|---|---|---|
| **`vs 6a833138` + `ps bb10e440`** | **10.6** (37% of the GPU frame) | 18 |
| `vs 3eaa5555` + `ps dbeccd9c` | 1.3 | 8 |
| `vs 6a833138` + `ps 1c3953c6` | 1.1 | 10 |
| `vs 9da6aacb` + `ps bb10e440` (same pixel shader) | 0.9 | 32 |
| `vs 63220f67` + `ps dbb222eb` | 0.6 | 17 |

What `vs 6a833138` draws (one-frame capture):

- 61 draws per frame, all **indexed, instanced** (×3–×17), triangle lists.
- Up to 56,232 indices per draw.
- About **0.81 M triangles** in total (indices × instances / 3).
- Depth test and write on (func 3), no blending.
- Half the draws have culling off (double-sided geometry).

### CPU (about 3.5 of 16 cores busy)

| Thread | % of one core | Role |
|---|---|---|
| `shadPS4:GpuCommandProcessor` | 89 | PM4 → Vulkan translation; 22 µs/draw at ~1,120 draws, ~46% of it waiting on the GPU |
| `bb:DrawRec` | 33 | Draw recording |
| `bb:VkRecorder` | 31 | Vulkan command recording |
| `GXRenderThread`, 5× `GXWorker` | ~9 each | The game's render threads |
| 8× `bb:DrawPrep`, `bb:DrawScan` | 4–6 each | Parallel draw preparation |
| Havok workers, FMOD mixer | 4–8 | Physics, audio |

Also measured: host copies block 3% (40/frame), about 1,000 write faults/s, 160–220 recorder
syncs per 5 s.

## Findings

1. **The 10.6 ms is not shader complexity.** In the translated SPIR-V, `vs 6a833138` is 884
   lines with no loops, and `ps bb10e440` is 1,479 lines with 6 texture samples. They are about
   the size of `vs 9da6aacb`, which pairs with the same pixel shader for 0.9 ms.
2. **Lost early-Z is not the cause here.** The recompiler ignores the game's
   `DB_SHADER_CONTROL` hints (`z_order`, `conservative_z_export`): any depth-writing shader gets
   plain `DepthReplacing`, and shaders that discard get no early-test mode. But none of the hot
   pixel shaders discard or write depth (checked: `e21e283a`, `bb10e440`). It is still a
   correctness/perf gap for other passes.
3. **It is not raw triangle volume.** About 0.8 M triangles in 10.6 ms is about 76 M tri/s,
   one to two orders of magnitude below what this GPU class sustains. Something about these
   draws costs far more than the PS4 paid.
4. **Next limit after the GPU: the command thread.** Without its GPU waits it still does about
   15–17 ms/frame of translation at 1.7 GHz (Balanced), which sits at the 60 fps budget.
5. **Small, known overheads:** 80 HLE copy dispatches (0.6 ms), GPU bubbles between
   submissions (0.6 ms), and a few image upload/download round trips (~0.5 ms).

## Hypotheses for the `vs 6a833138` cost (in order)

1. **Instanced vertex fetch.** GCN instance step rates and the fetch shader may be emulated
   in a way that is slow on Intel, for example per-vertex buffer loads or divisor handling.
2. **Pixel cost.** Large, close, double-sided instanced objects with six render targets could
   mean heavy overdraw and bandwidth. This is plausible on shared LPDDR5X.
3. **Attribution.** The first draws of a pass absorb its start-up cost (attachment loads,
   layout transitions, the tail of earlier work), and these draws open pass #151.

**Next measurement:** Vulkan pipeline-statistics queries per profiler segment. They count
vertex-shader invocations, fragment-shader invocations and clipped primitives for each run of
draws. That will show which hypothesis holds. A follow-up is an Intel GPA frame capture of pass
#151, for hardware counters (vertex fetch, pixel, memory).

## Session 2 (2026-10-23): statistics, pass splits, method

### Measurement method: alternate, don't compare runs

FPS in the same view drifts by up to ~25% between launches on this laptop: 33–36 fps in the
evening session, 45–46 fps an hour later, same build and settings (Balanced, on AC). That is
probably power, thermal or background state. So a change is only judged by **alternating it
within a session** (`BB_TOGGLE_FILE` bits are re-read every 250 ms) or by **A/B/A/B launches**
(scratchpad `ab.sh`). Benchmarks use their own `BB_CONFIG` (a copy of `bbport.ini` at 1080p
native), so they never touch, or depend on, the launcher's settings.

### Pipeline statistics (`BB_GPU_PROFILE=3`, new)

Per-draw `VK_QUERY_TYPE_PIPELINE_STATISTICS` totals per shader pair, printed with the
timings. The per-draw queries cost a lot on Intel (about 28 fps against 35), so use this only
for counts.

Frame totals in the heavy view:

| | per frame |
|---|---|
| Input-assembly vertices | 9.4 M |
| VS invocations | 3.6 M |
| Primitives in / after clipping | 3.1 M / 2.0 M |
| Fragment invocations | 62.3 M (30× a 1080p screen, mostly about 20 full-screen post/lighting passes at 2.07 M each) |

**The "10.6 ms" pair `vs 6a833138` + `ps bb10e440` is tiny:** 34 draws, 0.93 M VS invocations,
0.62 M primitives, 1.3 M fragments. Its share in the level-2 timings is an attribution
artefact. Timestamps inside a render pass charge the gap to whichever segment is open, and
these draws open the G-buffer pass and interleave with other shaders. The G-buffer *pass* is
still about 19 ms. The cost is not explained by shading volume.

### Render-pass splits (`BB_PASS_SPLITS=1`, new)

These are restarts of a render pass on the same attachments it just ended. Each one stores and
reloads every attachment. Result: **37 splits/frame, about 150 attachment reloads/frame:**

| Ended by | Splits/frame |
|---|---|
| HLE copy shader (`vk_shader_hle.cpp:229`): the game's compute copies between G-buffer draws | 17.0 |
| `BufferCache::SynchronizeMemory` uploads (`buffer_cache.cpp:521`) | 6.1 |
| `FlushBarriers` from `DrawRecord` (`vk_rasterizer.cpp:1017`) | 8.0 |
| Other (`DrawRecord`, `MultiCopy`, `CopyImage`) | ~6 |

**Tried and reverted: deferring the copy-shader transfers** past the open pass, flushing them on
a dependent access or at the pass end. It cut splits from about 37.5 to about 32.4 per frame
(171 transfers deferred, 12 dependent flushes per frame). Measured in three in-session on/off
cycles, it made **no difference**: 45.4–46.4 fps both ways, about 15 µs/draw both ways. Pass
splits are not the bottleneck on this GPU, so the extra complexity was not kept.

## Session 3 (2026-10-24): stutter, memory corruption, HUD

### Stall diagnostics now work on Windows

`BB_FRAME_STATS=1` prints a breakdown for every frame over 40 ms ("Stall: ..."), but it keyed
on a POSIX thread clock and never printed on Windows. It now uses the GPU thread's
`GetThreadTimes` (15.6 ms granularity, so the user/kernel split is coarse) and also reports
the shader/pipeline compiles in that frame (`driver.cpp`).

### Area-load hitches: sparse residency allocations (fixed)

From the title screen into the level, and walking (scripted, `walk.sh`):

| | worst load frame | time making buffer memory resident | image refresh |
|---|---|---|---|
| before | 1149 ms | 373 ms | 695 ms |
| after (2 runs) | 300 / 284 ms | 20 / 31 ms | 267 / 252 ms |

`BufferCache::EnsureResident` called `vkAllocateMemory` once per newly used range of the
sparse arena. Each is a kernel call on Windows (WDDM), and several hundred happen as an area
streams in. Backing memory now comes from 32 MiB chunks that are sub-allocated (residency is
never released, so a bump allocator is enough; at most one partly used chunk is wasted).
`Backing::offset` is now in blocks, like `start`/`end`, so `CanMergeWith`/`SubRange` are exact.

Still on the GPU thread during loads: image refresh (detile + upload, ~160 ms for 372 MB in
the worst frame) and about 190 ms of kernel time (page protection: 116k pages in one frame).
Hashing every mip (`XXH3` in `RefreshImage`) costs ~20 ms per load but cannot simply be
skipped for non-GPU-modified images: the stored hash protects GPU-written data from stale CPU
re-uploads later.

Shader compiles: none during play with a warm cache. The pipeline cache persists across
sessions on Windows (files from 10-09 still present; 64 new pipelines on 10-24). The Mac
port's "stale pipelines wipe the cache every launch" bug does not trigger here.

### Low allocator heap corruption (fixed, likely cause of the empty Repair menu)

`runtime_low_free` (`src/runtime_memory.c`) read the block's size class after overwriting it
with the free-list link, so freed blocks went onto the list named by the upper half of the old
head pointer (8 for the `0x08_xxxx_xxxx` low range: the 8 KiB class). An 8 KiB request could
then receive a 32-byte block, overlapping guest TLS, thread records and lock objects; the
small classes leaked. The macOS port fixed this on 10-18. Windows-only symptom that matched:
the workshop Repair list is empty on Windows with a save that works on macOS. To be confirmed
in game.

### HUD item icon missing off 1080p (fixed)

Ported macOS #17: Scaleform draws the item slot's stencil mask depth-only, and only
single-color-target draws reached the upscaler, so the mask went into the guest depth while
the icon was redirected to the host UI depth with a cleared stencil. Depth-only movie-shader
draws in the UI phase now prepare and bind the host UI depth (`vk_rasterizer.cpp`,
`TemporalUpscaler::OnDraw`). Verified at 2560x1440 output: the Molotov icon renders.

### Frame pacing

At 40-50 fps the guest flips at vblank (60 Hz), so frames alternate 16.7 and 33.3 ms
(stddev ~7.5 ms, 50-90 frames over 1.5x median per 5 s). This judder reads as stutter even
without hitches. The panel runs at 60 Hz but supports 120 Hz; options to evaluate: a 120 Hz
desktop mode, VRR, or a 40 fps mode (guest pacing at a 120 Hz vblank).

## Session 4 (2026-10-26): XeSS quality and performance

Heavy view, scripted launches (`xess4.sh`), XeSS Quality (1.5x) at 1080p output:

| | FPS | GPU ms/frame |
|---|---|---|
| native, no upscaler | 46.9 | 21.3 |
| XeSS, live resolution path (what Windows users got) | 40.9 | 24.5 |
| **XeSS, startup patch (scene 1280x720)** | **57.1** | **17.5** |
| XeSS + object motion, live path | 24.4 | 41.3 |
| XeSS + object motion, startup patch | 20.5 | 49.0 |

(The two object-motion rows ran in a slower power state: native measured 38.5 FPS then.)

### Fixed: XeSS ran on the slow path on Windows

- **`run.bat` never applied the startup patch.** Its `for /f ('"%PYTHON%" ... "%BB_CONFIG%"')`
  query lost its quotes to cmd's quote stripping and silently returned nothing, so every
  scaled preset at every output size ran on the live path. Now wrapped in an extra pair of
  quotes, with the `2>nul` redirect outside them.
- **1080p output with a reducing preset now uses the startup patch too**
  (`patches.py scaled_sizes`). The live path keeps the game's buffers and post-processing at
  1080p, reduces only 56 of 134 scene draws, and resolves the reduced proxies back every
  frame. That cost more than it saved, and the extra resampling washed out the image.
  `--print-scaled` now honours the launcher's `BB_UPSCALER` / `BB_UPSCALE_PRESET`
  overrides, so "upscaler off" never leaves a reduced scene. As for other outputs, a preset
  change at 1080p needs a restart (`BB_AUTO_RENDER_RES` recomputes it).
- Tests: `tests/test_patches.py` and `tests/test_run_settings.py` updated.

### Checked

- **Jitter sign: the default is right.** Flipping it live (toggle bit 26) while standing
  still gives a visibly blurrier and hazier image (frame mean 38.4 vs 35.1, native 34.8).
  The Laplacian "sharpness" metric is not comparable across AA methods: native's dithering
  and grass shimmer inflate it about 10x.
- **XeSS itself costs about 2.4-2.7 ms/frame** at 1280x720 → 1920x1080.

### Remaining problems and plan

1. **Ghosting of the character and moving objects** (10-26: see docs/xess.md, object motion cost) (the main artefact when moving). Object
   motion vectors are off by default (`object_motion=0`; the launcher switch can only force
   them off). With them on, the G-buffer cost explodes: motion draws add an 8th colour
   attachment and the rest don't, so the pass is split wherever they alternate (about 136
   motion draws per frame), and each split reloads 6-8 full-screen targets.
   *Plan:* while object motion is on, bind the motion attachment for every G-buffer draw.
   Non-motion pipelines get a key bit that declares the 8th format with a zero write mask.
   Then measure splits (`BB_PASS_SPLITS=1`) and GPU time again. If that brings the cost
   under ~2 ms, turn object motion on by default whenever a temporal upscaler is active, and
   make the launcher switch able to force it on.
2. **Residual haze/softness.** Compare a dumped XeSS input against the output
   (`BB_DUMP_TRIGGER`). Try `exposureScale` and auto-exposure off for the HDR input, and the
   tonemapped (LDR) input placement, and check which post effects run at 720p (DOF, bloom).
3. **Sharpening.** XeSS has none of its own; check that the RCAS default (0.30) suits
   1.5x on Arc.
4. **Start-up reliability.** About 3 of 25 scripted launches today either stopped at a black
   screen right after "pad opened" or crashed with guest fault `0x20b56d2` (also in the
   10-09 crash logs). `session.sh` now detects a title hang, saves the log and thread CPU,
   and retries. The cause is not investigated yet.
5. **The "fast/slow launch" G-buffer bimodality** (Session 3: `vs 6a833138` + `ps bb10e440`
   3.8 vs 11.0 ms per launch) affects every mode. `BB_TRACE_PS=bb10e440` prints the images
   it samples, so a fast and a slow launch can be compared.
6. **XeSS 2 frame generation / XeLL** need D3D12 present interop (see below).

## API, upscaling and frame generation

- **Stay on Vulkan for now.** Intel's driver compiles SPIR-V and DXIL with the same shader
  compiler (IGC), so a GNM → DX12 rewrite would not make the measured GPU work cheaper. The
  bottleneck is the work submitted, not API overhead. A full D3D12 backend is only worth it if
  a GPA capture shows the Vulkan driver itself is at fault.
- **XeSS-SR** works through its Vulkan API (`vk_xess.{h,cpp}`, `upscaler=xess`, `libxess`
  2.0.2 from SDK 3.0.2). It is deferred as a product feature until native 1080p holds 60.
  Image quality was poor in the first test with both XeSS and FSR 3.1, which points to the
  shared inputs (jitter, motion vectors or the reduced-resolution path), not to XeSS. Still
  to investigate.
- **XeSS-FG and XeLL are DirectX 12 only** in SDK 3.0.2: only `xefg_swapchain_d3d12.h` and
  `xell_d3d12.h` exist. To use them, keep rendering in Vulkan and present through a D3D12
  swapchain. Share the final image with `VK_KHR_external_memory_win32` and run XeSS-FG/XeLL
  on that swapchain. Whether Intel's driver-level frame generation can apply to this setup is
  still to be confirmed.

## Other issues found along the way

- `MapNumberConversion` asserted on SNORM_NZ with `Format32_32_32` and stopped the game.
  GCN has no such conversion, so the hardware reads raw bits. Fixed by returning `None`
  (`pixel_format.h`).
- The scheduler owner guard fires in `Runtime::CopyBuffer` (`vk_runtime.cpp:112/135`) on the
  GPU command thread while the draw pipe is busy. It drains the pipe first, so nothing breaks,
  but the calling path (buffer-cache sync/download/residency or the HLE copy shader) should
  drain explicitly.
- FSR 4 needs `fsr4_shaders/*.spv` (`tools/fetch_fsr4_assets.sh`). Without them it falls back
  to FSR 3.1.
