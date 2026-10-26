// SPDX-License-Identifier: GPL-2.0-or-later

#include <algorithm>
#include <cstdlib>

#include "video_core/renderer_vulkan/vk_gpu_profiler.h"
#include "video_core/renderer_vulkan/vk_instance.h"
#include "video_core/renderer_vulkan/vk_scheduler.h"

namespace Vulkan {

namespace {

/// BB_GPU_PROFILE=3: the statistics counted per draw, in Vulkan's result order (bit order).
constexpr vk::QueryPipelineStatisticFlags DrawStatistics =
    vk::QueryPipelineStatisticFlagBits::eInputAssemblyVertices |
    vk::QueryPipelineStatisticFlagBits::eInputAssemblyPrimitives |
    vk::QueryPipelineStatisticFlagBits::eVertexShaderInvocations |
    vk::QueryPipelineStatisticFlagBits::eClippingInvocations |
    vk::QueryPipelineStatisticFlagBits::eClippingPrimitives |
    vk::QueryPipelineStatisticFlagBits::eFragmentShaderInvocations;

} // namespace

void GpuProfiler::Init(const Instance& instance, Scheduler& scheduler) {
    const char* env = std::getenv("BB_GPU_PROFILE");
    if (!env || env[0] < '1' || env[0] > '3' || instance_ptr) {
        return;
    }
    instance_ptr = new GpuProfiler(instance, scheduler, env[0] - '0');
}

GpuProfiler::GpuProfiler(const Instance& instance, Scheduler& scheduler_, int level_)
    : device{instance.GetDevice()}, scheduler{scheduler_}, level{level_} {
    const vk::QueryPoolCreateInfo info = {
        .queryType = vk::QueryType::eTimestamp,
        .queryCount = NumSlices * SliceQueries,
    };
    pool = Check<"create timestamp query pool">(device.createQueryPoolUnique(info));
    device.resetQueryPool(*pool, 0, NumSlices * SliceQueries);
    period_ns = instance.GetPhysicalDevice().getProperties().limits.timestampPeriod;
    for (auto& k : keys) {
        k.reserve(SliceQueries);
    }
    if (level >= 3 && instance.GetPhysicalDevice().getFeatures().pipelineStatisticsQuery) {
        const vk::QueryPoolCreateInfo stats_info = {
            .queryType = vk::QueryType::ePipelineStatistics,
            .queryCount = NumSlices * SliceStats,
            .pipelineStatistics = DrawStatistics,
        };
        stats_pool =
            Check<"create statistics query pool">(device.createQueryPoolUnique(stats_info));
        device.resetQueryPool(*stats_pool, 0, NumSlices * SliceStats);
        for (auto& k : stats_keys) {
            k.reserve(SliceStats);
        }
    }
    std::printf("GPU profile: on (timestamp period %.2f ns, level %d%s%s)\n", period_ns, level,
                level >= 2 ? ": per shader pair inside passes" : "",
                stats_pool ? ", pipeline statistics per draw" : "");
}

void GpuProfiler::WriteTimestamp(u64 key, bool end_rendering) {
    if (used[slice] + 1 >= SliceQueries) {
        return; // the frame's slice is full: the rest of the frame goes to the last label
    }
    // Outside render passes: radv_CmdWriteTimestamp2 crashed inside some. Marks sit where a
    // pass, dispatch or submission ends anyway. Level 2 draw marks stay inside the pass.
    if (end_rendering) {
        scheduler.EndRendering();
    }
    const u32 query = slice * SliceQueries + used[slice]++;
    keys[slice].push_back(key);
    current = key;
    scheduler.Record([pool = *pool, query](vk::CommandBuffer cmdbuf) {
        cmdbuf.writeTimestamp2(vk::PipelineStageFlagBits2::eAllCommands, pool, query);
    });
}

void GpuProfiler::BeginFrame() {
    // Close the frame: one more timestamp without a label.
    if (used[slice] > 0 && used[slice] < SliceQueries) {
        scheduler.EndRendering();
        const u32 query = slice * SliceQueries + used[slice]++;
        scheduler.Record([pool = *pool, query](vk::CommandBuffer cmdbuf) {
            cmdbuf.writeTimestamp2(vk::PipelineStageFlagBits2::eAllCommands, pool, query);
        });
        pending[slice] = true;
    }
    slice = (slice + 1) % NumSlices;
    // The oldest slice: its frame was submitted four frames ago.
    if (pending[slice]) {
        Collect(slice);
    }
    if (used[slice]) {
        device.resetQueryPool(*pool, slice * SliceQueries, used[slice]);
    }
    if (stats_used[slice]) {
        device.resetQueryPool(*stats_pool, slice * SliceStats, stats_used[slice]);
    }
    used[slice] = 0;
    keys[slice].clear();
    stats_used[slice] = 0;
    stats_keys[slice].clear();
    pending[slice] = false;
    Print();
}

void GpuProfiler::Collect(u32 which) {
    const u32 count = used[which];
    std::vector<u64> stamps(count);
    // Four frames on this is complete unless the GPU lags that far: then wait for it.
    const auto result = device.getQueryPoolResults(
        *pool, which * SliceQueries, count, count * sizeof(u64), stamps.data(), sizeof(u64),
        vk::QueryResultFlagBits::e64 | vk::QueryResultFlagBits::eWait);
    if (result != vk::Result::eSuccess) {
        return;
    }
    for (u32 i = 0; i + 1 < count; ++i) {
        const double ms = double(stamps[i + 1] - stamps[i]) * period_ns / 1e6;
        auto& total = totals[keys[which][i]];
        total.ms += ms;
        ++total.segments;
    }
    ++frames;
    if (const u32 n = stats_used[which]; n) {
        std::vector<u64> values(size_t(n) * NumStats);
        const auto stats_result = device.getQueryPoolResults(
            *stats_pool, which * SliceStats, n, values.size() * sizeof(u64), values.data(),
            NumStats * sizeof(u64), vk::QueryResultFlagBits::e64 | vk::QueryResultFlagBits::eWait);
        if (stats_result == vk::Result::eSuccess) {
            for (u32 i = 0; i < n; ++i) {
                auto& s = stats[stats_keys[which][i]];
                for (u32 j = 0; j < NumStats; ++j) {
                    s.sum[j] += double(values[size_t(i) * NumStats + j]);
                }
                ++s.draws;
            }
        }
    }
}

void GpuProfiler::Print() {
    const auto now = std::chrono::steady_clock::now();
    if (now - window < std::chrono::seconds(5) || frames == 0) {
        return;
    }
    window = now;
    std::vector<std::pair<u64, Total>> sorted(totals.begin(), totals.end());
    std::ranges::sort(sorted, [](const auto& a, const auto& b) { return a.second.ms > b.second.ms; });
    double sum = 0;
    for (const auto& [key, total] : sorted) {
        sum += total.ms;
    }
    std::printf("GPU profile: %.2f ms/frame over %llu frames, %zu labels\n", sum / frames,
                static_cast<unsigned long long>(frames), sorted.size());
    for (size_t i = 0; i < std::min<size_t>(sorted.size(), level >= 2 ? 60 : 30); ++i) {
        const auto& [key, total] = sorted[i];
        std::printf("  %6.3f ms/frame %5.1f/frame  %s\n", total.ms / frames,
                    double(total.segments) / frames, described[key].c_str());
    }
    if (!stats.empty()) {
        // Per frame, by fragment shader invocations: pixel work (overdraw) next to vertex work.
        std::vector<std::pair<u64, Stats>> by_pixels(stats.begin(), stats.end());
        std::ranges::sort(by_pixels,
                          [](const auto& a, const auto& b) { return a.second.sum[5] > b.second.sum[5]; });
        std::array<double, NumStats> all{};
        for (const auto& [key, s] : by_pixels) {
            for (u32 j = 0; j < NumStats; ++j) {
                all[j] += s.sum[j];
            }
        }
        const double f = double(frames);
        std::printf("GPU statistics per frame: %.2fM vertices, %.2fM VS invocations, %.2fM "
                    "primitives in, %.2fM out of clipping, %.2fM fragment invocations "
                    "(%.2fx 1080p)\n",
                    all[0] / f / 1e6, all[2] / f / 1e6, all[1] / f / 1e6, all[4] / f / 1e6,
                    all[5] / f / 1e6, all[5] / f / (1920.0 * 1080.0));
        for (size_t i = 0; i < std::min<size_t>(by_pixels.size(), 30); ++i) {
            const auto& [key, s] = by_pixels[i];
            std::printf("  %7.3fM frag %6.3fM VS %6.3fM prim in %6.3fM out %5.1f draws  %s\n",
                        s.sum[5] / f / 1e6, s.sum[2] / f / 1e6, s.sum[1] / f / 1e6,
                        s.sum[4] / f / 1e6, double(s.draws) / f, described[key].c_str());
        }
        stats.clear();
    }
    totals.clear();
    frames = 0;
}

} // namespace Vulkan
