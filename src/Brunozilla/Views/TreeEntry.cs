using System.Collections.ObjectModel;
using Brunozilla.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Brunozilla.Views;

public enum TreeEntryKind { Collection, Folder, Request }

/// <summary>View-model for one row in the collections tree.</summary>
public sealed class TreeEntry : ObservableObject
{
    private bool _isExpanded;

    private TreeEntry(TreeEntryKind kind, ApiCollection collection, CollectionNode? node)
    {
        Kind = kind;
        Collection = collection;
        Node = node;
    }

    public static TreeEntry ForCollection(ApiCollection c) => new(TreeEntryKind.Collection, c, null);

    public static TreeEntry ForNode(ApiCollection c, CollectionNode n) =>
        new(n.IsFolder ? TreeEntryKind.Folder : TreeEntryKind.Request, c, n);

    public TreeEntryKind Kind { get; }
    public ApiCollection Collection { get; }
    public CollectionNode? Node { get; }
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
