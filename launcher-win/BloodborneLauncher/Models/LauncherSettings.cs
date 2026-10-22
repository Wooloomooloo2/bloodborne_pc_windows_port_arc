using System.Text.Json;
using System.Text.Json.Nodes;

namespace BloodborneLauncher.Models;

/// <summary>
/// launcher_settings.json, schema-compatible with launcher.py (which stays as a fallback):
/// the same keys and value formats, plus the native launcher's own keys. Unknown keys
/// are kept. Every value is validated on load, falling back to launcher.py's defaults.
/// </summary>
internal sealed class LauncherSettings
{
    private JsonObject _raw = [];

    public string Eboot { get; set; } = "";
    public string Fps { get; set; } = "uncap";
    public string Res { get; set; } = Choices.RenderRes[0];
    public string Aniso { get; set; } = "16x";
    public string Timeout { get; set; } = "0";
    public bool Mods { get; set; } = true;
    public bool HideVk { get; set; } = true;
    public bool FeatUpscaler { get; set; } = true;
    public bool FeatObjectMotion { get; set; } = true;
    public bool FeatOverlay { get; set; } = true;
    public bool FeatFpsPatch { get; set; } = true;
    public bool FeatResScaling { get; set; } = true;
    public bool FeatTracing { get; set; } = true;
    public bool FeatWatchdog { get; set; } = true;
    public bool FeatDrawPrep { get; set; } = true;
    public SortedSet<string> EnabledPatches { get; set; } = new(StringComparer.Ordinal) { "Skip Intro" };

    // Native launcher keys.
    public bool Fullscreen { get; set; } = Environment.GetEnvironmentVariable("BB_FULLSCREEN") == "1";
    public bool FrameStats { get; set; } = !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("BB_FRAME_STATS"));
    public bool ShowLauncher { get; set; } = true;
    public string UserDir { get; set; } = "";
    public bool IniMigrated { get; set; }

    /// <summary>True when the file had a feat_upscaler key (for the one-time ini migration).</summary>
    public bool HadFeatUpscaler { get; private set; }

    public static LauncherSettings Load(string path)
    {
        var s = new LauncherSettings();
        try
        {
            if (File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject obj)
            {
                s._raw = obj;
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
        }
        var r = s._raw;
        s.Eboot = Str(r, "eboot", s.Eboot);
        s.Fps = OneOf(Str(r, "fps", s.Fps), Choices.Fps, "uncap");
        s.Res = OneOf(Str(r, "res", s.Res), Choices.RenderRes, Choices.RenderRes[0]);
        s.Aniso = OneOf(Str(r, "aniso", s.Aniso), Choices.AnisoLabels, "16x");
        s.Timeout = OneOf(Str(r, "timeout", s.Timeout).Split(' ')[0], Choices.Timeouts, "0");
        s.Mods = Bool(r, "mods", s.Mods);
        s.HideVk = Bool(r, "hide_vk", s.HideVk);
        s.HadFeatUpscaler = r["feat_upscaler"] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False;
        s.FeatUpscaler = Bool(r, "feat_upscaler", s.FeatUpscaler);
        s.FeatObjectMotion = Bool(r, "feat_object_motion", s.FeatObjectMotion);
        s.FeatOverlay = Bool(r, "feat_overlay", s.FeatOverlay);
        s.FeatFpsPatch = Bool(r, "feat_fps_patch", s.FeatFpsPatch);
        s.FeatResScaling = Bool(r, "feat_res_scaling", s.FeatResScaling);
        s.FeatTracing = Bool(r, "feat_tracing", s.FeatTracing);
        s.FeatWatchdog = Bool(r, "feat_watchdog", s.FeatWatchdog);
        s.FeatDrawPrep = Bool(r, "feat_draw_prep", s.FeatDrawPrep);
        if (r["enabled_patches"] is JsonArray arr)
        {
            s.EnabledPatches = new SortedSet<string>(
                arr.OfType<JsonValue>().Where(x => x.GetValueKind() == JsonValueKind.String)
                   .Select(x => x.GetValue<string>().Trim()).Where(x => x.Length > 0),
                StringComparer.Ordinal);
        }
        s.Fullscreen = Bool(r, "fullscreen", s.Fullscreen);
        s.FrameStats = Bool(r, "frame_stats", s.FrameStats);
        s.ShowLauncher = Bool(r, "show_launcher", s.ShowLauncher);
        s.UserDir = Str(r, "user_dir", s.UserDir);
        s.IniMigrated = Bool(r, "ini_migrated", s.IniMigrated);
        return s;
    }

    public bool Save(string path)
    {
        var r = _raw;
        r["eboot"] = Eboot.Trim();
        r["fps"] = Fps;
        r["res"] = Res;
        r["aniso"] = Aniso;
        r["timeout"] = Timeout;
        r["mods"] = Mods;
        r["hide_vk"] = HideVk;
        r["feat_upscaler"] = FeatUpscaler;
        r["feat_object_motion"] = FeatObjectMotion;
        r["feat_overlay"] = FeatOverlay;
        r["feat_fps_patch"] = FeatFpsPatch;
        r["feat_res_scaling"] = FeatResScaling;
        r["feat_tracing"] = FeatTracing;
        r["feat_watchdog"] = FeatWatchdog;
        r["feat_draw_prep"] = FeatDrawPrep;
        r["enabled_patches"] = new JsonArray([.. EnabledPatches.Select(p => (JsonNode)JsonValue.Create(p)!)]);
        r["fullscreen"] = Fullscreen;
        r["frame_stats"] = FrameStats;
        r["show_launcher"] = ShowLauncher;
        r["user_dir"] = UserDir;
        r["ini_migrated"] = IniMigrated;
        try
        {
            File.WriteAllText(path, r.ToJsonString(JsonOptions));
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static string Str(JsonObject r, string key, string fallback) =>
        r[key] is JsonValue v && v.GetValueKind() == JsonValueKind.String ? v.GetValue<string>() :
        r[key] is JsonValue n && n.GetValueKind() == JsonValueKind.Number ? n.ToJsonString() : fallback;

    private static bool Bool(JsonObject r, string key, bool fallback) =>
        r[key] is JsonValue v && v.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? v.GetValue<bool>() : fallback;

    private static string OneOf(string value, string[] allowed, string fallback) =>
        allowed.Contains(value) ? value : fallback;
}
