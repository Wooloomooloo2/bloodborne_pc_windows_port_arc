using System.Globalization;
using System.Runtime.InteropServices;
using BloodborneLauncher.Models;
using Microsoft.UI.Xaml;

namespace BloodborneLauncher;

public partial class App : Application
{
    private MainWindow? _window;
    private static Mutex? _single;

    public App()
    {
        InitializeComponent();
        UnhandledException += (_, e) => WriteError(e.Exception, e.Message);
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        if (Environment.GetCommandLineArgs().Contains("--selftest-launch", StringComparer.OrdinalIgnoreCase))
        {
            var vm = new ViewModels.MainViewModel(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread());
            var code = vm.SelfTestLaunch();
            vm.Shutdown();
            Environment.Exit(code);
            return;
        }
        // One launcher at a time: a second would hold stale settings and could start a
        // second game. Starting it again brings the running one forward.
        _single = new Mutex(true, @"Local\BloodborneLauncher.Main", out var first);
        if (!first)
        {
            var existing = FindWindowW(null, "Bloodborne");
            if (existing != IntPtr.Zero)
            {
                ShowWindow(existing, 9); // SW_RESTORE
                SetForegroundWindow(existing);
            }
            Environment.Exit(0);
            return;
        }
        // Shift held at start shows the launcher even when it is set to start the game directly.
        var shift = (GetAsyncKeyState(0x10) & 0x8000) != 0;
        _window = new MainWindow();
        if (!_window.ViewModel.ShowLauncher && !shift)
        {
            _window.StartGameDirectly();
        }
        else
        {
            _window.Activate();
        }
    }

    /// <summary>launcher-error.log next to launcher.log, so a failed start leaves a trace.</summary>
    private static void WriteError(Exception exception, string message)
    {
        try
        {
            File.AppendAllText(Path.Combine(RepoPaths.Root, "launcher-error.log"), string.Create(CultureInfo.InvariantCulture,
                $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {exception}\r\n{message}\r\n\r\n"));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [LibraryImport("user32.dll")]
    private static partial short GetAsyncKeyState(int key);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial IntPtr FindWindowW(string? className, string windowName);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ShowWindow(IntPtr hwnd, int command);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}
