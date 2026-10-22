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
