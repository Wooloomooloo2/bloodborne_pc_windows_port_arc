using System.Collections;
using System.Globalization;
using BloodborneLauncher.Models;
using BloodborneLauncher.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;

namespace BloodborneLauncher.ViewModels;

internal sealed record LaunchResult(bool Started, string? ErrorTitle = null, string? ErrorMessage = null);

internal sealed record GameExit(uint Code, bool UserStopped, IReadOnlyList<string> NewCrashLogs, TimeSpan RunTime,
    IReadOnlyList<string> LastLines)
{
    /// <summary>Ended with an error within seconds: run.bat or the port failed before the game ran.</summary>
    public bool FailedToStart => !UserStopped && Code != 0 && RunTime < TimeSpan.FromSeconds(5);

    /// <summary>Not a normal quit (0/1) and not stopped from the launcher.</summary>
    public bool Crashed => !UserStopped && Code is not 0 and not 1;
}

/// <summary>
/// Every setting on the launcher's page. Settings that bbport.ini holds are written to it
/// as they change (the engine reads them at start and rewrites the file from its menu);
/// the rest go to launcher_settings.json. Launching mirrors launcher.py launch_game.
/// </summary>
internal sealed partial class MainViewModel : ObservableObject
{
    private readonly LauncherSettings _s;
    private readonly BbIni _ini = new(RepoPaths.IniFile);
    private DateTime _iniStamp;
    private bool _loading;
    private GameProcess? _proc;
    private bool _userStopped;
    private HashSet<string> _crashLogsBefore = [];
    private DateTime _startedAt;

    public LogStore Log { get; }

    public event Action<GameExit>? GameExited;

    public MainViewModel(DispatcherQueue dispatcher)
    {
        Log = new LogStore(dispatcher);
        Log.GameLine += CheckFeatureWarnings;
        _s = LauncherSettings.Load(RepoPaths.SettingsFile);
        Migrate();

        _loading = true;
        WindowModeIndex = _s.Fullscreen ? 1 : 0;
        FpsIndex = Math.Max(0, Array.IndexOf(Choices.Fps, _s.Fps));
        AnisoIndex = Math.Max(0, Array.IndexOf(Choices.AnisoLabels, _s.Aniso));
        FrameStats = _s.FrameStats;
        Overlay = _s.FeatOverlay;
        FpsPatch = _s.FeatFpsPatch;
        Mods = _s.Mods;
        ResScaling = _s.FeatResScaling;
        Tracing = _s.FeatTracing;
        Watchdog = _s.FeatWatchdog;
        DrawPrep = _s.FeatDrawPrep;
        RenderResIndex = Math.Max(0, Array.IndexOf(Choices.RenderRes, _s.Res));
        TimeoutIndex = Math.Max(0, Array.IndexOf(Choices.Timeouts, _s.Timeout));
        ShowLauncher = _s.ShowLauncher;
        HideVk = _s.HideVk;
        Log.HideVk = _s.HideVk;
        _loading = false;

        LoadIni(force: true);
        UpdateGamePath();
        UpdateUserDir();
        UpdatePatchSummary();
        _ = Task.Run(GpuInfo.Describe).ContinueWith(t => dispatcher.TryEnqueue(() => GpuName = t.Result),
            TaskScheduler.Default);
    }

    // ------------------------------------------------------------ Display

#pragma warning disable CA1822 // x:Bind binds to instance members.
    public string[] OutputLabels => Choices.OutputLabels;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial int OutputResIndex { get; set; } = Choices.OutputDefault;

    partial void OnOutputResIndexChanged(int value)
    {
        if (value >= 0)
        {
            WriteIni("output_res", Choices.OutputValue(value));
        }
        OnPropertyChanged(nameof(RendersText));
    }

    [ObservableProperty]
    public partial int WindowModeIndex { get; set; }

    partial void OnWindowModeIndexChanged(int value) => SaveSetting(() => _s.Fullscreen = value == 1);

    [ObservableProperty]
    public partial int FpsIndex { get; set; }

    partial void OnFpsIndexChanged(int value) =>
        SaveSetting(value, () => _s.Fps = Choices.Fps[Math.Clamp(value, 0, Choices.Fps.Length - 1)]);

    // ------------------------------------------------------------ Upscaler

#pragma warning disable CA1822 // x:Bind binds to instance members.
    public string[] UpscalerLabels => Choices.UpscalerLabels;
    public string[] PresetLabels => Choices.Presets;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial int UpscalerIndex { get; set; }

    partial void OnUpscalerIndexChanged(int value)
    {
        if (value >= 0)
        {
            WriteIni("upscaler", Choices.Upscalers[value]);
            if (value != Choices.UpscalerOff)
            {
                // Temporal upscalers need the sub-pixel camera jitter.
                Jitter = true;
                WriteIni("jitter", "1");
            }
            SaveSetting(() => _s.FeatUpscaler = value != Choices.UpscalerOff);
        }
        OnPropertyChanged(nameof(IsUpscalerOn));
        OnPropertyChanged(nameof(IsPresetEnabled));
        OnPropertyChanged(nameof(IsTemporalReconstruction));
        OnPropertyChanged(nameof(IsXess));
        OnPropertyChanged(nameof(IsFsr4));
        OnPropertyChanged(nameof(RendersText));
        OnPropertyChanged(nameof(PresetDescription));
    }

    /// <summary>The upscalers that reconstruct from several frames (not TAA, not off): the
    /// reactive mask and the provider checks apply to them.</summary>
    public bool IsTemporalReconstruction => UpscalerIndex is not Choices.UpscalerOff and not Choices.UpscalerTaa;

    public bool IsXess => UpscalerIndex == Choices.UpscalerXess;

    public bool IsFsr4 => UpscalerIndex is 2 or 3;

    public bool IsUpscalerOn => UpscalerIndex != Choices.UpscalerOff;

    /// <summary>TAA renders at native size (BbSettings::RenderPreset), so the preset only
    /// matters for the upscalers.</summary>
    public bool IsPresetEnabled => UpscalerIndex is not Choices.UpscalerOff and not Choices.UpscalerTaa;

    public string PresetDescription => UpscalerIndex == Choices.UpscalerTaa
        ? "TAA renders at the output size" : "Render scale before upscaling";

    [ObservableProperty]
    public partial int PresetIndex { get; set; }

    partial void OnPresetIndexChanged(int value)
    {
        if (value >= 0)
        {
            WriteIni("preset", value.ToString(CultureInfo.InvariantCulture));
        }
        OnPropertyChanged(nameof(RendersText));
    }

    [ObservableProperty]
    public partial bool Sharpen { get; set; } = true;

    partial void OnSharpenChanged(bool value) => WriteIni("sharpen", value ? "1" : "0");

    [ObservableProperty]
    public partial double Sharpness { get; set; } = 0.3;

    partial void OnSharpnessChanged(double value)
    {
        WriteIni("sharpness", Math.Clamp(value, 0, 2).ToString("0.00", CultureInfo.InvariantCulture));
        OnPropertyChanged(nameof(SharpnessText));
    }

    public string SharpnessText => Sharpness.ToString("0.00", CultureInfo.InvariantCulture);

    public string RendersText
    {
        get
        {
            var output = Choices.Outputs[Math.Clamp(OutputResIndex, 0, Choices.Outputs.Length - 1)];
            if (!IsPresetEnabled)
            {
                return $"{output.W} × {output.H} (native)";
            }
            var r = Choices.ForPreset(PresetIndex, output);
            return $"{r.W} × {r.H} → {output.W} × {output.H}";
        }
    }

    // In-game menu settings (bbport.ini, BbSettings): the same keys the overlay writes.

    [ObservableProperty]
    public partial bool Jitter { get; set; } = true;

    partial void OnJitterChanged(bool value) => WriteIni("jitter", value);

    [ObservableProperty]
    public partial bool ObjectMotion { get; set; }

    partial void OnObjectMotionChanged(bool value) => WriteIni("object_motion", value);

    [ObservableProperty]
    public partial bool Reactive { get; set; }

    partial void OnReactiveChanged(bool value) => WriteIni("reactive", value);

    [ObservableProperty]
    public partial double ReactiveScale { get; set; } = 1.0;

    partial void OnReactiveScaleChanged(double value)
    {
        WriteIni("reactive_scale", Fixed(Math.Clamp(value, 0, 4)));
        OnPropertyChanged(nameof(ReactiveScaleText));
    }

    public string ReactiveScaleText => Fixed(ReactiveScale);

    [ObservableProperty]
    public partial double ReactiveThreshold { get; set; } = 0.2;

    partial void OnReactiveThresholdChanged(double value)
    {
        WriteIni("reactive_threshold", Fixed(Math.Clamp(value, 0, 1)));
        OnPropertyChanged(nameof(ReactiveThresholdText));
    }

    public string ReactiveThresholdText => Fixed(ReactiveThreshold);

    [ObservableProperty]
    public partial double ReactiveMax { get; set; } = 0.9;

    partial void OnReactiveMaxChanged(double value)
    {
        WriteIni("reactive_max", Fixed(Math.Clamp(value, 0, 1)));
        OnPropertyChanged(nameof(ReactiveMaxText));
    }

    public string ReactiveMaxText => Fixed(ReactiveMax);

    [ObservableProperty]
    public partial bool XessInvertJitter { get; set; }

    partial void OnXessInvertJitterChanged(bool value) => WriteIni("xess_invert_jitter", value);

    [ObservableProperty]
    public partial bool Fsr4AutoExposure { get; set; } = true;

    partial void OnFsr4AutoExposureChanged(bool value) => WriteIni("fsr4_auto_exposure", value);

    [ObservableProperty]
    public partial bool Fsr4InvertJitter { get; set; }

    partial void OnFsr4InvertJitterChanged(bool value) => WriteIni("fsr4_invert_jitter", value);

#pragma warning disable CA1822 // x:Bind binds to instance members.
    public string[] LiveResolutionLabels => Choices.LiveResolutionLabels;
    public string[] ModelLodLabels => Choices.ModelLodLabels;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial int LiveResolutionIndex { get; set; }

    partial void OnLiveResolutionIndexChanged(int value)
    {
        if (value >= 0)
        {
            WriteIni("live_resolution", Choices.LiveResolution[Math.Clamp(value, 0, Choices.LiveResolution.Length - 1)]);
        }
    }

    [ObservableProperty]
    public partial int ModelLodIndex { get; set; } = 1;

    partial void OnModelLodIndexChanged(int value)
    {
        if (value >= 0)
        {
            WriteIni("model_lod", Choices.ModelLod[Math.Clamp(value, 0, Choices.ModelLod.Length - 1)]
                .ToString(CultureInfo.InvariantCulture));
        }
    }

    private static string Fixed(double v) => v.ToString("0.00", CultureInfo.InvariantCulture);

    // ------------------------------------------------------------ Advanced

    [ObservableProperty]
    public partial string GpuName { get; set; } = "Detecting…";

#pragma warning disable CA1822 // x:Bind binds to instance members.
    public string[] AnisoLabels => Choices.AnisoLabels;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial int AnisoIndex { get; set; }

    partial void OnAnisoIndexChanged(int value) =>
        SaveSetting(value, () => _s.Aniso = Choices.AnisoLabels[Math.Clamp(value, 0, Choices.AnisoLabels.Length - 1)]);

    [ObservableProperty]
    public partial bool FrameStats { get; set; }

    partial void OnFrameStatsChanged(bool value) => SaveSetting(() => _s.FrameStats = value);

    [ObservableProperty]
    public partial bool Overlay { get; set; }

    partial void OnOverlayChanged(bool value) => SaveFeature("In-game overlay menu", value, () => _s.FeatOverlay = value);

    [ObservableProperty]
    public partial bool FpsPatch { get; set; }

    partial void OnFpsPatchChanged(bool value) => SaveFeature("FPS patch", value, () => _s.FeatFpsPatch = value);

    [ObservableProperty]
    public partial bool Mods { get; set; }

    partial void OnModsChanged(bool value) => SaveFeature("Mods folder", value, () => _s.Mods = value);

    [ObservableProperty]
    public partial bool ResScaling { get; set; }

    partial void OnResScalingChanged(bool value) => SaveFeature("Resolution scaling", value, () => _s.FeatResScaling = value);

#pragma warning disable CA1822 // x:Bind binds to instance members.
    public string[] RenderResLabels => Choices.RenderRes;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial int RenderResIndex { get; set; }

    partial void OnRenderResIndexChanged(int value) =>
        SaveSetting(value, () => _s.Res = Choices.RenderRes[Math.Clamp(value, 0, Choices.RenderRes.Length - 1)]);

    [ObservableProperty]
    public partial bool Tracing { get; set; }

    partial void OnTracingChanged(bool value) => SaveFeature("Debug tracing", value, () => _s.FeatTracing = value);

    [ObservableProperty]
    public partial bool Watchdog { get; set; }

    partial void OnWatchdogChanged(bool value) => SaveFeature("Watchdog", value, () => _s.FeatWatchdog = value);

#pragma warning disable CA1822 // x:Bind binds to instance members.
    public string[] TimeoutLabels => Choices.TimeoutLabels;
#pragma warning restore CA1822

    [ObservableProperty]
    public partial int TimeoutIndex { get; set; }

    partial void OnTimeoutIndexChanged(int value) =>
        SaveSetting(value, () => _s.Timeout = Choices.Timeouts[Math.Clamp(value, 0, Choices.Timeouts.Length - 1)]);

    [ObservableProperty]
    public partial bool DrawPrep { get; set; }

    partial void OnDrawPrepChanged(bool value) => SaveFeature("GPU draw prep workers", value, () => _s.FeatDrawPrep = value);

    [ObservableProperty]
    public partial string PatchSummary { get; set; } = "";

    private void UpdatePatchSummary() => PatchSummary = _s.EnabledPatches.Count switch
    {
        0 => "No community patches enabled",
        1 => $"1 enabled: {_s.EnabledPatches.Min}",
        var n => $"{n} enabled",
    };

    /// <summary>The patch list for the manager, ticked from the saved set.</summary>
    public List<PatchItem> LoadPatches()
    {
        var list = XmlPatches.Load(RepoPaths.PatchesXml);
        foreach (var p in list)
        {
            p.IsEnabled = _s.EnabledPatches.Contains(p.Name);
        }
        return list;
    }

    public void SavePatches(IEnumerable<PatchItem> items)
    {
        var known = items.ToList();
        var names = new HashSet<string>(known.Select(p => p.Name), StringComparer.Ordinal);
        // Names the XML no longer lists stay as they were.
        var kept = _s.EnabledPatches.Where(n => !names.Contains(n));
        _s.EnabledPatches = new SortedSet<string>(kept.Concat(known.Where(p => p.IsEnabled).Select(p => p.Name)), StringComparer.Ordinal);
        _s.Save(RepoPaths.SettingsFile);
        UpdatePatchSummary();
        Log.Log($"[PATCHES] Saved {_s.EnabledPatches.Count} active patch(es): " +
                (_s.EnabledPatches.Count > 0 ? string.Join(", ", _s.EnabledPatches) : "None"));
    }

    // ------------------------------------------------------------ Effects

    [ObservableProperty] public partial bool Dof { get; set; }
    partial void OnDofChanged(bool value) => WriteIni("effect_dof", value);

    [ObservableProperty] public partial bool MotionBlur { get; set; }
    partial void OnMotionBlurChanged(bool value) => WriteIni("effect_motion_blur", value);

    [ObservableProperty] public partial bool ChromaticAberration { get; set; }
    partial void OnChromaticAberrationChanged(bool value) => WriteIni("effect_chromatic_aberration", value);

    [ObservableProperty] public partial bool Ssao { get; set; }
    partial void OnSsaoChanged(bool value) => WriteIni("effect_ssao", value);

    [ObservableProperty] public partial bool DynamicShadows { get; set; }
    partial void OnDynamicShadowsChanged(bool value) => WriteIni("effect_dynamic_shadows", value);

    [ObservableProperty] public partial bool Ssr { get; set; }
    partial void OnSsrChanged(bool value) => WriteIni("effect_ssr", value);

    [ObservableProperty] public partial bool GameAa { get; set; }
    partial void OnGameAaChanged(bool value) => WriteIni("effect_game_aa", value);

    [ObservableProperty] public partial bool ShowFps { get; set; }
    partial void OnShowFpsChanged(bool value) => WriteIni("show_fps", value);

    [ObservableProperty] public partial bool SkipIntro { get; set; }
    partial void OnSkipIntroChanged(bool value) => WriteIni("skip_intro", value);

    [ObservableProperty] public partial bool DebugCamera { get; set; }
    partial void OnDebugCameraChanged(bool value) => WriteIni("debug_camera", value);

    [ObservableProperty] public partial bool DebugMenu { get; set; }
    partial void OnDebugMenuChanged(bool value) => WriteIni("debug_menu", value);

    // ------------------------------------------------------------ Locations

    [ObservableProperty]
    public partial string GameDirText { get; set; } = "";

    [ObservableProperty]
    public partial string GameDirError { get; set; } = "";

    [ObservableProperty]
    public partial bool HasGameDirError { get; set; }

    [ObservableProperty]
    public partial bool IsGameDirCustom { get; set; }

    /// <summary>The game folder the launcher would start, or null.</summary>
    public string? GameDir => RepoPaths.ResolveGameDir(string.IsNullOrWhiteSpace(_s.Eboot) ? RepoPaths.DefaultGameDir : _s.Eboot);

    /// <summary>A picked eboot.bin or folder; false (unchanged) when it holds no eboot.bin.</summary>
    public bool SetGamePath(string path)
    {
        if (RepoPaths.ResolveGameDir(path) is null)
        {
            GameDirError = $"No eboot.bin in {path}";
            HasGameDirError = true;
            return false;
        }
        SaveSetting(() => _s.Eboot = path);
        UpdateGamePath();
        Log.Log($"Selected game: {path}");
        return true;
    }

    public void ResetGamePath()
    {
        SaveSetting(() => _s.Eboot = "");
        UpdateGamePath();
    }

    private void UpdateGamePath()
    {
        var target = string.IsNullOrWhiteSpace(_s.Eboot) ? RepoPaths.DefaultGameDir : _s.Eboot;
        IsGameDirCustom = !string.IsNullOrWhiteSpace(_s.Eboot);
        var dir = RepoPaths.ResolveGameDir(target);
        GameDirText = dir ?? RepoPaths.Resolve(target, target);
        HasGameDirError = dir is null;
        GameDirError = dir is null ? "eboot.bin not found here: choose the game folder (CUSA03173 with update 1.09)" : "";
    }

    [ObservableProperty]
    public partial string UserDirText { get; set; } = "";

    [ObservableProperty]
    public partial bool IsUserDirCustom { get; set; }

    public string UserDirStart => string.IsNullOrWhiteSpace(_s.UserDir) ? RepoPaths.DefaultUserDir : _s.UserDir;

    public void SetUserDir(string? path)
    {
        SaveSetting(() => _s.UserDir = path ?? "");
        UpdateUserDir();
    }

    private void UpdateUserDir()
    {
        IsUserDirCustom = !string.IsNullOrWhiteSpace(_s.UserDir);
        UserDirText = IsUserDirCustom ? RepoPaths.Resolve(_s.UserDir, _s.UserDir) : RepoPaths.DefaultUserDir;
    }

    // ------------------------------------------------------------ Footer

    [ObservableProperty]
    public partial bool ShowLauncher { get; set; } = true;

    partial void OnShowLauncherChanged(bool value) => SaveSetting(() => _s.ShowLauncher = value);

    [ObservableProperty]
    public partial bool HideVk { get; set; }

    partial void OnHideVkChanged(bool value)
    {
        Log.HideVk = value;
        SaveSetting(() => _s.HideVk = value);
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlayText), nameof(CanEdit), nameof(CanPlay))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CanPlay))]
    public partial bool IsChecking { get; set; }

    public string PlayText => IsRunning ? "Stop" : "Play";
    public bool CanPlay => !IsChecking;
    public bool CanEdit => !IsRunning;

    // ------------------------------------------------------------ ini / settings

    /// <summary>An engine feature switch; the change goes in the log, so an unexpected one can be traced.</summary>
    private void SaveFeature(string label, bool value, Action change)
    {
        if (_loading)
        {
            return;
        }
        SaveSetting(change);
        Log.Log($"[SETTINGS] {label}: {(value ? "on" : "off")}");
    }

    /// <summary>A ComboBox index: -1 (no selection, while its items load) changes nothing.</summary>
    private void SaveSetting(int index, Action change)
    {
        if (index >= 0)
        {
            SaveSetting(change);
        }
    }

    private void SaveSetting(Action change)
    {
        if (_loading)
        {
            return;
        }
        change();
        _s.Save(RepoPaths.SettingsFile);
    }

    private void WriteIni(string key, bool value) => WriteIni(key, value ? "1" : "0");

    private void WriteIni(string key, string value)
    {
        if (_loading)
        {
            return;
        }
        _ini.Update(i => i.Set(key, value));
        _iniStamp = Stamp();
    }

    private static DateTime Stamp()
    {
        try
        {
            return File.GetLastWriteTimeUtc(RepoPaths.IniFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return default;
        }
    }

    /// <summary>Re-reads bbport.ini (the in-game menu may have changed it).</summary>
    public void LoadIni(bool force = false)
    {
        var stamp = Stamp();
        if (!force && stamp == _iniStamp)
        {
            return;
        }
        _iniStamp = stamp;
        _ini.Reload();
        _loading = true;
        try
        {
            // An unlisted value (fsr411) shows as its family and is only rewritten when changed.
            UpscalerIndex = Choices.UpscalerIndex(_ini.Get("upscaler"));
            PresetIndex = Math.Clamp(Int(_ini.Get("preset"), 0), 0, Choices.Presets.Length - 1);
            Sharpen = Int(_ini.Get("sharpen"), 1) != 0;
            Sharpness = Math.Clamp(Double(_ini.Get("sharpness"), 0.3), 0, 2);
            OutputResIndex = Choices.OutputIndex(_ini.Get("output_res"));
            Dof = Flag("effect_dof", true);
            MotionBlur = Flag("effect_motion_blur", true);
            ChromaticAberration = Flag("effect_chromatic_aberration", true);
            Ssao = Flag("effect_ssao", true);
            DynamicShadows = Flag("effect_dynamic_shadows", true);
            Ssr = Flag("effect_ssr", false);
            GameAa = Flag("effect_game_aa", true);
            ShowFps = Flag("show_fps", false);
            SkipIntro = Flag("skip_intro", false);
            DebugCamera = Flag("debug_camera", false);
            DebugMenu = Flag("debug_menu", false);
            Jitter = Flag("jitter", false);
            ObjectMotion = Flag("object_motion", false);
            Reactive = Flag("reactive", false);
            ReactiveScale = Math.Clamp(Double(_ini.Get("reactive_scale"), 1.0), 0, 4);
            ReactiveThreshold = Math.Clamp(Double(_ini.Get("reactive_threshold"), 0.2), 0, 1);
            ReactiveMax = Math.Clamp(Double(_ini.Get("reactive_max"), 0.9), 0, 1);
            XessInvertJitter = Flag("xess_invert_jitter", false);
            Fsr4AutoExposure = Flag("fsr4_auto_exposure", true);
            Fsr4InvertJitter = Flag("fsr4_invert_jitter", false);
            LiveResolutionIndex = Choices.LiveResolutionIndex(_ini.Get("live_resolution"));
            var lod = Array.IndexOf(Choices.ModelLod, Math.Clamp(Int(_ini.Get("model_lod"), 0), -2, 2));
            ModelLodIndex = lod >= 0 ? lod : 1;
        }
        finally
        {
            _loading = false;
        }
        _s.FeatUpscaler = UpscalerIndex != Choices.UpscalerOff;
    }

    private bool Flag(string key, bool fallback) => Int(_ini.Get(key), fallback ? 1 : 0) != 0;

    private static int Int(string? v, int fallback) =>
        int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : fallback;

    private static double Double(string? v, double fallback) =>
        double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : fallback;

    /// <summary>
    /// bbport.ini is the one place for the effects: enabled_patches entries that patches.py
    /// derives from the ini (EFFECTS) become ini keys and leave the list. Once, the old
    /// "Upscaler" feature switch carries over: unticked meant BB_UPSCALER=off whatever the
    /// ini said, so the ini's upscaler becomes off.
    /// </summary>
    private void Migrate()
    {
        var moved = new List<string>();
        var upscalerOff = false;
        var upscalerCarry = !_s.IniMigrated && _s.HadFeatUpscaler && !_s.FeatUpscaler;
        if (!upscalerCarry && !_s.EnabledPatches.Overlaps(Choices.IniManagedPatches))
        {
            if (!_s.IniMigrated)
            {
                _s.IniMigrated = true;
                _s.Save(RepoPaths.SettingsFile);
            }
            return;
        }
        _ini.Update(ini =>
        {
            foreach (var (key, off, on) in Choices.EffectPatches)
            {
                if (off is not null && _s.EnabledPatches.Remove(off))
                {
                    ini.Set(key, "0");
                    moved.Add($"{off} → {key}=0");
                }
                if (on is not null && _s.EnabledPatches.Remove(on))
                {
                    ini.Set(key, "1");
                    moved.Add($"{on} → {key}=1");
                }
            }
            if (upscalerCarry && Choices.UpscalerIndex(ini.Get("upscaler")) != Choices.UpscalerOff)
            {
                ini.Set("upscaler", "off");
                upscalerOff = true;
            }
        });
        _s.IniMigrated = true;
        _s.Save(RepoPaths.SettingsFile);
        foreach (var m in moved)
        {
            Log.Log($"[SETTINGS] Moved patch to bbport.ini: {m}");
        }
        if (upscalerOff)
        {
            Log.Log("[SETTINGS] The launcher's Upscaler switch was off: bbport.ini upscaler set to off.");
        }
    }

    // ------------------------------------------------------------ launch

    /// <summary>The environment for run.bat: the launcher's own (so variables set by
    /// "Play Bloodborne.bat" pass through), overridden by the launcher's settings.</summary>
    private Dictionary<string, string> BuildEnv(string gameDir, out string resChoice)
    {
        var env = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry e in Environment.GetEnvironmentVariables())
        {
            if (e.Key is string k && e.Value is string v)
            {
                env[k] = v;
            }
        }
        void Feature(bool on, string name, string offValue)
        {
            if (on)
            {
                env.Remove(name);
            }
            else
            {
                env[name] = offValue;
            }
        }
        env["BB_GAME_DIR"] = gameDir;
        env["BB_FPS"] = _s.Fps;
        env["BB_MODS_ENABLED"] = _s.Mods ? "1" : "0";
        Feature(IsUpscalerOn, "BB_UPSCALER", "off");
        // Object motion is a bbport.ini setting now (the launcher's switch writes it).
        env.Remove("BB_OBJECT_MOTION");
        Feature(_s.FeatOverlay, "BB_OVERLAY", "0");
        Feature(_s.FeatFpsPatch, "BB_FPS_PATCH", "0");
        env.Remove("BB_RENDER_RES");
        resChoice = _s.Res;
        if (_s.FeatResScaling)
        {
            if (_s.Res != Choices.RenderRes[0])
            {
                env["BB_RENDER_RES"] = _s.Res;
            }
        }
        else
        {
            resChoice = "Native (1080p, scaling off)";
        }
        env["BB_TRACE"] = _s.FeatTracing ? "1" : "0";
        env["BB_TIMEOUT"] = _s.Timeout;
        Feature(_s.FeatWatchdog, "BB_WATCHDOG", "0");
        Feature(_s.FeatDrawPrep, "BB_PREP_WORKERS", "0");
        env.TryAdd("BB_FRAMES_AHEAD", "2");
        env["BB_ANISO"] = Choices.AnisoValues[Math.Max(0, Array.IndexOf(Choices.AnisoLabels, _s.Aniso))];
        if (_s.EnabledPatches.Count > 0)
        {
            env["BB_PATCHES"] = string.Join(";", _s.EnabledPatches);
        }
        else
        {
            env.Remove("BB_PATCHES");
        }
        env["BB_FULLSCREEN"] = _s.Fullscreen ? "1" : "0";
        // The engine checks BB_FRAME_STATS for presence, so off removes it.
        if (_s.FrameStats)
        {
            env["BB_FRAME_STATS"] = "1";
        }
        else
        {
            env.Remove("BB_FRAME_STATS");
        }
        if (!string.IsNullOrWhiteSpace(_s.UserDir))
        {
            env["BB_USER_DIR"] = RepoPaths.Resolve(_s.UserDir, _s.UserDir);
        }
        RunBat.PrepareEnvironment(env);
        return env;
    }

    public async Task<LaunchResult> LaunchAsync()
    {
        if (IsRunning || IsChecking)
        {
            return new LaunchResult(false);
        }
        var target = string.IsNullOrWhiteSpace(_s.Eboot) ? RepoPaths.DefaultGameDir : _s.Eboot;
        var gameDir = RepoPaths.ResolveGameDir(target);
        if (gameDir is null)
        {
            return new LaunchResult(false, "Game not found", $"Could not find eboot.bin in:\n{RepoPaths.Resolve(target, target)}\n\nChoose the game folder under Locations.");
        }
        LoadIni();
        var env = BuildEnv(gameDir, out var resChoice);

        IsChecking = true;
        PreLaunchResult check;
        try
        {
            check = await PreLaunch.VerifyAsync(gameDir, env).ConfigureAwait(true);
        }
        finally
        {
            IsChecking = false;
        }
        if (!check.Ready)
        {
            Log.Log($"[PRE-LAUNCH ERROR] Verification failed: {check.Error.Split('\n')[0]}");
            return new LaunchResult(false, "Pre-launch verification failed", check.Error);
        }
        Log.Log("[PRE-LAUNCH] Pre-flight verification passed:");
        foreach (var line in check.Details)
        {
            Log.Log($"  {line}");
        }

        Log.OpenFile(RepoPaths.LogFile);
        var fsr = IsUpscalerOn;
        string OnOff(bool b) => b ? "on" : "off";
        var patches = _s.EnabledPatches.ToList();
        Log.Log(new string('=', 50));
        Log.Log($"Starting Bloodborne from: {gameDir}   [{DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture)}]");
        Log.Log($"FPS: {_s.Fps} | Res: {resChoice} | Mods: {env["BB_MODS_ENABLED"]}");
        Log.Log($"Features: FSR={OnOff(fsr)}, ObjectMotion={OnOff(ObjectMotion)}, Overlay={OnOff(_s.FeatOverlay)}, " +
                $"FPSPatch={OnOff(_s.FeatFpsPatch)}, Mods={OnOff(_s.Mods)}, ResScaling={OnOff(_s.FeatResScaling)}, " +
                $"Tracing={OnOff(_s.FeatTracing)}, Watchdog={OnOff(_s.FeatWatchdog)}, DrawPrep={OnOff(_s.FeatDrawPrep)}");
        Log.Log($"XML Patches ({patches.Count} active): {(patches.Count > 0 ? string.Join(", ", patches) : "None")}");
        Log.Log(new string('=', 50));
        Log.Log($"Port folder: {RepoPaths.Root}");

        _crashLogsBefore = CrashLogs();
        _userStopped = false;
        try
        {
            _proc = GameProcess.Start(RunBat.CommandLine(RepoPaths.RunBat), RepoPaths.Root, env);
        }
        catch (GameProcess.Win32Exception ex)
        {
            Log.Log($"Launch Error: {ex.Message}");
            Log.CloseFile();
            return new LaunchResult(false, "Could not start the game", ex.Message);
        }
        _proc.Line += Log.Enqueue;
        var proc = _proc;
        _proc.Exited += code => Log.AfterQueued(() => OnFinished(proc, code));
        _proc.BeginRead();
        _startedAt = DateTime.UtcNow;
        IsRunning = true;
        return new LaunchResult(true);
    }

    public void Stop()
    {
        if (_proc is { } proc && !proc.HasExited)
        {
            Log.Log("Stopping game process tree...");
            _userStopped = true;
            proc.Kill();
        }
    }

    /// <summary>On launcher exit: the job dies with the process anyway; this makes it explicit.</summary>
    public void Shutdown()
    {
        _proc?.Kill();
        // Every change was saved as it was made; saving the whole set again here could
        // overwrite edits made to the file meanwhile (another launcher, launcher.py, by hand).
        Log.Dispose();
    }

    private void OnFinished(GameProcess proc, uint rc)
    {
        if (Log.HiddenCount > 0)
        {
            Log.Log($"({Log.HiddenCount} Vulkan warning lines hidden; full output is in launcher.log)");
        }
        var code = rc < 256 ? rc.ToString(CultureInfo.InvariantCulture)
            : string.Create(CultureInfo.InvariantCulture, $"{rc} (0x{rc:X8})");
        Log.Log($"Process finished with code: {code}");
        Log.CloseFile();
        proc.Dispose();
        if (ReferenceEquals(_proc, proc))
        {
            _proc = null;
        }
        IsRunning = false;
        LoadIni();
        var newLogs = CrashLogs().Except(_crashLogsBefore, StringComparer.OrdinalIgnoreCase).Order(StringComparer.Ordinal).ToList();
        var last = Log.Visible.Skip(Math.Max(0, Log.Visible.Count - 8)).ToList();
        GameExited?.Invoke(new GameExit(rc, _userStopped, newLogs, DateTime.UtcNow - _startedAt, last));
    }

    private static HashSet<string> CrashLogs()
    {
        try
        {
            return Directory.Exists(RepoPaths.OutDir)
                ? new HashSet<string>(Directory.GetFiles(RepoPaths.OutDir, "crash_*.log"), StringComparer.OrdinalIgnoreCase)
                : [];
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return [];
        }
    }

    private void CheckFeatureWarnings(string line)
    {
        if (!IsUpscalerOn && line.Contains("Upscaler: FSR", StringComparison.Ordinal) && line.Contains("available (on)", StringComparison.Ordinal))
        {
            Log.Log($"[WARNING] Feature switch discrepancy: Upscaler is toggled OFF but startup reported: {line.Trim()}");
        }
        if (!ObjectMotion && line.Contains("Object motion: on", StringComparison.Ordinal))
        {
            Log.Log($"[WARNING] Feature switch discrepancy: Object motion is toggled OFF but startup reported: {line.Trim()}");
        }
        if (!_s.FeatOverlay && line.Contains("Overlay: menu ready", StringComparison.Ordinal))
        {
            Log.Log($"[WARNING] Feature switch discrepancy: Overlay menu is toggled OFF but startup reported: {line.Trim()}");
        }
        if (!_s.FeatFpsPatch && line.Contains("writes from ['", StringComparison.Ordinal) && line.Contains("FPS++", StringComparison.Ordinal))
        {
            Log.Log($"[WARNING] Feature switch discrepancy: FPS patch is toggled OFF but patches reported: {line.Trim()}");
        }
    }

    /// <summary>--selftest-launch (see <see cref="RunBat.SelfTest"/>).</summary>
    public int SelfTestLaunch() => RunBat.SelfTest(BuildEnv(GameDir ?? RepoPaths.DefaultGameDir, out _));

    /// <summary>launcher.py verify_prelaunch on demand, into the log.</summary>
    public async Task<PreLaunchResult?> VerifyAsync()
    {
        var gameDir = GameDir;
        if (gameDir is null)
        {
            Log.Log("[PRE-LAUNCH ERROR] No eboot.bin in the game folder.");
            return null;
        }
        Log.Log("--- Running Pre-Launch Verification Check ---");
        var result = await PreLaunch.VerifyAsync(gameDir, BuildEnv(gameDir, out _)).ConfigureAwait(true);
        foreach (var line in result.Details)
        {
            Log.Log($"  {line}");
        }
        Log.Log(result.Ready ? "[PRE-LAUNCH] All checks PASSED! System and game files are ready."
            : $"[PRE-LAUNCH ERROR] {result.Error.Split('\n')[0]}");
        return result;
    }
}
