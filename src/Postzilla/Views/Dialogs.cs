using System.Collections.ObjectModel;
using Postzilla.Models;
using Postzilla.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace Postzilla.Views;

public sealed record SaveTarget(ApiCollection? Collection, string? NewCollectionName, string RequestName);

/// <summary>Code-built ContentDialogs used across the app.</summary>
public static class Dialogs
{
    private static ContentDialog Create(XamlRoot root, string title, object content, string primary, string? secondary = null) => new()
    {
        XamlRoot = root,
        Title = title,
        Content = content,
        PrimaryButtonText = primary,
        SecondaryButtonText = secondary ?? "",
        CloseButtonText = "Cancel",
        DefaultButton = ContentDialogButton.Primary,
    };

    public static async Task<string?> PromptTextAsync(XamlRoot root, string title, string label, string initial = "")
    {
        var box = new TextBox { Header = label, Text = initial, MinWidth = 360 };
        box.Loaded += (_, _) => { box.Focus(FocusState.Programmatic); box.SelectAll(); };
        var dialog = Create(root, title, box, "OK");
        var result = await dialog.ShowAsync();
        var text = box.Text.Trim();
        return result == ContentDialogResult.Primary && text.Length > 0 ? text : null;
    }

    /// <summary>Shows About. Returns an update if the user found one via "Check for updates".</summary>
    public static async Task<UpdateInfo?> ShowAboutAsync(XamlRoot root)
    {
        var version = UpdateService.CurrentVersion.ToString(3);
        var secondary = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

        var art = new Microsoft.UI.Xaml.Controls.Image
        {
            Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(
                new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "AboutArt.png"))),
            Width = 240, Height = 240,
            HorizontalAlignment = HorizontalAlignment.Center,
        };

        var shortcuts = new Grid { ColumnSpacing = 16, RowSpacing = 4 };
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        shortcuts.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        (string Keys, string Action)[] keys =
        [
            ("Ctrl+Enter / F5", "Send request"),
            ("Ctrl+Space", "GraphQL autocomplete"),
            ("F1", "GraphQL docs for the word at the caret"),
            ("Shift+Alt+F", "Format GraphQL / JSON"),
        ];
        for (var i = 0; i < keys.Length; i++)
        {
            shortcuts.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var k = new TextBlock { Text = keys[i].Keys, FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas") };
            var a = new TextBlock { Text = keys[i].Action, Foreground = secondary, TextWrapping = TextWrapping.Wrap };
            Grid.SetRow(k, i); Grid.SetRow(a, i); Grid.SetColumn(a, 1);
            shortcuts.Children.Add(k); shortcuts.Children.Add(a);
        }

        var panel = new StackPanel { Spacing = 10, Width = 380 };
        panel.Children.Add(art);
        panel.Children.Add(new TextBlock
        {
            Text = "Postzilla", HorizontalAlignment = HorizontalAlignment.Center,
            Style = (Style)Application.Current.Resources["TitleTextBlockStyle"],
        });
        panel.Children.Add(new TextBlock { Text = $"Version {version}", Foreground = secondary, HorizontalAlignment = HorizontalAlignment.Center });
        panel.Children.Add(new TextBlock
        {
            Text = "A lightweight API client for HTTP and GraphQL — collections, cURL import and schema docs.",
            TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 4, 0, 8),
        });
        panel.Children.Add(new TextBlock { Text = "Keyboard shortcuts", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] });
        panel.Children.Add(shortcuts);
        panel.Children.Add(new TextBlock
        {
            Text = "Copyright (c) 2026 James West. Released under the MIT License.",
            Foreground = secondary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0),
        });
        panel.Children.Add(new HyperlinkButton
        {
            Content = $"github.com/{UpdateService.Repo}",
            NavigateUri = new Uri($"https://github.com/{UpdateService.Repo}"),
            Padding = new Thickness(0),
        });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Content = new ScrollViewer { Content = panel },
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };

        UpdateInfo? found = null;
        var checkResult = new TextBlock { Foreground = secondary, TextWrapping = TextWrapping.Wrap };
        var checkButton = new Button { Content = "Check for updates" };
        checkButton.Click += async (_, _) =>
        {
            checkButton.IsEnabled = false;
            checkResult.Text = "Checking…";
            try
            {
                found = await UpdateService.CheckAsync(CancellationToken.None);
                if (found is null) checkResult.Text = "You're up to date.";
                else dialog.Hide();
            }
            catch (Exception ex) { checkResult.Text = $"Couldn't check for updates: {ex.Message}"; }
            finally { checkButton.IsEnabled = true; }
        };
        panel.Children.Add(checkButton);
        panel.Children.Add(checkResult);

        await dialog.ShowAsync();
        return found;
    }

    /// <summary>Asks to install an update; on accept downloads it, launches the installer and exits the app.</summary>
    public static async Task OfferUpdateAsync(XamlRoot root, UpdateInfo update)
    {
        var message = new TextBlock
        {
            Text = $"Postzilla {update.Version} is available (you have {UpdateService.CurrentVersion.ToString(3)}). Update now? Postzilla will restart.",
            TextWrapping = TextWrapping.Wrap,
        };
        var progress = new ProgressBar { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 12, 0, 0) };
        var panel = new StackPanel();
        panel.Children.Add(message);
        panel.Children.Add(progress);

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = "Update available",
            Content = panel,
            PrimaryButtonText = "Update now",
            CloseButtonText = "Later",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.PrimaryButtonClick += async (_, args) =>
        {
            args.Cancel = true;
            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            progress.Visibility = Visibility.Visible;
            message.Text = $"Downloading Postzilla {update.Version}…";
            try
            {
                var installer = await UpdateService.DownloadAsync(update, CancellationToken.None);
                UpdateService.StartInstaller(installer);
                Application.Current.Exit();
            }
            catch (Exception ex)
            {
                progress.Visibility = Visibility.Collapsed;
                message.Text = $"Update failed: {ex.Message}";
            }
            finally { deferral.Complete(); }
        };
        await dialog.ShowAsync();
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string confirm = "Delete")
    {
        var dialog = Create(root, title, new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap }, confirm);
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }

    /// <summary>Returns Primary = save, Secondary = discard, None = cancel.</summary>
    public static async Task<ContentDialogResult> ConfirmUnsavedAsync(XamlRoot root, string name)
    {
        var dialog = Create(root, "Unsaved changes",
            new TextBlock { Text = $"Save changes to “{name}” before closing?", TextWrapping = TextWrapping.Wrap },
            "Save", "Don't save");
        return await dialog.ShowAsync();
    }

    public static async Task<string?> PromptCurlAsync(XamlRoot root)
    {
        var box = new TextBox
        {
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 260,
            Width = 560,
            FontFamily = (Microsoft.UI.Xaml.Media.FontFamily)Application.Current.Resources["CodeFont"],
            IsSpellCheckEnabled = false,
            PlaceholderText = "curl -X POST https://api.example.com/items -H 'Content-Type: application/json' -d '{\"name\":\"x\"}'",
        };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Paste a cURL command (bash or Windows cmd format, e.g. from browser dev tools “Copy as cURL”).",
            TextWrapping = TextWrapping.Wrap,
        });
        panel.Children.Add(box);
        box.Loaded += (_, _) => box.Focus(FocusState.Programmatic);

        var dialog = Create(root, "Import cURL", panel, "Import");
        return await dialog.ShowAsync() == ContentDialogResult.Primary && box.Text.Trim().Length > 0 ? box.Text : null;
    }

    public static async Task<SaveTarget?> PromptSaveTargetAsync(XamlRoot root, IReadOnlyList<ApiCollection> collections, string defaultName)
    {
        const string newOption = "+ New collection…";
        var nameBox = new TextBox { Header = "Request name", Text = defaultName, MinWidth = 380 };
        var combo = new ComboBox { Header = "Collection", HorizontalAlignment = HorizontalAlignment.Stretch };
        foreach (var c in collections) combo.Items.Add(c.Name);
        combo.Items.Add(newOption);
        combo.SelectedIndex = 0;
        var newCollectionBox = new TextBox { Header = "New collection name", Text = "My Collection" };
        newCollectionBox.Visibility = collections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        combo.SelectionChanged += (_, _) =>
            newCollectionBox.Visibility = combo.SelectedIndex == collections.Count ? Visibility.Visible : Visibility.Collapsed;

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(combo);
        panel.Children.Add(newCollectionBox);
        nameBox.Loaded += (_, _) => { nameBox.Focus(FocusState.Programmatic); nameBox.SelectAll(); };

        if (await Create(root, "Save request", panel, "Save").ShowAsync() != ContentDialogResult.Primary) return null;

        var requestName = nameBox.Text.Trim().Length > 0 ? nameBox.Text.Trim() : defaultName;
        if (combo.SelectedIndex >= 0 && combo.SelectedIndex < collections.Count)
            return new SaveTarget(collections[combo.SelectedIndex], null, requestName);
        var newName = newCollectionBox.Text.Trim();
        return new SaveTarget(null, newName.Length > 0 ? newName : "My Collection", requestName);
    }

    /// <summary>Edits a list of variables. Returns the edited copy, or null if cancelled.</summary>
    public static async Task<List<KeyValueItem>?> EditVariablesAsync(XamlRoot root, string title, IEnumerable<KeyValueItem> variables)
    {
        var items = new ObservableCollection<KeyValueItem>(KeyValueItem.CloneList(variables));
        var editor = new KeyValueEditor { Items = items, Width = 620, Height = 380, KeyLabel = "Variable" };
        var panel = new StackPanel { Spacing = 8 };
        panel.Children.Add(new TextBlock
        {
            Text = "Reference variables as {{name}} in URLs, headers, bodies and auth. Built-ins: {{$guid}}, {{$timestamp}}, {{$isoTimestamp}}, {{$randomInt}}.",
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 620,
        });
        panel.Children.Add(editor);
        var dialog = Create(root, title, panel, "Save");
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? items.ToList() : null;
    }

    public sealed class ColorChoice
    {
        public string? Color { get; init; }
    }

    /// <summary>Picks a colour for a collection or folder. Returns null if cancelled; a choice with a null colour clears it.</summary>
    public static async Task<ColorChoice?> PickColorAsync(XamlRoot root, string title, string description, string noneTooltip, string? current)
    {
        var picker = new ColorSwatchPicker(noneTooltip) { Color = current };
        var panel = new StackPanel { Spacing = 12, Width = 440 };
        panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(picker);
        var dialog = Create(root, title, new ScrollViewer { Content = panel }, "Save");
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        return new ColorChoice { Color = picker.Color };
    }

    /// <summary>Environment manager. Works on clones; returns the new list or null if cancelled.</summary>
    public static async Task<List<AppEnvironment>?> ManageEnvironmentsAsync(XamlRoot root, IEnumerable<AppEnvironment> environments)
    {
        var envs = environments.Select(e => e.Clone()).ToList();
        var items = new ObservableCollection<KeyValueItem>();
        AppEnvironment? current = null;

        var picker = new ComboBox { Width = 260, PlaceholderText = "No environments" };
        var nameBox = new TextBox { Width = 260, Header = "Name" };
        var addButton = new Button { Content = "New" };
        var deleteButton = new Button { Content = "Delete" };
        var editor = new KeyValueEditor { Items = items, Width = 620, Height = 340, KeyLabel = "Variable" };

        var colorPicker = new ColorSwatchPicker("No colour");
        var colorPanel = new StackPanel { Spacing = 8 };
        colorPanel.Children.Add(new TextBlock { Text = "Colour — frames the window and tints the title bar while this environment is active" });
        colorPanel.Children.Add(colorPicker);
        void Commit()
        {
            if (current == null) return;
            current.Variables = KeyValueItem.CloneList(items);
            current.Color = colorPicker.Color;
            if (nameBox.Text.Trim().Length > 0) current.Name = nameBox.Text.Trim();
        }

        void RefreshPicker(int select)
        {
            picker.Items.Clear();
            foreach (var e in envs) picker.Items.Add(e.Name);
            picker.SelectedIndex = Math.Min(select, envs.Count - 1);
        }

        void Show(AppEnvironment? env)
        {
            current = env;
            items.Clear();
            nameBox.Text = env?.Name ?? "";
            if (env != null) foreach (var v in env.Variables) items.Add(v.Clone());
            colorPicker.Color = env?.Color;
            nameBox.IsEnabled = editor.IsEnabled = deleteButton.IsEnabled = colorPanel.IsHitTestVisible = env != null;
            colorPanel.Opacity = env != null ? 1 : 0.5;
        }

        picker.SelectionChanged += (_, _) =>
        {
            var index = picker.SelectedIndex;
            if (index < 0) { Show(null); return; }
            if (current != envs[index]) { Commit(); Show(envs[index]); }
        };
        addButton.Click += (_, _) =>
        {
            Commit();
            envs.Add(new AppEnvironment
            {
                Name = $"Environment {envs.Count + 1}",
                Color = ColorPalette.Presets[envs.Count % ColorPalette.Presets.Length],
            });
            current = null;
            RefreshPicker(envs.Count - 1);
        };
        deleteButton.Click += (_, _) =>
        {
            if (current == null) return;
            var index = envs.IndexOf(current);
            envs.Remove(current);
            current = null;
            RefreshPicker(Math.Max(0, index - 1));
            if (envs.Count == 0) Show(null);
        };
        nameBox.LostFocus += (_, _) =>
        {
            if (current == null || nameBox.Text.Trim().Length == 0) return;
            var index = envs.IndexOf(current);
            current.Name = nameBox.Text.Trim();
            Commit();
            var keep = current;
            RefreshPicker(index);
            current = keep;
        };

        var top = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        top.Children.Add(picker);
        top.Children.Add(addButton);
        top.Children.Add(deleteButton);
        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(top);
        panel.Children.Add(nameBox);
        panel.Children.Add(colorPanel);
        panel.Children.Add(editor);

        RefreshPicker(0);
        if (envs.Count == 0) Show(null);

        var dialog = Create(root, "Environments", new ScrollViewer { Content = panel }, "Save");
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return null;
        Commit();
        return envs;
    }
}
