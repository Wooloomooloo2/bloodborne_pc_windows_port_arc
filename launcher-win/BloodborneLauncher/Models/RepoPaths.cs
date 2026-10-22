namespace BloodborneLauncher.Models;

/// <summary>
/// Where the port's files live. The launcher runs from out\launcher (or bin\ during
/// development), so the repository root is the nearest parent holding run.bat and scripts\.
/// Relative paths resolve the way run.bat resolves them: against the repository root,
/// which is the working directory the game is started in. BB_PORT_DIR overrides the search.
/// </summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string RunBat => Path.Combine(Root, "run.bat");
    public static string BuildBat => Path.Combine(Root, "build.bat");
    public static string LogFile => Path.Combine(Root, "launcher.log");
    public static string SettingsFile => Path.Combine(Root, "launcher_settings.json");
    public static string PatchesXml => Path.Combine(Root, "patches", "Bloodborne.xml");
    public static string ScriptsDir => Path.Combine(Root, "scripts");
    public static string PatchesPy => Path.Combine(ScriptsDir, "patches.py");
    public static string GameCheckPy => Path.Combine(ScriptsDir, "game_check.py");

    /// <summary>run.bat checks out\bbport.exe relative to the repository root.</summary>
    public static string OutDir => Path.Combine(Root, "out");
    public static string BbportExe => Path.Combine(OutDir, "bbport.exe");

    /// <summary>BB_DATA_DIR (default "."): out\, user\ and bbport.ini.</summary>
    public static string DataDir { get; } = Resolve(Environment.GetEnvironmentVariable("BB_DATA_DIR"), ".");

    /// <summary>BB_CONFIG, else BB_DATA_DIR\bbport.ini (run.bat).</summary>
    public static string IniFile { get; } = ResolveOr(Environment.GetEnvironmentVariable("BB_CONFIG"),
        Path.Combine(DataDir, "bbport.ini"));

    /// <summary>The save folder run.bat uses when the launcher sets none: BB_USER_DIR from the
    /// environment the launcher was started in, else BB_DATA_DIR\user.</summary>
    public static string DefaultUserDir { get; } = ResolveOr(Environment.GetEnvironmentVariable("BB_USER_DIR"),
        Path.Combine(DataDir, "user"));

    /// <summary>BB_GAME_DIR, else ..\CUSA03173 next to the repository (run.bat).</summary>
    public static string DefaultGameDir { get; } = Resolve(Environment.GetEnvironmentVariable("BB_GAME_DIR"), @"..\CUSA03173");

    public static string Resolve(string? value, string fallback)
    {
        var v = string.IsNullOrWhiteSpace(value) ? fallback : value.Trim().Trim('"');
        try
        {
            return Path.GetFullPath(Path.Combine(Root, v));
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return v;
        }
    }

    private static string ResolveOr(string? value, string fullFallback) =>
        string.IsNullOrWhiteSpace(value) ? Path.GetFullPath(fullFallback) : Resolve(value, value);

    private static string FindRoot()
    {
        var overrideDir = Environment.GetEnvironmentVariable("BB_PORT_DIR");
        if (!string.IsNullOrWhiteSpace(overrideDir) && File.Exists(Path.Combine(overrideDir.Trim('"'), "run.bat")))
        {
            return Path.GetFullPath(overrideDir.Trim('"')).TrimEnd(Path.DirectorySeparatorChar);
        }
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "run.bat")) &&
                Directory.Exists(Path.Combine(dir.FullName, "scripts")))
            {
                return dir.FullName.TrimEnd(Path.DirectorySeparatorChar);
            }
        }
        return AppContext.BaseDirectory.TrimEnd(Path.DirectorySeparatorChar);
    }

    /// <summary>The game folder for an eboot.bin path or a folder holding eboot.bin, else null.</summary>
    public static string? ResolveGameDir(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return null;
        }
        var p = Resolve(target, target);
        if (File.Exists(p) && string.Equals(Path.GetFileName(p), "eboot.bin", StringComparison.OrdinalIgnoreCase))
        {
            return Path.GetDirectoryName(p);
        }
        if (Directory.Exists(p) && File.Exists(Path.Combine(p, "eboot.bin")))
        {
            return p.TrimEnd(Path.DirectorySeparatorChar);
        }
        return null;
    }
}
