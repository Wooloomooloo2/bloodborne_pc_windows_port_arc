using System.Globalization;
using System.Text;
using BloodborneLauncher.Models;

namespace BloodborneLauncher.Services;

/// <summary>How run.bat is started: cmd.exe with the script's absolute path, from the
/// repository root.</summary>
internal static class RunBat
{
    public static string CmdExe => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    /// <summary><c>"cmd.exe" /d /c ""C:\...\script.bat""</c>: cmd /c strips the outer pair
    /// of quotes, leaving the quoted path (safe with spaces).</summary>
    public static string CommandLine(string script) => $"\"{CmdExe}\" /d /c \"\"{script}\"\"";

    /// <summary>
    /// NoDefaultCurrentDirectoryInExePath (set by some terminals and tools) stops cmd finding
    /// programs in the current directory, which run.bat relies on ("call build.bat"); it
    /// also made a bare "run.bat" fail with "not recognized". The game never wants it.
    /// </summary>
    public static void PrepareEnvironment(IDictionary<string, string> env) =>
        env.Remove("NoDefaultCurrentDirectoryInExePath");

    /// <summary>
    /// --selftest-launch: runs a harmless batch file through exactly the launch path (job
    /// object, command-line form, working directory, environment) and writes what it saw to
    /// launcher-selftest.log in the repository root. Never starts the game.
    /// </summary>
    public static int SelfTest(IDictionary<string, string> env)
    {
        var report = new StringBuilder();
        report.AppendLine(CultureInfo.InvariantCulture, $"Port folder: {RepoPaths.Root}");
        report.AppendLine(CultureInfo.InvariantCulture, $"run.bat: {RepoPaths.RunBat} (exists: {File.Exists(RepoPaths.RunBat)})");
        report.AppendLine(CultureInfo.InvariantCulture, $"launcher.log: {RepoPaths.LogFile}");
        report.AppendLine(CultureInfo.InvariantCulture, $"launcher_settings.json: {RepoPaths.SettingsFile}");
        report.AppendLine(CultureInfo.InvariantCulture, $"bbport.ini: {RepoPaths.IniFile}");
        var probe = Path.Combine(Path.GetTempPath(), "bbport launcher selftest.bat");
        File.WriteAllText(probe,
            "@echo off\r\n" +
            "echo CWD=%CD%\r\n" +
            "if exist run.bat (echo CWD_RUN_BAT=FOUND) else (echo CWD_RUN_BAT=MISSING)\r\n" +
            $"if exist \"{RepoPaths.RunBat}\" (echo ABS_RUN_BAT=FOUND) else (echo ABS_RUN_BAT=MISSING)\r\n" +
            "echo ComSpec=%ComSpec%\r\n" +
            "echo PATHEXT=%PATHEXT%\r\n" +
            "echo SystemRoot=%SystemRoot%\r\n" +
            "echo BB_GAME_DIR=%BB_GAME_DIR%\r\n" +
            "echo NoDefaultCurrentDirectoryInExePath=%NoDefaultCurrentDirectoryInExePath%\r\n");
        var command = CommandLine(probe);
        report.AppendLine(CultureInfo.InvariantCulture, $"Command: {command}");
        uint code = 255;
        try
        {
            using var proc = GameProcess.Start(command, RepoPaths.Root, env);
            using var done = new ManualResetEventSlim();
            proc.Line += line =>
            {
                lock (report)
                {
                    report.AppendLine("  " + line);
                }
            };
            proc.Exited += c =>
            {
                code = c;
                done.Set();
            };
            proc.BeginRead();
            if (!done.Wait(TimeSpan.FromSeconds(20)))
            {
                proc.Kill();
                report.AppendLine("TIMEOUT");
            }
        }
        catch (GameProcess.Win32Exception ex)
        {
            report.AppendLine(CultureInfo.InvariantCulture, $"Start failed: {ex.Message}");
        }
        finally
        {
            File.Delete(probe);
        }
        report.AppendLine(CultureInfo.InvariantCulture, $"Exit code: {code}");
        var ok = code == 0 && report.ToString().Contains("CWD_RUN_BAT=FOUND", StringComparison.Ordinal)
                 && report.ToString().Contains("ABS_RUN_BAT=FOUND", StringComparison.Ordinal);
        report.AppendLine(ok ? "SELFTEST PASSED" : "SELFTEST FAILED");
        File.WriteAllText(Path.Combine(RepoPaths.Root, "launcher-selftest.log"), report.ToString());
        return ok ? 0 : 1;
    }
}
