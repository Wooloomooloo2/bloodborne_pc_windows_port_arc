using System.Xml;
using System.Xml.Linq;
using CommunityToolkit.Mvvm.ComponentModel;

namespace BloodborneLauncher.Models;

/// <summary>One community patch from patches/Bloodborne.xml.</summary>
public sealed partial class PatchItem : ObservableObject
{
    public required string Name { get; init; }
    public required string Author { get; init; }
    public required string Note { get; init; }
    public required string Category { get; init; }

    public string AuthorText => $"by {Author}";
    public bool HasNote => Note.Length > 0;

    [ObservableProperty]
    public partial bool IsEnabled { get; set; }
}

internal static class XmlPatches
{
    public const string AllCategories = "All categories";

    public static readonly string[] Categories =
    [
        AllCategories, "Performance & Fixes", "Visuals & Camera", "Gameplay & Cheats",
        "Framerate & Engine", "Resolutions & Grids", "Debug & Tools",
    ];

    /// <summary>The 01.09 eboot.bin patches (launcher.py load_xml_patches), without the ones
    /// bbport.ini drives through Effects.</summary>
    public static List<PatchItem> Load(string path)
    {
        var list = new List<PatchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        if (!File.Exists(path))
        {
            return list;
        }
        try
        {
            foreach (var meta in XDocument.Load(path).Descendants("Metadata"))
            {
                if ((string?)meta.Attribute("AppVer") != "01.09" ||
                    ((string?)meta.Attribute("AppElf") ?? "eboot.bin") != "eboot.bin")
                {
                    continue;
                }
                var name = ((string?)meta.Attribute("Name") ?? "").Trim();
                if (name.Length == 0 || Choices.IniManagedPatches.Contains(name) || !seen.Add(name))
                {
                    continue;
                }
                var author = ((string?)meta.Attribute("Author") ?? "").Trim();
                list.Add(new PatchItem
                {
                    Name = name,
                    Author = author.Length > 0 ? author : "Unknown",
                    // The XML writes line breaks in notes as a literal backslash-n.
                    Note = ((string?)meta.Attribute("Note") ?? "").Replace(@"\n", "\n", StringComparison.Ordinal).Trim(),
                    Category = Categorise(name),
                });
            }
        }
        catch (Exception ex) when (ex is XmlException or IOException or UnauthorizedAccessException)
        {
        }
        return list;
    }

    private static string Categorise(string name)
    {
        var n = name.ToLowerInvariant();
        bool Any(params string[] keys) => keys.Any(n.Contains);
        if (Any("resolution patch", "light grid", "optimal 1080p"))
        {
            return "Resolutions & Grids";
        }
        if (n.Contains("fps", StringComparison.Ordinal))
        {
            return "Framerate & Engine";
        }
        if (Any("debug", "capture", "http"))
        {
            return "Debug & Tools";
        }
        if (Any("stealth", "silent", "no dead", "rally", "cheat", "sensitive analog", "unlock game region", "enemy control"))
        {
            return "Gameplay & Cheats";
        }
        if (Any("blur", "shadow", "dof", "ssao", "chromatic", "camera", "reflections", "text scale", "physics", "aa"))
        {
            return "Visuals & Camera";
        }
        return "Performance & Fixes";
    }
}
