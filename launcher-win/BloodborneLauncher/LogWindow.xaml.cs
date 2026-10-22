using System.Collections.Specialized;
using BloodborneLauncher.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;

namespace BloodborneLauncher;

/// <summary>The live output of run.bat and the game (the last 5000 lines); it follows the
/// end unless scrolled up.</summary>
public sealed partial class LogWindow : Window
{
    private readonly MainWindow _owner;
    private ScrollViewer? _scroller;
    private bool _follow = true;
    private bool _scrollQueued;

    internal MainViewModel ViewModel { get; }

    internal LogWindow(MainViewModel viewModel, MainWindow owner)
    {
        ViewModel = viewModel;
        _owner = owner;
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        var scale = Content.XamlRoot?.RasterizationScale ?? owner.Content.XamlRoot?.RasterizationScale ?? 1.0;
        AppWindow.Resize(new SizeInt32((int)(980 * scale), (int)(640 * scale)));
        ViewModel.Log.Visible.CollectionChanged += OnLinesChanged;
        Closed += (_, _) => ViewModel.Log.Visible.CollectionChanged -= OnLinesChanged;
        Lines.Loaded += (_, _) =>
        {
            _scroller = FindScroller(Lines);
            if (_scroller is not null)
            {
                _scroller.ViewChanged += (_, _) =>
                    _follow = _scroller.VerticalOffset >= _scroller.ScrollableHeight - 8;
            }
            ScrollToEnd();
        };
    }

    private void OnLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (!_follow || _scrollQueued)
        {
            return;
        }
        _scrollQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _scrollQueued = false;
            ScrollToEnd();
        });
    }

    private void ScrollToEnd()
    {
        var items = ViewModel.Log.Visible;
        if (items.Count > 0)
        {
            Lines.ScrollIntoView(items[^1]);
        }
    }

    private static ScrollViewer? FindScroller(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is ScrollViewer sv)
            {
                return sv;
            }
            if (FindScroller(child) is { } found)
            {
                return found;
            }
        }
        return null;
    }

    private void OnCopy(object sender, RoutedEventArgs e)
    {
        var package = new DataPackage();
        package.SetText(ViewModel.Log.AllText());
        Clipboard.SetContent(package);
    }

    private void OnClear(object sender, RoutedEventArgs e) => ViewModel.Log.Clear();

    private void OnOpenFile(object sender, RoutedEventArgs e) => _owner.OpenLogFile();

    private async void OnVerify(object sender, RoutedEventArgs e)
    {
        VerifyButton.IsEnabled = false;
        try
        {
            _follow = true;
            await ViewModel.VerifyAsync().ConfigureAwait(true);
        }
        finally
        {
            VerifyButton.IsEnabled = true;
        }
    }
}
