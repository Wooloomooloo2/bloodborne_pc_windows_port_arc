using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Text;
using Microsoft.UI.Dispatching;

namespace BloodborneLauncher.Services;

/// <summary>
/// The live log: launcher.log on disk (every line, appended and flushed as it arrives)
/// and the last <see cref="MaxLines"/> lines for the Log window, with the Vulkan instance
/// warnings optionally hidden from view (never from the file).
/// </summary>
internal sealed class LogStore : IDisposable
{
    public const int MaxLines = 5000;
    private const string VkNoise = "<Warning> vk_instance.cpp";

    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly ConcurrentQueue<string> _incoming = new();
    private readonly LinkedList<string> _all = new();
    private readonly object _fileLock = new();
    private StreamWriter? _file;
    private bool _hideVk;

    /// <summary>The lines shown (UI thread).</summary>
    public ObservableCollection<string> Visible { get; } = [];

    /// <summary>Vulkan warning lines hidden since the last launch.</summary>
    public int HiddenCount { get; private set; }

    /// <summary>Every line from the game, on the UI thread (feature warnings).</summary>
    public event Action<string>? GameLine;

    public LogStore(DispatcherQueue dispatcher)
    {
        _dispatcher = dispatcher;
        _timer = dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(50);
        _timer.Tick += (_, _) => Drain();
        _timer.Start();
    }

    public bool HideVk
    {
        get => _hideVk;
        set
        {
            if (_hideVk == value)
            {
                return;
            }
            _hideVk = value;
            Visible.Clear();
            foreach (var line in _all)
            {
                if (Shown(line))
                {
                    Visible.Add(line);
                }
            }
        }
    }

    /// <summary>Truncates launcher.log and starts writing to it.</summary>
    public void OpenFile(string path)
    {
        lock (_fileLock)
        {
            _file?.Dispose();
            try
            {
                _file = new StreamWriter(path, false, new UTF8Encoding(false)) { AutoFlush = true };
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _file = null;
            }
        }
        HiddenCount = 0;
    }

    public void CloseFile()
    {
        lock (_fileLock)
        {
            _file?.Dispose();
            _file = null;
        }
    }

    /// <summary>A launcher message (UI thread): to the file and the view at once.</summary>
    public void Log(string message)
    {
        WriteFile(message);
        Add(message);
    }

    /// <summary>A game output line (any thread): to the file now, to the view on the next tick.</summary>
    public void Enqueue(string line)
    {
        WriteFile(line);
        _incoming.Enqueue(line);
    }

    /// <summary>Runs <paramref name="action"/> on the UI thread after every queued line is shown.</summary>
    public void AfterQueued(Action action) => _dispatcher.TryEnqueue(() =>
    {
        while (!_incoming.IsEmpty)
        {
            Drain();
        }
        action();
    });

    public string AllText()
    {
        var sb = new StringBuilder();
        foreach (var line in Visible)
        {
            sb.Append(line).Append("\r\n");
        }
        return sb.ToString();
    }

    public void Clear()
    {
        _all.Clear();
        Visible.Clear();
    }

    public void Dispose()
    {
        _timer.Stop();
        CloseFile();
    }

    private void Drain()
    {
        var n = 0;
        while (n++ < 2000 && _incoming.TryDequeue(out var line))
        {
            GameLine?.Invoke(line);
            if (_hideVk && line.Contains(VkNoise, StringComparison.Ordinal))
            {
                HiddenCount++;
            }
            Add(line);
        }
    }

    private bool Shown(string line) => !_hideVk || !line.Contains(VkNoise, StringComparison.Ordinal);

    private void Add(string line)
    {
        _all.AddLast(line);
        if (_all.Count > MaxLines)
        {
            var dropped = _all.First!.Value;
            _all.RemoveFirst();
            if (Visible.Count > 0 && Shown(dropped) && ReferenceEquals(Visible[0], dropped))
            {
                Visible.RemoveAt(0);
            }
        }
        if (Shown(line))
        {
            Visible.Add(line);
        }
    }

    private void WriteFile(string line)
    {
        lock (_fileLock)
        {
            if (_file is null)
            {
                return;
            }
            try
            {
                _file.WriteLine(line);
            }
            catch (IOException)
            {
                _file.Dispose();
                _file = null;
            }
        }
    }
}
