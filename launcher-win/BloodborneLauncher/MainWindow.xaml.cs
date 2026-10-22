using System.Diagnostics;
using System.Runtime.InteropServices;
using BloodborneLauncher.Models;
using BloodborneLauncher.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Windows.Storage.Pickers;
using Windows.Graphics;

namespace BloodborneLauncher;

public sealed partial class MainWindow : Window
{
    private const double WideLayoutMinWidth = 900;
    private LogWindow? _logWindow;
    private bool _closing;
    /// <summary>Started without showing the launcher: quit when the game ends normally.</summary>
    private bool _direct;

    internal MainViewModel ViewModel { get; }

    public MainWindow()
    {
        ViewModel = new MainViewModel(DispatcherQueue);
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SizeAndCentre(1100, 900);
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.PreferredMinimumWidth = Scale(560);
            p.PreferredMinimumHeight = Scale(520);
        }

        Root.SizeChanged += (_, e) => ApplyLayout(e.NewSize.Width);
        Activated += (_, e) =>
        {
            if (e.WindowActivationState != WindowActivationState.Deactivated && !ViewModel.IsRunning)
            {
                ViewModel.LoadIni();
            }
        };
        AppWindow.Closing += OnAppWindowClosing;
        ViewModel.GameExited += OnGameExited;
    }

    // ------------------------------------------------------------ layout

    private void ApplyLayout(double width)
    {
        var wide = width >= WideLayoutMinWidth;
        Grid.SetColumn(RightColumn, wide ? 2 : 0);
        Grid.SetRow(RightColumn, wide ? 0 : 1);
        Grid.SetColumnSpan(LeftColumn, wide ? 1 : 3);
        Grid.SetColumnSpan(RightColumn, wide ? 1 : 3);
        Columns.ColumnDefinitions[2].Width = wide ? new GridLength(9, GridUnitType.Star) : new GridLength(0);
    }

    private int Scale(double dips) => (int)(dips * GetDpiForWindow(WinRT.Interop.WindowNative.GetWindowHandle(this)) / 96.0);

    private void SizeAndCentre(double width, double height)
    {
        var area = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Primary).WorkArea;
        var w = Math.Min(Scale(width), area.Width);
        var h = Math.Min(Scale(height), area.Height);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h));
    }

    // ------------------------------------------------------------ play / stop

    /// <summary>"Show launcher every time" off: play at once, showing the window only if
    /// something needs the user.</summary>
    internal void StartGameDirectly()
    {
        _direct = true;
        if (AppWindow.Presenter is OverlappedPresenter p)
        {
            p.Minimize(false);
        }
        DispatcherQueue.TryEnqueue(async () =>
        {
            Activate();
            if (AppWindow.Presenter is OverlappedPresenter op)
            {
                op.Minimize(false);
            }
            await PlayAsync().ConfigureAwait(true);
            if (!ViewModel.IsRunning)
            {
                _direct = false;
                ShowWindow();
            }
        });
    }

    private async void OnPlay(object sender, RoutedEventArgs e)
    {
        if (ViewModel.IsRunning)
        {
            ViewModel.Stop();
            return;
        }
        Status.IsOpen = false;
        await PlayAsync().ConfigureAwait(true);
    }

    private async Task PlayAsync()
    {
        var result = await ViewModel.LaunchAsync().ConfigureAwait(true);
        if (result.Started)
        {
            if (AppWindow.Presenter is OverlappedPresenter p)
            {
                p.Minimize();
            }
        }
        else if (result.ErrorTitle is not null)
        {
            ShowWindow();
            await ShowMessageAsync(result.ErrorTitle, result.ErrorMessage ?? "").ConfigureAwait(true);
        }
    }

    private void OnGameExited(GameExit exit)
    {
        if (_closing)
        {
            return;
        }
        if (_direct && !exit.Crashed && !exit.FailedToStart)
        {
            Close();
            return;
        }
        _direct = false;
        ShowWindow();
        if (exit.FailedToStart)
        {
            Status.Severity = InfoBarSeverity.Error;
            Status.Title = "The game failed to start";
            Status.Message = string.Join(Environment.NewLine, exit.LastLines.Where(l => l.Length > 0).TakeLast(4));
            StatusAction.Content = "Open log";
            Status.IsOpen = true;
        }
        else if (exit.Crashed)
        {
            var code = exit.Code < 256 ? $"{exit.Code}" : $"0x{exit.Code:X8}";
            var crash = exit.NewCrashLogs.Count > 0
                ? $" A crash report was written: {string.Join(", ", exit.NewCrashLogs.Select(p => Path.GetRelativePath(RepoPaths.Root, p)))}."
                : "";
            Status.Severity = InfoBarSeverity.Error;
            Status.Title = "Bloodborne stopped unexpectedly";
            Status.Message = $"Exit code {code}.{crash} launcher.log has the full output.";
            StatusAction.Content = "Open log";
            Status.IsOpen = true;
        }
    }

    private void ShowWindow()
    {
        if (AppWindow.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Minimized } p)
        {
            p.Restore(true);
        }
        Activate();
        SetForegroundWindow(WinRT.Interop.WindowNative.GetWindowHandle(this));
    }

    // ------------------------------------------------------------ dialogs

    internal async Task ShowMessageAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            Title = title,
            Content = new ScrollViewer
            {
                Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, IsTextSelectionEnabled = true },
                MaxHeight = 420,
            },
            CloseButtonText = "OK",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = Content.XamlRoot,
        };
        await dialog.ShowAsync();
    }

    private async void OnPatches(object sender, RoutedEventArgs e)
    {
        var dialog = new PatchManagerDialog(ViewModel.LoadPatches()) { XamlRoot = Content.XamlRoot };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            ViewModel.SavePatches(dialog.Patches);
        }
    }

    private async void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        if (_closing)
        {
            return;
        }
        if (ViewModel.IsRunning && !_direct)
        {
            args.Cancel = true;
            ShowWindow();
            var dialog = new ContentDialog
            {
                Title = "Quit",
                Content = "Bloodborne is still running. Stop it and quit?",
                PrimaryButtonText = "Stop and quit",
                CloseButtonText = "Cancel",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = Content.XamlRoot,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary)
            {
                return;
            }
            ViewModel.Stop();
        }
        _closing = true;
        ViewModel.Shutdown();
        _logWindow?.Close();
        if (args.Cancel)
        {
            Close();
        }
    }

    // ------------------------------------------------------------ log

    private void OnShowLog(object sender, RoutedEventArgs e)
    {
        if (_logWindow is null)
        {
            _logWindow = new LogWindow(ViewModel, this);
            _logWindow.Closed += (_, _) => _logWindow = null;
        }
        _logWindow.Activate();
    }

    private void OnOpenLogFile(object sender, RoutedEventArgs e) => OpenLogFile();

    internal async void OpenLogFile()
    {
        if (!File.Exists(RepoPaths.LogFile))
        {
            await ShowMessageAsync("No log yet", "launcher.log is created the first time you launch the game.").ConfigureAwait(true);
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(RepoPaths.LogFile) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            await ShowMessageAsync("Error", $"Could not open {RepoPaths.LogFile}:\n{ex.Message}").ConfigureAwait(true);
        }
    }

    // ------------------------------------------------------------ locations

    private async void OnChooseEboot(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker(AppWindow.Id)
        {
            CommitButtonText = "Use this eboot.bin",
            SuggestedStartLocation = PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".bin");
        picker.FileTypeFilter.Add("*");
        var file = await picker.PickSingleFileAsync();
        if (file is not null && !ViewModel.SetGamePath(file.Path))
        {
            await ShowMessageAsync("Not a game folder", $"Choose eboot.bin from the dumped game folder.\n\n{file.Path}").ConfigureAwait(true);
        }
    }

    private async void OnChooseGameFolder(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null && !ViewModel.SetGamePath(folder.Path))
        {
            await ShowMessageAsync("Not a game folder",
                $"There is no eboot.bin in:\n{folder.Path}\n\nChoose the dumped game folder (CUSA03173 with update 1.09 merged).").ConfigureAwait(true);
        }
    }

    private void OnResetGame(object sender, RoutedEventArgs e) => ViewModel.ResetGamePath();

    private async void OnChooseUserDir(object sender, RoutedEventArgs e)
    {
        var picker = new FolderPicker(AppWindow.Id) { SuggestedStartLocation = PickerLocationId.ComputerFolder };
        var folder = await picker.PickSingleFolderAsync();
        if (folder is not null)
        {
            ViewModel.SetUserDir(folder.Path);
        }
    }

    private void OnResetUserDir(object sender, RoutedEventArgs e) => ViewModel.SetUserDir(null);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(IntPtr hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(IntPtr hwnd);
}
