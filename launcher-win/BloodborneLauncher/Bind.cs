using Microsoft.UI.Xaml;

namespace BloodborneLauncher;

/// <summary>x:Bind function helpers.</summary>
internal static class Bind
{
    public static bool And(bool a, bool b) => a && b;
    public static Visibility Visible(bool value) => value ? Visibility.Visible : Visibility.Collapsed;
}
