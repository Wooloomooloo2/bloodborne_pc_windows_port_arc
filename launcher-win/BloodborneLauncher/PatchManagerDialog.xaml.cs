using System.Collections.ObjectModel;
using System.ComponentModel;
using BloodborneLauncher.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace BloodborneLauncher;

/// <summary>launcher.py's patch manager: search, category filter, tick boxes, bulk actions.
/// Changes apply on Save only.</summary>
public sealed partial class PatchManagerDialog : ContentDialog
{
    private readonly ObservableCollection<PatchItem> _visible = [];
    private readonly bool _ready;

    internal List<PatchItem> Patches { get; }

    internal PatchManagerDialog(List<PatchItem> patches)
    {
        Patches = patches;
        InitializeComponent();
        Title = $"XML patches ({patches.Count})";
        CategoryBox.ItemsSource = XmlPatches.Categories;
        CategoryBox.SelectedIndex = 0;
        PatchList.ItemsSource = _visible;
        foreach (var p in patches)
        {
            p.PropertyChanged += OnPatchChanged;
        }
        Closed += (_, _) =>
        {
            foreach (var p in patches)
            {
                p.PropertyChanged -= OnPatchChanged;
            }
        };
        _ready = true;
        Refresh();
    }

    private void OnPatchChanged(object? sender, PropertyChangedEventArgs e) => UpdateCount();

    private void UpdateCount() => CountText.Text = $"{Patches.Count(p => p.IsEnabled)} of {Patches.Count} active";

    private void Refresh()
    {
        if (!_ready)
        {
            return;
        }
        var q = (SearchBox.Text ?? "").Trim();
        var cat = CategoryBox.SelectedItem as string ?? XmlPatches.AllCategories;
        _visible.Clear();
        foreach (var p in Patches)
        {
            if (cat != XmlPatches.AllCategories && p.Category != cat)
            {
                continue;
            }
            if (q.Length > 0 &&
                !p.Name.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !p.Note.Contains(q, StringComparison.OrdinalIgnoreCase) &&
                !p.Author.Contains(q, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }
            _visible.Add(p);
        }
        UpdateCount();
    }

    private void OnSearchChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args) => Refresh();

    private void OnCategoryChanged(object sender, SelectionChangedEventArgs e) => Refresh();

    private void OnSelectVisible(object sender, RoutedEventArgs e)
    {
        foreach (var p in _visible)
        {
            p.IsEnabled = true;
        }
    }

    private void OnDeselectVisible(object sender, RoutedEventArgs e)
    {
        foreach (var p in _visible)
        {
            p.IsEnabled = false;
        }
    }

    private void OnRecommended(object sender, RoutedEventArgs e)
    {
        foreach (var p in Patches)
        {
            p.IsEnabled = Choices.RecommendedPatches.Contains(p.Name);
        }
    }
}
