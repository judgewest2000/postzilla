using System.Collections.ObjectModel;
using Postzilla.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Postzilla.Views;

public enum TreeEntryKind { Collection, Folder, Request }

/// <summary>View-model for one row in the collections tree.</summary>
public sealed class TreeEntry : ObservableObject
{
    private bool _isExpanded;

    private TreeEntry(TreeEntryKind kind, ApiCollection collection, CollectionNode? node, string? effectiveColor)
    {
        Kind = kind;
        Collection = collection;
        Node = node;
        EffectiveColor = effectiveColor;
    }

    public static TreeEntry ForCollection(ApiCollection c) => new(TreeEntryKind.Collection, c, null, c.Color);

    /// <param name="inheritedColor">The effective colour of the parent folder or collection.</param>
    public static TreeEntry ForNode(ApiCollection c, CollectionNode n, string? inheritedColor) =>
        new(n.IsFolder ? TreeEntryKind.Folder : TreeEntryKind.Request, c, n,
            string.IsNullOrWhiteSpace(n.Color) ? inheritedColor : n.Color);

    public TreeEntryKind Kind { get; }
    public ApiCollection Collection { get; }
    public CollectionNode? Node { get; }
    /// <summary>This entry's own colour, or the one inherited from its folder/collection.</summary>
    public string? EffectiveColor { get; }
    public ObservableCollection<TreeEntry> Children { get; } = new();

    public string Id => Node?.Id ?? Collection.Id;
    public string Label => Node?.Name ?? Collection.Name;

    public bool IsExpanded
    {
        get => _isExpanded;
        set => SetProperty(ref _isExpanded, value);
    }

    public string Glyph => Kind == TreeEntryKind.Collection ? "\uE8F1" : "\uE8B7";
    public Visibility GlyphVisibility => Kind == TreeEntryKind.Request ? Visibility.Collapsed : Visibility.Visible;

    public Brush GlyphBrush => ColorPalette.Brush(EffectiveColor)
                               ?? (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];

    /// <summary>A thin bar beside requests in the colour they inherit, matching their tab.</summary>
    public Brush ColorBarBrush => (Kind == TreeEntryKind.Request ? ColorPalette.Brush(EffectiveColor) : null)
                                  ?? new SolidColorBrush(Colors.Transparent);
    public Visibility MethodVisibility => Kind == TreeEntryKind.Request ? Visibility.Visible : Visibility.Collapsed;

    public string MethodLabel => (Node?.Request?.Method ?? "") switch
    {
        "DELETE" => "DEL",
        "OPTIONS" => "OPT",
        "PATCH" => "PAT",
        var m => m,
    };

    public Brush MethodBrush => MethodBrushFor(Node?.Request?.Method);

    public static Brush MethodBrushFor(string? method) => new SolidColorBrush(method switch
    {
        "GET" => Color.FromArgb(255, 0x2E, 0xA0, 0x43),
        "POST" => Color.FromArgb(255, 0xD9, 0x7B, 0x06),
        "PUT" => Color.FromArgb(255, 0x25, 0x75, 0xD6),
        "PATCH" => Color.FromArgb(255, 0x8E, 0x4E, 0xC6),
        "DELETE" => Color.FromArgb(255, 0xD1, 0x34, 0x38),
        _ => Colors.Gray,
    });
}
