using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using BloodborneLauncher.Models;

namespace BloodborneLauncher.Services;

internal sealed record PreLaunchResult(bool Ready, string Error, IReadOnlyList<string> Details);

/// <summary>launcher.py verify_prelaunch: what run.bat needs before it can start the game.</summary>
internal static class PreLaunch
{
    public static async Task<PreLaunchResult> VerifyAsync(string gameDir, IDictionary<string, string> env)
    {
        var details = new List<string>();
        PreLaunchResult Fail(string error) => new(false, error, details);

        var eboot = Path.Combine(gameDir, "eboot.bin");
        if (!File.Exists(eboot))
        {
            return Fail($"Missing eboot.bin in game directory:\n{gameDir}\n\nPlease ensure your dumped game folder contains eboot.bin.");
        }
        var ebootSize = new FileInfo(eboot).Length;
        if (ebootSize == 0)
        {
            return Fail($"eboot.bin is empty (0 bytes) in:\n{eboot}");
        }
        details.Add(Inv($"[OK] Game executable: eboot.bin ({ebootSize / 1048576.0:0.0} MiB)"));

        // Game title and version (scripts/game_check.py), unless BB_SKIP_GAME_CHECK=1.
        if (env.TryGetValue("BB_SKIP_GAME_CHECK", out var skip) && skip == "1")
        {
            details.Add("[NOTE] Game version check skipped (BB_SKIP_GAME_CHECK=1)");
        }
        else
        {
            var check = await Task.Run(() => RunGameCheck(gameDir, env)).ConfigureAwait(true);
            if (check.Problem is { } problem)
            {
                return Fail(
                    $"Game File Verification Failed ({problem.Kind}):\n\n{problem.Explanation}\n\n" +
                    "Expected: Bloodborne CUSA03173 with Update 1.09 merged.\n" +
                    "Why blocked: Upstream port patches and memory hooks rely strictly on the 1.09 executable layout.\n" +
                    "To bypass (not recommended): set environment variable BB_SKIP_GAME_CHECK=1.");
            }
            details.Add(check.Note ?? "[OK] Game Title & Version: Bloodborne CUSA03173 v1.09 verified");
        }

        details.Add(Directory.Exists(Path.Combine(gameDir, "dvdroot_ps4"))
            ? "[OK] Game asset directory: dvdroot_ps4 detected"
            : "[NOTE] dvdroot_ps4 not detected directly under game root (custom layout or loose files)");

        if (!File.Exists(RepoPaths.RunBat))
        {
            return Fail($"Launcher script run.bat not found in:\n{RepoPaths.Root}");
        }
        details.Add("[OK] Launcher script: run.bat ready");

        var exe = new FileInfo(RepoPaths.BbportExe);
        if (!exe.Exists)
        {
            return Fail($"Compiled game executable 'bbport.exe' was not found in:\n{exe.FullName}\n\n" +
                        "The source code has not been compiled yet.\n" +
                        "Please run 'build.bat' in the project directory to compile the project before launching.");
        }
        if (exe.Length == 0)
        {
            return Fail($"Compiled 'bbport.exe' is empty (0 bytes) in:\n{exe.FullName}");
        }
        details.Add(Inv($"[OK] Compiled binary: bbport.exe ({exe.Length / 1048576.0:0.0} MiB, built {exe.LastWriteTime:yyyy-MM-dd HH:mm:ss})"));

        var dll = new FileInfo(Path.Combine(RepoPaths.OutDir, "bbgpu.dll"));
        var lib = new FileInfo(Path.Combine(RepoPaths.OutDir, "gpu", "libbbgpu.a"));
        details.Add(dll.Exists
            ? Inv($"[OK] GPU library: bbgpu.dll ({dll.Length / 1048576.0:0.0} MiB, built {dll.LastWriteTime:yyyy-MM-dd HH:mm:ss})")
            : lib.Exists
                ? Inv($"[OK] GPU subsystem: statically linked via libbbgpu.a ({lib.Length / 1048576.0:0.0} MiB, built {lib.LastWriteTime:yyyy-MM-dd HH:mm:ss})")
                : "[OK] GPU subsystem: statically linked into bbport.exe");

        details.Add(File.Exists(Path.Combine(RepoPaths.OutDir, "SDL3.dll"))
            ? "[OK] Runtime library: out/SDL3.dll staged"
            : "[WARN] Runtime library: out/SDL3.dll not staged in out/");

        if (!File.Exists(RepoPaths.PatchesPy))
        {
            return Fail($"Game patch script not found at:\n{RepoPaths.PatchesPy}");
        }
        details.Add("[OK] Patch engine: scripts/patches.py ready");

        if (NativeLibrary.TryLoad("vulkan-1.dll", out var vk))
        {
            NativeLibrary.Free(vk);
            details.Add("[OK] Vulkan runtime: vulkan-1.dll loaded successfully");
        }
        else
        {
            details.Add("[WARN] Vulkan runtime: vulkan-1.dll not found in standard system paths");
        }
        return new PreLaunchResult(true, "", details);
    }

    private sealed record GameProblem(string Kind, string Explanation);
    private sealed record GameCheck(GameProblem? Problem, string? Note);

    // game_check.problem() has no command line; call it the way launcher.py imported it.
    private const string CheckScript =
        "import sys\n" +
        "sys.path.insert(0, sys.argv[1])\n" +
        "import game_check as g\n" +
        "r = g.problem(sys.argv[2])\n" +
        "print('BBCHECK OK' if r is None else 'BBCHECK PROBLEM\\t' + r[0] + '\\t' + g.explain(*r).replace('\\n', ' '))\n";

    private static GameCheck RunGameCheck(string gameDir, IDictionary<string, string> env)
    {
        if (!File.Exists(RepoPaths.GameCheckPy))
        {
            return new GameCheck(null, "[NOTE] Game version check skipped: scripts/game_check.py not found");
        }
        var python = Python.Find(env);
        try
        {
            var psi = new ProcessStartInfo(python)
            {
                WorkingDirectory = RepoPaths.ScriptsDir,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
            };
            psi.ArgumentList.Add("-c");
            psi.ArgumentList.Add(CheckScript);
            psi.ArgumentList.Add(RepoPaths.ScriptsDir);
            psi.ArgumentList.Add(gameDir);
            psi.Environment["PYTHONIOENCODING"] = "utf-8";
            psi.Environment["PYTHONDONTWRITEBYTECODE"] = "1";
            using var p = Process.Start(psi);
            if (p is null)
            {
                return new GameCheck(null, "[NOTE] Game version check skipped: Python did not start");
            }
            p.StandardInput.Close();
            var stderrTask = p.StandardError.ReadToEndAsync();
            var stdout = p.StandardOutput.ReadToEnd();
            if (!p.WaitForExit(120_000))
            {
                p.Kill(true);
                return new GameCheck(null, "[NOTE] Game version check skipped: timed out");
            }
            var line = stdout.Split('\n').Select(l => l.Trim()).LastOrDefault(l => l.StartsWith("BBCHECK ", StringComparison.Ordinal));
            if (line == "BBCHECK OK")
            {
                return new GameCheck(null, null);
            }
            if (line is not null)
            {
                var parts = line.Split('\t');
                return new GameCheck(new GameProblem(parts.Length > 1 ? parts[1] : "unknown", parts.Length > 2 ? parts[2] : line), null);
            }
            var err = stderrTask.Result.Trim().Split('\n').LastOrDefault()?.Trim();
            return new GameCheck(null, $"[NOTE] Game version check skipped: {(string.IsNullOrEmpty(err) ? $"exit code {p.ExitCode}" : err)}");
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException or IOException)
        {
            return new GameCheck(null, $"[NOTE] Game version check skipped: {ex.Message}");
        }
    }

    private static string Inv(FormattableString s) => s.ToString(CultureInfo.InvariantCulture);
}

/// <summary>Finds Python the way run.bat does: %PYTHON%, the usual install folders, then
/// python.exe on PATH outside WindowsApps (the Store stub), then py.exe, then "python".</summary>
internal static class Python
{
    public static string Find(IDictionary<string, string> env)
    {
        if (env.TryGetValue("PYTHON", out var p) && !string.IsNullOrWhiteSpace(p))
        {
            return p.Trim('"');
        }
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        string[] candidates =
        [
            @"C:\Program Files\Python313\python.exe", @"C:\Program Files\Python312\python.exe",
            @"C:\Program Files\Python311\python.exe", @"C:\Program Files\Python310\python.exe",
            Path.Combine(local, @"Programs\Python\Python313\python.exe"), Path.Combine(local, @"Programs\Python\Python312\python.exe"),
            Path.Combine(local, @"Programs\Python\Python311\python.exe"), Path.Combine(local, @"Programs\Python\Python310\python.exe"),
        ];
        var found = candidates.FirstOrDefault(File.Exists);
        if (found is not null)
        {
            return found;
        }
        var windowsApps = Path.Combine(local, @"Microsoft\WindowsApps");
        var path = env.TryGetValue("PATH", out var pv) ? pv : Environment.GetEnvironmentVariable("PATH") ?? "";
        var dirs = path.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (var dir in dirs)
        {
            var exe = Path.Combine(dir.Trim('"'), "python.exe");
            if (File.Exists(exe) && !string.Equals(Path.GetFullPath(dir.Trim('"')).TrimEnd('\\'), windowsApps, StringComparison.OrdinalIgnoreCase))
            {
                return exe;
            }
        }
        foreach (var dir in dirs)
        {
            var exe = Path.Combine(dir.Trim('"'), "py.exe");
            if (File.Exists(exe))
            {
                return exe;
            }
        }
        return "python";
    }
}
