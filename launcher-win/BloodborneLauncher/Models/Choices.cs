using System.Globalization;

namespace BloodborneLauncher.Models;

/// <summary>The fixed option lists, and the values each maps to (launcher.py, bbport_settings.h).</summary>
internal static class Choices
{
    /// <summary>bbport.ini output_res, written as WxH (BbSettings::Save). Index 1 is the default.</summary>
    public static readonly (int W, int H)[] Outputs = [(1280, 720), (1920, 1080), (2560, 1440), (3840, 2160)];
    public const int OutputDefault = 1;
    public static readonly string[] OutputLabels = [.. Outputs.Select(o => $"{o.W} × {o.H}")];

    public static string OutputValue(int index)
    {
        var (w, h) = Outputs[Math.Clamp(index, 0, Outputs.Length - 1)];
        return string.Create(CultureInfo.InvariantCulture, $"{w}x{h}");
    }

    public static int OutputIndex(string? value)
    {
        for (var i = 0; i < Outputs.Length; i++)
        {
            if (string.Equals(value, OutputValue(i), StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }
        // The engine also accepts nothing else; an index (older notes) is tolerated here.
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) && n >= 0 && n < Outputs.Length
            ? n : OutputDefault;
    }

    /// <summary>Segmented order; BB_FPS values.</summary>
    public static readonly string[] Fps = ["30", "60", "90", "uncap"];

    /// <summary>bbport.ini upscaler values in ComboBox order ("fsr411" is shown as FSR 4).</summary>
    public static readonly string[] Upscalers = ["off", "fsr3", "fsr4", "xess", "taa"];
    public static readonly string[] UpscalerLabels = ["Off", "FSR 3.1", "FSR 4", "XeSS", "TAA"];
    public const int UpscalerOff = 0;
    public const int UpscalerTaa = 4;

    public static int UpscalerIndex(string? value) => value switch
    {
        "fsr3" => 1,
        "fsr4" or "fsr411" => 2,
        "xess" => 3,
        "taa" => 4,
        _ => 0,
    };

    public static readonly string[] Presets = ["Native AA", "Quality", "Balanced", "Performance", "Ultra Performance"];
    private static readonly double[] PresetScales = [1.0, 1.5, 1.7, 2.0, 3.0];

    /// <summary>SceneResolution::ForPreset (vk_scene_resolution / scene_resolution.h): even sizes,
    /// std::lround (half away from zero).</summary>
    public static (int W, int H) ForPreset(int preset, (int W, int H) output)
    {
        var scale = PresetScales[Math.Clamp(preset, 0, 4)];
        static int Even(double v) => (int)Math.Max(1L, (long)Math.Round(v, MidpointRounding.AwayFromZero)) * 2;
        return (Even(output.W / scale / 2), Even(output.H / scale / 2));
    }

    public static readonly string[] AnisoLabels = ["16x", "8x", "4x", "2x", "Off"];
    public static readonly string[] AnisoValues = ["16", "8", "4", "2", "0"];

    /// <summary>BB_RENDER_RES choices (resolution scaling); the first is "no override".</summary>
    public static readonly string[] RenderRes = ["Default (1080p)", "1280x720", "1920x1080", "2560x1440", "3840x2160"];

    public static readonly string[] Timeouts = ["0", "10", "30", "60", "120", "300", "600"];
    public static readonly string[] TimeoutLabels = ["0 (no limit)", "10 s", "30 s", "60 s", "120 s", "300 s", "600 s"];

    /// <summary>patches.py EFFECTS: ini key → (patch when off, patch when on).</summary>
    public static readonly (string Key, string? Off, string? On)[] EffectPatches =
    [
        ("effect_chromatic_aberration", "Disable Chromatic Aberration", null),
        ("effect_dof", "Disable DoF", null),
        ("effect_motion_blur", "Disable Motion Blur (perf increase)", null),
        ("effect_ssao", "Disable SSAO", null),
        ("effect_game_aa", "Disable AA", null),
        ("effect_dynamic_shadows", "Disable Dynamic Light Shadows (perf increase)", null),
        ("effect_ssr", null, "Enable Screen Space Reflections (READ NOTE)"),
        ("skip_intro", null, "Skip Intro"),
        ("debug_camera", null, "Restore Debug Camera"),
        ("debug_menu", null, "Restore Debug Menu (READ NOTES)"),
    ];

    /// <summary>Patch names bbport.ini drives; the patch manager leaves them out.</summary>
    public static readonly HashSet<string> IniManagedPatches =
        [.. EffectPatches.SelectMany(e => new[] { e.Off, e.On }).OfType<string>()];

    /// <summary>launcher.py's recommended set, minus the patches now set in Effects.</summary>
    public static readonly string[] RecommendedPatches = ["Performance Patch (perf increase)"];
}
