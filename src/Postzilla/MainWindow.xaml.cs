using System.Collections.ObjectModel;
using System.Runtime;
using Postzilla.Models;
using Postzilla.Services;
using Postzilla.Views;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.Graphics;
using Windows.Storage.Pickers;

namespace Postzilla;

public sealed partial class MainWindow : Window
{
    private readonly CollectionStore _store = new();
    private readonly List<ApiCollection> _collections = new();
    private List<AppEnvironment> _environments = new();
    private AppEnvironment? _activeEnvironment;
    private int _untitledCounter = 1;

    public ObservableCollection<TreeEntry> RootEntries { get; } = new();

    public MainWindow()
    {
        InitializeComponent();
        Title = "Postzilla";
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.TitleBar.PreferredTheme = Microsoft.UI.Windowing.TitleBarTheme.Dark;
        SizeAndCenter();
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico"));
        TitleBarIcon.Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
            new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.png")));

        _store.EnsureInitialized();
        _collections.AddRange(_store.LoadCollections());
        _environments = _store.LoadEnvironments();
        RefreshEnvironmentBox();
        LoadExpandedState();
        RebuildTree();
        OpenNewTab();
        Root.Loaded += async (_, _) => await CheckForUpdateAsync();
    }

    private XamlRoot XamlRoot => Root.XamlRoot;

    /// <summary>Sizes the window relative to the monitor's work area, so it looks the same on any resolution or DPI scale.</summary>
    private void SizeAndCenter()
    {
        var area = Microsoft.UI.Windowing.DisplayArea.GetFromWindowId(AppWindow.Id, Microsoft.UI.Windowing.DisplayAreaFallback.Primary).WorkArea;
        var width = (int)(area.Width * 0.75);
        var height = (int)(area.Height * 0.8);
        AppWindow.MoveAndResize(new RectInt32(area.X + (area.Width - width) / 2, area.Y + (area.Height - height) / 2, width, height));
    }

    private RequestEditor? CurrentEditor => (Tabs.SelectedItem as TabViewItem)?.Content as RequestEditor;

    private IEnumerable<(TabViewItem Tab, RequestEditor Editor)> OpenEditors =>
        Tabs.TabItems.OfType<TabViewItem>().Where(t => t.Content is RequestEditor).Select(t => (t, (RequestEditor)t.Content));

    // ------------------------------------------------------------------ variables

    private IReadOnlyDictionary<string, string> GetVariables(ApiCollection? collection)
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var v in collection?.Variables.Where(v => v.Enabled && v.Key.Length > 0) ?? Enumerable.Empty<KeyValueItem>())
            vars[v.Key] = v.Value;
        foreach (var v in _activeEnvironment?.Variables.Where(v => v.Enabled && v.Key.Length > 0) ?? Enumerable.Empty<KeyValueItem>())
            vars[v.Key] = v.Value;
        return vars;
    }

    private void RefreshEnvironmentBox()
    {
        var activeId = _activeEnvironment?.Id;
        EnvironmentBox.Items.Clear();
        EnvironmentBox.Items.Add("No environment");
        foreach (var e in _environments) EnvironmentBox.Items.Add(EnvironmentItem(e));
        var index = _environments.FindIndex(e => e.Id == activeId);
        EnvironmentBox.SelectedIndex = index + 1;
    }

    private static object EnvironmentItem(AppEnvironment environment)
    {
        if (ColorPalette.Brush(environment.Color) is not { } brush) return environment.Name;
        var item = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        item.Children.Add(new Border { Width = 12, Height = 12, CornerRadius = new CornerRadius(3), Background = brush, VerticalAlignment = VerticalAlignment.Center });
        item.Children.Add(new TextBlock { Text = environment.Name, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis });
        return item;
    }

    private void EnvironmentBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        var index = EnvironmentBox.SelectedIndex - 1;
        _activeEnvironment = index >= 0 && index < _environments.Count ? _environments[index] : null;
        ApplyEnvironmentColor();
    }

    /// <summary>
    /// The outer chrome (title bar and window frame) shows the active environment's colour; the inner parts
    /// (tabs and request editors) show the collection/folder colour.
    /// </summary>
    private void ApplyEnvironmentColor()
    {
        var color = ColorPalette.TryParse(_activeEnvironment?.Color);
        if (color is { } c)
        {
            AppTitleBar.Background = new SolidColorBrush(ColorPalette.WithAlpha(c, 0x50));
            TitleEnvBadge.Background = new SolidColorBrush(c);
            TitleEnvText.Foreground = new SolidColorBrush(ColorPalette.ContrastText(c));
            TitleEnvText.Text = _activeEnvironment!.Name.ToUpperInvariant();
            TitleEnvBadge.Visibility = Visibility.Visible;
            Root.BorderBrush = new SolidColorBrush(c);
            Root.BorderThickness = new Thickness(2);
        }
        else
        {
            AppTitleBar.Background = null;
            TitleEnvBadge.Visibility = Visibility.Collapsed;
            Root.BorderBrush = null;
            Root.BorderThickness = new Thickness(0);
        }
        Title = _activeEnvironment != null ? $"Postzilla - {_activeEnvironment.Name}" : "Postzilla";
    }

    private async void About_Click(object sender, RoutedEventArgs e)
    {
        var update = await Dialogs.ShowAboutAsync(XamlRoot);
        if (update is not null) await Dialogs.OfferUpdateAsync(XamlRoot, update);
    }

    private async Task CheckForUpdateAsync()
    {
        UpdateInfo? update;
        try { update = await UpdateService.CheckAsync(CancellationToken.None); }
        catch { return; }
        if (update is not null) await Dialogs.OfferUpdateAsync(XamlRoot, update);
    }

    private async void ManageEnvironments_Click(object sender, RoutedEventArgs e)
    {
        var result = await Dialogs.ManageEnvironmentsAsync(XamlRoot, _environments);
        if (result == null) return;
        _environments = result;
        _activeEnvironment = _environments.FirstOrDefault(x => x.Id == _activeEnvironment?.Id);
        _store.SaveEnvironments(_environments);
        RefreshEnvironmentBox();
        ApplyEnvironmentColor();
    }

    // ------------------------------------------------------------------ tree

    private void RebuildTree()
    {
        _trackExpansion = false;
        RootEntries.Clear();
        foreach (var c in _collections)
        {
            var entry = TreeEntry.ForCollection(c);
            entry.IsExpanded = _expandedIds.Contains(entry.Id);
            AddChildren(entry, c, c.Items);
            RootEntries.Add(entry);
        }
        EmptyHint.Visibility = _collections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateAllTabHeaders();
        // Containers are realized later and can reset nested expansion; re-apply it, then resume tracking user changes.
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () =>
        {
            foreach (var entry in Flatten(RootEntries))
                entry.IsExpanded = entry.Kind != TreeEntryKind.Request && _expandedIds.Contains(entry.Id);
            DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _trackExpansion = true);
        });
    }

    private void AddChildren(TreeEntry parent, ApiCollection c, List<CollectionNode> nodes)
    {
        foreach (var node in nodes)
        {
            var entry = TreeEntry.ForNode(c, node, parent.EffectiveColor);
            entry.IsExpanded = node.IsFolder && _expandedIds.Contains(entry.Id);
            AddChildren(entry, c, node.Children);
            parent.Children.Add(entry);
        }
    }

    // ------------------------------------------------------------------ expanded state

    // The app owns which entries are expanded: the TreeView resets its own state when the tree is rebuilt or dragged.
    private HashSet<string> _expandedIds = new();
    private bool _trackExpansion;

    private void LoadExpandedState()
    {
        var saved = _store.LoadExpandedIds();
        if (saved != null) { _expandedIds = saved; return; }
        var containerIds = _collections
            .SelectMany(c => new[] { c.Id }.Concat(FolderIds(c.Items)));
        _expandedIds = new HashSet<string>(containerIds);
    }

    private static IEnumerable<string> FolderIds(IEnumerable<CollectionNode> nodes) =>
        nodes.Where(n => n.IsFolder).SelectMany(n => new[] { n.Id }.Concat(FolderIds(n.Children)));

    private void SaveExpandedState()
    {
        var existingIds = _collections.SelectMany(c => new[] { c.Id }.Concat(FolderIds(c.Items))).ToHashSet();
        _expandedIds.IntersectWith(existingIds);
        _store.SaveExpandedIds(_expandedIds);
    }

    private void SetExpanded(TreeEntry entry, bool expanded)
    {
        if (entry.Kind == TreeEntryKind.Request) return;
        var changed = expanded ? _expandedIds.Add(entry.Id) : _expandedIds.Remove(entry.Id);
        entry.IsExpanded = expanded;
        if (changed) SaveExpandedState();
    }

    private void CollectionTree_Expanding(TreeView sender, TreeViewExpandingEventArgs args)
    {
        if (_trackExpansion && !_dragging && args.Item is TreeEntry entry) SetExpanded(entry, true);
    }

    private void CollectionTree_Collapsed(TreeView sender, TreeViewCollapsedEventArgs args)
    {
        if (_trackExpansion && !_dragging && args.Item is TreeEntry entry) SetExpanded(entry, false);
    }

    private void SetAllExpanded(bool expanded)
    {
        if (expanded) _expandedIds.UnionWith(_collections.SelectMany(c => new[] { c.Id }.Concat(FolderIds(c.Items))));
        else _expandedIds.Clear();
        SaveExpandedState();
        RebuildTree();
    }

    private void ExpandAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(true);
    private void CollapseAll_Click(object sender, RoutedEventArgs e) => SetAllExpanded(false);

    private static IEnumerable<TreeEntry> Flatten(IEnumerable<TreeEntry> entries) =>
        entries.SelectMany(e => new[] { e }.Concat(Flatten(e.Children)));

    private TreeEntry? FindEntry(string id) => Flatten(RootEntries).FirstOrDefault(e => e.Id == id);

    private void CollectionTree_ItemInvoked(TreeView sender, TreeViewItemInvokedEventArgs args)
    {
        if (args.InvokedItem is not TreeEntry entry) return;
        if (entry.Kind == TreeEntryKind.Request) OpenNode(entry.Collection, entry.Node!);
        else SetExpanded(entry, !entry.IsExpanded);
    }

    private void CollectionTree_RightTapped(object sender, RightTappedRoutedEventArgs e)
    {
        var entry = EntryFromElement(e.OriginalSource as DependencyObject);
        if (entry == null) return;
        e.Handled = true;

        var menu = new MenuFlyout();
        void Add(string text, string glyph, Action action)
        {
            var item = new MenuFlyoutItem { Text = text, Icon = new FontIcon { Glyph = glyph } };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        if (entry.Kind is TreeEntryKind.Collection or TreeEntryKind.Folder)
        {
            Add("New request", "\uE710", () => _ = NewRequestInAsync(entry));
            Add("New folder", "\uE8F4", () => _ = NewFolderInAsync(entry));
            Add("New request from cURL…", "\uE77F", () => _ = NewRequestFromCurlInAsync(entry));
            menu.Items.Add(new MenuFlyoutSeparator());
        }
        if (entry.Kind == TreeEntryKind.Request)
        {
            Add("Open", "\uE8A7", () => OpenNode(entry.Collection, entry.Node!));
            Add("Copy as cURL", "\uE8C8", () => CopyNodeAsCurl(entry));
        }
        if (entry.Kind == TreeEntryKind.Collection)
        {
            Add("Variables…", "\uE943", () => _ = EditCollectionVariablesAsync(entry.Collection));
            Add("Export…", "\uEDE1", () => _ = ExportCollectionAsync(entry.Collection));
        }
        Add("Rename…", "\uE8AC", () => _ = RenameAsync(entry));
        if (entry.Kind is TreeEntryKind.Collection or TreeEntryKind.Folder)
            Add("Colour…", "\uE790", () => _ = EditColorAsync(entry));
        if (entry.Kind != TreeEntryKind.Collection) Add("Duplicate", "\uE8C8", () => Duplicate(entry));
        menu.Items.Add(new MenuFlyoutSeparator());
        Add(entry.Kind == TreeEntryKind.Collection ? "Delete collection" : "Delete", "\uE74D", () => _ = DeleteAsync(entry));

        menu.ShowAt(CollectionTree, new FlyoutShowOptions { Position = e.GetPosition(CollectionTree) });
    }

    private TreeEntry? EntryFromElement(DependencyObject? element)
    {
        for (var d = element; d != null; d = VisualTreeHelper.GetParent(d))
            if (d is TreeViewItem item) return CollectionTree.ItemFromContainer(item) as TreeEntry ?? item.DataContext as TreeEntry;
        return null;
    }

    private static List<CollectionNode> ChildListOf(TreeEntry entry) =>
        entry.Kind == TreeEntryKind.Collection ? entry.Collection.Items : entry.Node!.Children;

    // ------------------------------------------------------------------ drag & drop

    private TreeEntry? _dragEntry;
    private bool _dragging;
    private DragEventHandler? _dragOverHandler, _dragLeaveHandler, _dropHandler;

    private sealed record DropTarget(List<CollectionNode> List, int Index, string Caption, Thickness Indicator);

    // TreeViewItem handles drag events internally, so subscribe with handledEventsToo.
    private void TreeItem_Loaded(object sender, RoutedEventArgs e)
    {
        var item = (TreeViewItem)sender;
        _dragOverHandler ??= TreeItem_DragOver;
        _dragLeaveHandler ??= TreeItem_DragLeave;
        _dropHandler ??= TreeItem_Drop;
        item.RemoveHandler(UIElement.DragOverEvent, _dragOverHandler);
        item.RemoveHandler(UIElement.DragLeaveEvent, _dragLeaveHandler);
        item.RemoveHandler(UIElement.DropEvent, _dropHandler);
        item.AddHandler(UIElement.DragOverEvent, _dragOverHandler, true);
        item.AddHandler(UIElement.DragLeaveEvent, _dragLeaveHandler, true);
        item.AddHandler(UIElement.DropEvent, _dropHandler, true);
    }

    private void CollectionTree_DragItemsStarting(TreeView sender, TreeViewDragItemsStartingEventArgs args)
    {
        _dragEntry = args.Items.FirstOrDefault() as TreeEntry;
        if (_dragEntry?.Node == null) { args.Cancel = true; return; }
        _dragging = true;
        args.Data.RequestedOperation = DataPackageOperation.Move;
        args.Data.SetText(_dragEntry.Label);
    }

    private DropTarget? ResolveDrop(TreeViewItem item, DragEventArgs e)
    {
        if (_dragEntry?.Node is not { } dragged) return null;
        var target = EntryFromElement(item);
        if (target == null || target.Node == dragged) return null;

        if (target.Kind != TreeEntryKind.Request)
        {
            var list = ChildListOf(target);
            return new DropTarget(list, list.Count, $"Move into {target.Label}", new Thickness(1));
        }

        var parentList = target.Collection.FindParentList(target.Node!);
        if (parentList == null) return null;
        var before = e.GetPosition(item).Y < item.ActualHeight / 2;
        var index = parentList.IndexOf(target.Node!) + (before ? 0 : 1);
        return new DropTarget(parentList, index, $"Move {(before ? "before" : "after")} {target.Label}",
            before ? new Thickness(0, 2, 0, 0) : new Thickness(0, 0, 0, 2));
    }

    private void TreeItem_DragOver(object sender, DragEventArgs e)
    {
        var item = (TreeViewItem)sender;
        var drop = ResolveDrop(item, e);
        e.Handled = true;
        if (drop == null || ApiCollection.IsWithin(_dragEntry!.Node!, drop.List))
        {
            e.AcceptedOperation = DataPackageOperation.None;
            ClearDropIndicator(item);
            return;
        }
        e.AcceptedOperation = DataPackageOperation.Move;
        e.DragUIOverride.Caption = drop.Caption;
        e.DragUIOverride.IsGlyphVisible = false;
        item.BorderBrush = (Brush)Application.Current.Resources["AccentFillColorDefaultBrush"];
        item.BorderThickness = drop.Indicator;
    }

    private void TreeItem_DragLeave(object sender, DragEventArgs e) => ClearDropIndicator((TreeViewItem)sender);

    private static void ClearDropIndicator(TreeViewItem item)
    {
        item.ClearValue(Control.BorderBrushProperty);
        item.ClearValue(Control.BorderThicknessProperty);
    }

    private void TreeItem_Drop(object sender, DragEventArgs e)
    {
        var item = (TreeViewItem)sender;
        ClearDropIndicator(item);
        e.Handled = true;
        var dragged = _dragEntry;
        var drop = ResolveDrop(item, e);
        _dragEntry = null;
        if (dragged?.Node is not { } node || drop == null || EntryFromElement(item) is not { } target) return;

        var from = dragged.Collection;
        var to = target.Collection;
        if (!ApiCollection.MoveNode(from, node, to, drop.List, drop.Index)) return;

        // Save the destination first so a failure can't lose the request.
        _store.Save(to);
        if (from != to) _store.Save(from);
        foreach (var (_, editor) in OpenEditors)
        {
            if (editor.Collection != from || from == to || editor.Node == null || to.FindParentList(editor.Node) == null) continue;
            editor.Collection = to;
            editor.NotifyStateChanged();
        }
        if (target.Kind != TreeEntryKind.Request) _expandedIds.Add(target.Id);
        SaveExpandedState();
        RebuildTree();
    }

    private void CollectionTree_DragItemsCompleted(TreeView sender, TreeViewDragItemsCompletedEventArgs args)
    {
        _dragEntry = null;
        DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, () => _dragging = false);
    }

    private void SaveCollection(ApiCollection c, string? expandId = null)
    {
        _store.Save(c);
        if (expandId != null && _expandedIds.Add(expandId)) SaveExpandedState();
        RebuildTree();
    }

    private async Task NewRequestInAsync(TreeEntry parent)
    {
        var name = await Dialogs.PromptTextAsync(XamlRoot, "New request", "Name", "New Request");
        if (name == null) return;
        var node = CollectionNode.ForRequest(name, new ApiRequest());
        ChildListOf(parent).Add(node);
        SaveCollection(parent.Collection, parent.Id);
        OpenNode(parent.Collection, node);
    }

    private async Task NewRequestFromCurlInAsync(TreeEntry parent)
    {
        var text = await Dialogs.PromptCurlAsync(XamlRoot);
        if (text == null) return;
        try
        {
            var parsed = CurlParser.Parse(text);
            var node = CollectionNode.ForRequest(parsed.Name, parsed.Request);
            ChildListOf(parent).Add(node);
            SaveCollection(parent.Collection, parent.Id);
            OpenNode(parent.Collection, node);
        }
        catch (FormatException ex)
        {
            await ShowErrorAsync("Couldn't parse cURL command", ex.Message);
        }
    }

    private async Task NewFolderInAsync(TreeEntry parent)
    {
        var name = await Dialogs.PromptTextAsync(XamlRoot, "New folder", "Name", "New Folder");
        if (name == null) return;
        ChildListOf(parent).Add(CollectionNode.Folder(name));
        SaveCollection(parent.Collection, parent.Id);
    }

    private async Task EditColorAsync(TreeEntry entry)
    {
        var isFolder = entry.Kind == TreeEntryKind.Folder;
        var choice = await Dialogs.PickColorAsync(XamlRoot, $"{entry.Label} — colour",
            isFolder
                ? "Tabs for requests in this folder (and its subfolders) are tinted with this colour. With no colour, the folder uses its parent's."
                : "Tabs for requests in this collection are tinted with this colour. Folders can override it.",
            isFolder ? "Use the parent's colour" : "No colour",
            isFolder ? entry.Node!.Color : entry.Collection.Color);
        if (choice == null) return;
        if (isFolder) entry.Node!.Color = choice.Color;
        else entry.Collection.Color = choice.Color;
        SaveCollection(entry.Collection);
    }

    private async Task RenameAsync(TreeEntry entry)
    {
        var name = await Dialogs.PromptTextAsync(XamlRoot, "Rename", "Name", entry.Label);
        if (name == null) return;
        if (entry.Node != null) entry.Node.Name = name;
        else entry.Collection.Name = name;
        SaveCollection(entry.Collection);
    }

    private void Duplicate(TreeEntry entry)
    {
        var list = entry.Collection.FindParentList(entry.Node!);
        if (list == null) return;
        var copy = entry.Node!.DeepClone();
        copy.Name += " (copy)";
        list.Insert(list.IndexOf(entry.Node) + 1, copy);
        SaveCollection(entry.Collection);
    }

    private async Task DeleteAsync(TreeEntry entry)
    {
        var what = entry.Kind switch
        {
            TreeEntryKind.Collection => $"collection “{entry.Label}” and all its requests",
            TreeEntryKind.Folder => $"folder “{entry.Label}” and its contents",
            _ => $"request “{entry.Label}”",
        };
        if (!await Dialogs.ConfirmAsync(XamlRoot, "Delete", $"Delete the {what}? This cannot be undone.")) return;

        if (entry.Kind == TreeEntryKind.Collection)
        {
            _store.Delete(entry.Collection);
            _collections.Remove(entry.Collection);
            RebuildTree();
        }
        else
        {
            entry.Collection.FindParentList(entry.Node!)?.Remove(entry.Node!);
            SaveCollection(entry.Collection);
        }

        // Tabs for deleted requests become unsaved drafts.
        foreach (var (_, editor) in OpenEditors)
        {
            if (editor.Collection == null) continue;
            var stillExists = _collections.Contains(editor.Collection)
                              && editor.Node != null && editor.Collection.FindParentList(editor.Node) != null;
            if (stillExists) continue;
            editor.Node = null;
            editor.Collection = null;
            editor.MarkSaved();
            editor.NotifyStateChanged();
        }
    }

    private async Task EditCollectionVariablesAsync(ApiCollection c)
    {
        var vars = await Dialogs.EditVariablesAsync(XamlRoot, $"{c.Name} — variables", c.Variables);
        if (vars == null) return;
        c.Variables = vars;
        _store.Save(c);
    }

    private void CopyNodeAsCurl(TreeEntry entry)
    {
        if (entry.Node?.Request == null) return;
        var package = new DataPackage();
        package.SetText(CurlExporter.Export(entry.Node.Request, GetVariables(entry.Collection)));
        Clipboard.SetContent(package);
    }

    private async void NewCollection_Click(object sender, RoutedEventArgs e)
    {
        var name = await Dialogs.PromptTextAsync(XamlRoot, "New collection", "Name", "My Collection");
        if (name == null) return;
        var c = new ApiCollection { Name = name };
        _collections.Add(c);
        SaveCollection(c, c.Id);
    }

    private async void Import_Click(object sender, RoutedEventArgs e)
    {
        var picker = new FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeFilter.Add(".json");
        var file = await picker.PickSingleFileAsync();
        if (file == null) return;
        try
        {
            var c = CollectionStore.Import(await File.ReadAllTextAsync(file.Path));
            _collections.Add(c);
            SaveCollection(c, c.Id);
        }
        catch (Exception ex)
        {
            await ShowErrorAsync("Import failed", ex.Message);
        }
    }

    private async Task ExportCollectionAsync(ApiCollection c)
    {
        var picker = new FileSavePicker { SuggestedFileName = c.Name };
        WinRT.Interop.InitializeWithWindow.Initialize(picker, WinRT.Interop.WindowNative.GetWindowHandle(this));
        picker.FileTypeChoices.Add("Postzilla collection", new List<string> { ".json" });
        var file = await picker.PickSaveFileAsync();
        if (file == null) return;
        await File.WriteAllTextAsync(file.Path, CollectionStore.Export(c));
    }

    private async void PasteCurl_Click(object sender, RoutedEventArgs e)
    {
        var text = await Dialogs.PromptCurlAsync(XamlRoot);
        if (text == null) return;
        try
        {
            var parsed = CurlParser.Parse(text);
            OpenNewTab(parsed.Request, parsed.Name);
        }
        catch (FormatException ex)
        {
            await ShowErrorAsync("Couldn't parse cURL command", ex.Message);
        }
    }

    // ------------------------------------------------------------------ tabs

    private void OpenNode(ApiCollection c, CollectionNode node)
    {
        var existing = OpenEditors.FirstOrDefault(x => x.Editor.Node == node);
        if (existing.Tab != null)
        {
            Tabs.SelectedItem = existing.Tab;
            return;
        }
        AddTab(new RequestEditor(node.Request?.Clone() ?? new ApiRequest(), c, node, node.Name, GetVariables));
    }

    private void OpenNewTab(ApiRequest? request = null, string? name = null)
    {
        var editor = new RequestEditor(request ?? new ApiRequest(), null, null, name ?? $"Untitled {_untitledCounter++}", GetVariables);
        AddTab(editor);
        if (request != null) editor.NotifyStateChanged();
        editor.FocusUrl();
    }

    private void AddTab(RequestEditor editor)
    {
        var tab = new TabViewItem
        {
            Content = editor,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        editor.HorizontalAlignment = HorizontalAlignment.Stretch;
        editor.VerticalAlignment = VerticalAlignment.Stretch;
        editor.StateChanged += (_, _) => UpdateTabHeader(tab, editor);
        editor.SaveRequested += async (_, _) => await SaveEditorAsync(editor);
        UpdateTabHeader(tab, editor);
        Tabs.TabItems.Add(tab);
        Tabs.SelectedItem = tab;
    }

    private void UpdateAllTabHeaders()
    {
        foreach (var (tab, editor) in OpenEditors) UpdateTabHeader(tab, editor);
    }

    private static void UpdateTabHeader(TabViewItem tab, RequestEditor editor)
    {
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock
        {
            Text = editor.Method,
            FontSize = 11,
            FontWeight = Microsoft.UI.Text.FontWeights.Bold,
            Foreground = TreeEntry.MethodBrushFor(editor.Method),
            VerticalAlignment = VerticalAlignment.Center,
        });
        header.Children.Add(new TextBlock { Text = editor.Title, MaxWidth = 220, TextTrimming = TextTrimming.CharacterEllipsis });
        if (editor.IsDirty)
            header.Children.Add(new TextBlock { Text = "●", FontSize = 10, VerticalAlignment = VerticalAlignment.Center, Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"] });
        tab.Header = header;
        ToolTipService.SetToolTip(tab, editor.Collection != null ? $"{editor.Collection.Name} / {editor.Title}" : editor.Title + " (unsaved)");

        var color = ColorPalette.TryParse(editor.Collection?.ResolveColor(editor.Node));
        if (color is { } c) tab.Background = new SolidColorBrush(ColorPalette.WithAlpha(c, 0x40));
        else tab.ClearValue(Control.BackgroundProperty);
        SetTabResource(tab, "TabViewItemHeaderBackgroundSelected", color, 0x80);
        SetTabResource(tab, "TabViewItemHeaderBackgroundPointerOver", color, 0x60);
        SetTabResource(tab, "TabViewItemHeaderBackgroundPressed", color, 0x70);
        editor.SetAccent(color);
    }

    private static void SetTabResource(TabViewItem tab, string key, Windows.UI.Color? color, byte alpha)
    {
        if (color is { } c) tab.Resources[key] = new SolidColorBrush(ColorPalette.WithAlpha(c, alpha));
        else tab.Resources.Remove(key);
    }

    private async Task<bool> SaveEditorAsync(RequestEditor editor)
    {
        var request = editor.GetRequest();
        if (editor.Node != null && editor.Collection != null)
        {
            editor.Node.Request = request;
            SaveCollection(editor.Collection);
            editor.MarkSaved();
            return true;
        }

        var target = await Dialogs.PromptSaveTargetAsync(XamlRoot, _collections, editor.Title);
        if (target == null) return false;
        var collection = target.Collection;
        if (collection == null)
        {
            collection = new ApiCollection { Name = target.NewCollectionName ?? "My Collection" };
            _collections.Add(collection);
        }
        var node = CollectionNode.ForRequest(target.RequestName, request);
        collection.Items.Add(node);
        editor.Collection = collection;
        editor.Node = node;
        SaveCollection(collection, collection.Id);
        editor.MarkSaved();
        return true;
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => OpenNewTab();

    private async void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args) => await CloseTabAsync(args.Tab);

    private async Task CloseTabAsync(TabViewItem tab)
    {
        if (tab.Content is RequestEditor { IsDirty: true } editor)
        {
            var choice = await Dialogs.ConfirmUnsavedAsync(XamlRoot, editor.Title);
            if (choice == ContentDialogResult.None) return;
            if (choice == ContentDialogResult.Primary && !await SaveEditorAsync(editor)) return;
        }
        (tab.Content as RequestEditor)?.Close();
        Tabs.TabItems.Remove(tab);
        ReclaimMemory();
    }

    private CancellationTokenSource? _reclaimCts;

    /// <summary>
    /// A closed tab's native editors (Scintilla documents, D3D devices: tens of MB each) are only freed once the GC finalises
    /// their WinRT wrappers <em>and</em> XAML then renders a frame to process its deferred releases, layer by layer.
    /// An idle window renders nothing, so without this the memory stays committed indefinitely.
    /// </summary>
    private async void ReclaimMemory()
    {
        _reclaimCts?.Cancel();
        var cts = _reclaimCts = new CancellationTokenSource();
        CompositionTarget.Rendering += KeepRendering;
        try
        {
            // Each GC + frame frees another layer (wrappers → XAML elements → native controls → D3D/Scintilla), so continue until memory settles.
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            var last = long.MaxValue;
            for (int i = 0, stable = 0; i < 40 && stable < 4; i++)
            {
                await Task.Delay(500, cts.Token);
                await Task.Run(() =>
                {
                    GC.Collect();
                    GC.WaitForPendingFinalizers();
                    GC.Collect();
                });
                process.Refresh();
                var now = process.PrivateMemorySize64;
                stable = now > last - (1 << 20) ? stable + 1 : 0;
                last = Math.Min(last, now);
            }
            GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
            await Task.Run(() => GC.Collect());
        }
        catch (OperationCanceledException) { }
        finally
        {
            CompositionTarget.Rendering -= KeepRendering;
        }
    }

    private static void KeepRendering(object? sender, object e) { }

    // ------------------------------------------------------------------ accelerators

    private async void SaveAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (CurrentEditor is { } editor) await SaveEditorAsync(editor);
    }

    private async void SendAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (CurrentEditor is { } editor) await editor.SendAsync();
    }

    private void NewTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        OpenNewTab();
    }

    private async void CloseTabAccelerator_Invoked(KeyboardAccelerator sender, KeyboardAcceleratorInvokedEventArgs args)
    {
        args.Handled = true;
        if (Tabs.SelectedItem is TabViewItem tab) await CloseTabAsync(tab);
    }

    private async Task ShowErrorAsync(string title, string message)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = XamlRoot,
            Title = title,
            Content = new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
            CloseButtonText = "OK",
        };
        await dialog.ShowAsync();
    }
}
