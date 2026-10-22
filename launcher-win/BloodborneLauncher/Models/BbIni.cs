using System.Text;

namespace BloodborneLauncher.Models;

/// <summary>
/// bbport.ini, read and written line by line so unknown keys, comments, order and line
/// endings survive. The engine reads it as BbSettings::Load does (gpu/shim/bbport_settings.cpp):
/// "key=value" lines, '#' comments, the last line for a key wins. The engine also rewrites
/// the whole file from its in-game menu, so every change re-reads the file first.
/// </summary>
internal sealed class BbIni(string path)
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);
    private List<string> _lines = [];
    private string _newline = "\n";

    public string FilePath { get; } = path;

    public void Reload()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var text = File.ReadAllText(FilePath, Utf8NoBom);
                _newline = text.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
                _lines = [.. text.Split('\n').Select(l => l.TrimEnd('\r'))];
                if (_lines.Count > 0 && _lines[^1].Length == 0)
                {
                    _lines.RemoveAt(_lines.Count - 1);
                }
            }
            else
            {
                _lines = [];
            }
        }
        catch (IOException)
        {
            // Being written by the engine; keep what we had.
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    public string? Get(string key)
    {
        string? value = null;
        foreach (var line in _lines)
        {
            if (Parse(line, out var k, out var v) && k == key)
            {
                value = v;
            }
        }
        return value;
    }

    public void Set(string key, string value)
    {
        var found = false;
        for (var i = 0; i < _lines.Count; i++)
        {
            if (Parse(_lines[i], out var k, out _) && k == key)
            {
                _lines[i] = $"{key}={value}";
                found = true;
            }
        }
        if (!found)
        {
            _lines.Add($"{key}={value}");
        }
    }

    /// <summary>Re-reads the file, applies <paramref name="edit"/> and writes it back.</summary>
    public bool Update(Action<BbIni> edit)
    {
        Reload();
        edit(this);
        return Save();
    }

    public bool Save()
    {
        try
        {
            if (_lines.Count == 0)
            {
                _lines.Add("# bbport settings (in-game menu: Insert / L3+R3)");
            }
            // No BOM: the engine would read it as part of the first key.
            File.WriteAllText(FilePath, string.Join(_newline, _lines) + _newline, Utf8NoBom);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static bool Parse(string line, out string key, out string value)
    {
        var text = line.TrimEnd(' ', '\t', '\r', '\n');
        var eq = text.IndexOf('=', StringComparison.Ordinal);
        if (text.Length == 0 || text[0] == '#' || eq < 0)
        {
            key = value = "";
            return false;
        }
        key = text[..eq].Trim();
        value = text[(eq + 1)..].Trim();
        return true;
    }
}
