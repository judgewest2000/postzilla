using System.Collections.ObjectModel;
using System.Text.Json;
using Postzilla.GraphQL;
using Postzilla.Models;
using Postzilla.Services;
using Microsoft.UI.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.ApplicationModel.DataTransfer;
using Windows.System;
using Windows.UI;
using Windows.UI.Core;

namespace Postzilla.Views;

public sealed partial class RequestEditor : UserControl
{
    private static readonly string[] Methods = { "GET", "POST", "PUT", "PATCH", "DELETE", "HEAD", "OPTIONS" };
    private static readonly string[] BodyModeNames = { "No body", "JSON", "Text", "Form URL-encoded", "Multipart form", "GraphQL" };
    private static readonly string[] AuthModeNames = { "No auth", "Bearer token", "Basic auth", "API key (header)" };
    private static readonly Dictionary<string, GqlSchema> SchemaCache = new(StringComparer.OrdinalIgnoreCase);
    private const int MaxDisplayChars = 2_000_000;

    private readonly Func<ApiCollection?, IReadOnlyDictionary<string, string>> _variables;
    private readonly ObservableCollection<KeyValueItem> _params = new();
    private readonly ObservableCollection<KeyValueItem> _headers = new();
    private readonly ObservableCollection<KeyValueItem> _form = new();

    private string? _savedSnapshot;
    private string _lastUrl = "";
    private bool _syncingParams;
    private bool _dirty;
    private string _draftName;
    private CancellationTokenSource? _sendCts;
    private GqlSchema? _schema;

    public RequestEditor(ApiRequest request, ApiCollection? collection, CollectionNode? node, string draftName,
        Func<ApiCollection?, IReadOnlyDictionary<string, string>> variables)
    {
        InitializeComponent();
        Collection = collection;
        Node = node;
        _draftName = draftName;
        _variables = variables;

        MethodBox.ItemsSource = Methods;
        BodyModeBox.ItemsSource = BodyModeNames;
        AuthModeBox.ItemsSource = AuthModeNames;

        ParamsEditor.KeyLabel = "Query parameter";
        FormEditor.KeyLabel = "Field";
        FormEditor.ValueLabel = "Value (prefix @ for a file path)";
        HeadersEditor.KeyLabel = "Header";
        ParamsEditor.Items = _params;
        HeadersEditor.Items = _headers;
        FormEditor.Items = _form;
        ParamsEditor.Changed += OnParamsChanged;
        HeadersEditor.Changed += (_, _) => MarkChanged();
        FormEditor.Changed += (_, _) => MarkChanged();

        foreach (var editor in new[] { QueryBox, VariablesBox, BodyBox, ResponseBodyBox })
        {
            editor.CommandInvoked += Editor_CommandInvoked;
            if (editor != ResponseBodyBox) editor.TextChanged += (_, _) => MarkChanged();
        }
        QueryBox.CompletionSource = GetCompletionsAsync;
        QueryBox.CursorMoved += QueryBox_CursorMoved;

        LoadRequest(request);
        MarkSaved();
    }

    public ApiCollection? Collection { get; set; }
    public CollectionNode? Node { get; set; }
    public bool IsDirty => _dirty;
    public string Title => Node?.Name ?? _draftName;
    public string Method => MethodBox.SelectedItem as string ?? "GET";

    /// <summary>Raised when the title, method or dirty state changes.</summary>
    public event EventHandler? StateChanged;

    public event EventHandler? SaveRequested;

    public void NotifyStateChanged() => StateChanged?.Invoke(this, EventArgs.Empty);

    public void FocusUrl() => UrlBox.Focus(FocusState.Programmatic);

    /// <summary>Releases the embedded code editors; call when the tab is closed.</summary>
    public void Close()
    {
        _sendCts?.Cancel();
        foreach (var editor in new[] { QueryBox, VariablesBox, BodyBox, ResponseBodyBox }) editor.Close();
    }

    // ------------------------------------------------------------------ load / snapshot

    public void LoadRequest(ApiRequest r)
    {
        MethodBox.SelectedItem = Methods.Contains(r.Method.ToUpperInvariant()) ? r.Method.ToUpperInvariant() : "GET";
        _lastUrl = r.Url;
        UrlBox.Text = r.Url;
        SetParams(UrlQuery.Parse(r.Url).Concat(r.Params.Where(p => !p.Enabled).Select(p => p.Clone())));
        Replace(_headers, r.Headers);
        Replace(_form, r.FormFields);

        BodyModeBox.SelectedIndex = (int)r.BodyMode;
        BodyBox.Text = r.Body;
        QueryBox.Text = r.GraphQLQuery;
        VariablesBox.Text = r.GraphQLVariables;

        AuthModeBox.SelectedIndex = (int)r.AuthMode;
        AuthTokenBox.Text = r.AuthToken;
        AuthUserBox.Text = r.AuthUsername;
        AuthPasswordBox.Text = r.AuthPassword;
        ApiKeyNameBox.Text = r.ApiKeyName;
        ApiKeyValueBox.Text = r.ApiKeyValue;

        UpdateBodyVisibility();
        UpdateAuthVisibility();

        if (r.BodyMode == BodyMode.GraphQL)
        {
            if (SchemaCache.TryGetValue(ResolvedUrl(r.Url), out var cached)) SetSchema(cached);
            if (r.BodyMode == BodyMode.GraphQL) SelectTab(BodyTab);
        }
        else if (r.BodyMode != BodyMode.None)
        {
            SelectTab(BodyTab);
        }
    }

    public ApiRequest GetRequest() => new()
    {
        Method = Method,
        Url = UrlBox.Text,
        Params = KeyValueItem.CloneList(_params),
        Headers = KeyValueItem.CloneList(_headers),
        BodyMode = (BodyMode)Math.Max(0, BodyModeBox.SelectedIndex),
        Body = Normalize(BodyBox.Text),
        FormFields = KeyValueItem.CloneList(_form),
        GraphQLQuery = Normalize(QueryBox.Text),
        GraphQLVariables = Normalize(VariablesBox.Text),
        AuthMode = (AuthMode)Math.Max(0, AuthModeBox.SelectedIndex),
        AuthToken = AuthTokenBox.Text,
        AuthUsername = AuthUserBox.Text,
        AuthPassword = AuthPasswordBox.Text,
        ApiKeyName = ApiKeyNameBox.Text,
        ApiKeyValue = ApiKeyValueBox.Text,
    };

    public void MarkSaved()
    {
        _savedSnapshot = Snapshot();
        _dirty = false;
        NotifyStateChanged();
    }

    private string Snapshot() => JsonSerializer.Serialize(GetRequest(), JsonUtil.Options);

    // TextBox.TextChanged fires asynchronously, so dirty state is derived by comparing
    // against a snapshot rather than by suppressing events during loads.
    private void MarkChanged()
    {
        if (_savedSnapshot == null) return;
        var dirty = Snapshot() != _savedSnapshot;
        if (dirty == _dirty) return;
        _dirty = dirty;
        NotifyStateChanged();
    }

    private static string Normalize(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    private static void Replace(ObservableCollection<KeyValueItem> target, IEnumerable<KeyValueItem> source)
    {
        target.Clear();
        foreach (var item in source) target.Add(item.Clone());
    }

    // ------------------------------------------------------------------ url <-> params sync

    private void SetParams(IEnumerable<KeyValueItem> items)
    {
        _syncingParams = true;
        try
        {
            Replace(_params, items.ToList());
        }
        finally
        {
            _syncingParams = false;
        }
    }

    private void UrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (UrlBox.Text != _lastUrl)
        {
            _lastUrl = UrlBox.Text;
            var disabled = _params.Where(p => !p.Enabled).ToList();
            SetParams(UrlQuery.Parse(UrlBox.Text).Concat(disabled));
        }
        MarkChanged();
    }

    private void OnParamsChanged(object? sender, EventArgs e)
    {
        if (_syncingParams) return;
        var url = UrlQuery.WithParams(UrlBox.Text, _params);
        if (url != UrlBox.Text)
        {
            _lastUrl = url;
            UrlBox.Text = url;
        }
        MarkChanged();
    }

    private async void UrlBox_Paste(object sender, TextControlPasteEventArgs e)
    {
        var content = Clipboard.GetContent();
        if (!content.Contains(StandardDataFormats.Text)) return;
        e.Handled = true;

        var text = await content.GetTextAsync();
        if (CurlParser.LooksLikeCurl(text))
        {
            ApplyCurl(text);
            return;
        }

        // Single-line box: strip newlines ourselves since we took over the paste.
        var single = text.Replace("\r", "").Replace("\n", "");
        var start = UrlBox.SelectionStart;
        UrlBox.Text = UrlBox.Text.Remove(start, UrlBox.SelectionLength).Insert(start, single);
        UrlBox.SelectionStart = start + single.Length;
    }

    private async void UrlBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key != VirtualKey.Enter) return;
        e.Handled = true;
        await SendAsync();
    }

    public bool ApplyCurl(string text)
    {
        try
        {
            var parsed = CurlParser.Parse(text);
            LoadRequest(parsed.Request);
            if (Node == null) _draftName = parsed.Name;
            ShowInfo(InfoBarSeverity.Success, "Imported cURL command", parsed.Name);
            MarkChanged();
            NotifyStateChanged();
            return true;
        }
        catch (FormatException ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Couldn't parse cURL command", ex.Message);
            return false;
        }
    }

    // ------------------------------------------------------------------ control events

    private void MethodBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        MarkChanged();
        NotifyStateChanged();
    }

    private void AnyText_Changed(object sender, TextChangedEventArgs e) => MarkChanged();

    private void BodyModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (BodyModeBox.SelectedIndex == (int)BodyMode.GraphQL && Method == "GET") MethodBox.SelectedItem = "POST";
        UpdateBodyVisibility();
        MarkChanged();
    }

    private void AuthModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateAuthVisibility();
        MarkChanged();
    }

    private void RequestTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (AuthPanel == null) return;
        var selected = sender.SelectedItem;
        ParamsEditor.Visibility = Vis(selected == ParamsTab);
        BodyPanel.Visibility = Vis(selected == BodyTab);
        HeadersEditor.Visibility = Vis(selected == HeadersTab);
        AuthPanel.Visibility = Vis(selected == AuthTab);
    }

    private void ResponseTabs_SelectionChanged(SelectorBar sender, SelectorBarSelectionChangedEventArgs args)
    {
        if (ResponseHeadersList == null) return;
        ResponseBodyBox.Visibility = Vis(sender.SelectedItem == ResponseBodyTab);
        ResponseHeadersList.Visibility = Vis(sender.SelectedItem == ResponseHeadersTab);
    }

    private void SelectTab(SelectorBarItem item)
    {
        RequestTabs.SelectedItem = item;
        // The XAML IsSelected default is re-applied when the SelectorBar loads, so select again once loaded.
        if (!RequestTabs.IsLoaded) RequestTabs.Loaded += (_, _) => RequestTabs.SelectedItem = item;
    }

    private void UpdateBodyVisibility()
    {
        var mode = (BodyMode)Math.Max(0, BodyModeBox.SelectedIndex);
        NoBodyText.Visibility = Vis(mode == BodyMode.None);
        BodyBox.Visibility = Vis(mode is BodyMode.Json or BodyMode.Text);
        FormEditor.Visibility = Vis(mode is BodyMode.FormUrlEncoded or BodyMode.Multipart);
        GraphQLPanel.Visibility = Vis(mode == BodyMode.GraphQL);
        PrettifyButton.Visibility = Vis(mode is BodyMode.Json or BodyMode.GraphQL);
        FetchSchemaButton.Visibility = Vis(mode == BodyMode.GraphQL);
        DocsToggle.Visibility = Vis(mode == BodyMode.GraphQL);
        SchemaStatus.Visibility = Vis(mode == BodyMode.GraphQL);
        BodyBox.Placeholder = mode == BodyMode.Json ? "{ \"key\": \"value\" }" : "Request body";
        BodyBox.CodeLanguage = mode == BodyMode.Json ? "json" : "plaintext";
        UpdateDocsVisibility();
    }

    private void UpdateAuthVisibility()
    {
        var mode = (AuthMode)Math.Max(0, AuthModeBox.SelectedIndex);
        AuthTokenBox.Visibility = Vis(mode == AuthMode.Bearer);
        AuthUserBox.Visibility = Vis(mode == AuthMode.Basic);
        AuthPasswordBox.Visibility = Vis(mode == AuthMode.Basic);
        ApiKeyNameBox.Visibility = Vis(mode == AuthMode.ApiKey);
        ApiKeyValueBox.Visibility = Vis(mode == AuthMode.ApiKey);
        AuthHint.Visibility = Vis(mode != AuthMode.None);
    }

    private static bool IsDown(VirtualKey key) =>
        InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);

    // The code editor handles these keys itself (before Scintilla sees them) and forwards them.
    private async void Editor_CommandInvoked(object? sender, EditorCommandEventArgs e)
    {
        switch (e.Command)
        {
            case EditorCommand.Send:
                await SendAsync();
                break;
            case EditorCommand.Save:
                SaveRequested?.Invoke(this, EventArgs.Empty);
                break;
            case EditorCommand.Format when sender is CodeEditor editor:
                FormatEditor(editor);
                break;
            case EditorCommand.Docs when ReferenceEquals(sender, QueryBox):
                await LookupDocsAtCursorAsync(e.Offset);
                break;
        }
    }

    // ------------------------------------------------------------------ formatting

    private void Prettify_Click(object sender, RoutedEventArgs e)
    {
        if (BodyModeBox.SelectedIndex == (int)BodyMode.GraphQL)
        {
            FormatEditor(QueryBox);
            if (!string.IsNullOrWhiteSpace(VariablesBox.Text)) FormatEditor(VariablesBox);
        }
        else FormatEditor(BodyBox);
    }

    /// <summary>Formats GraphQL in the query editor and JSON elsewhere (body, variables, response).</summary>
    private void FormatEditor(CodeEditor editor)
    {
        if (string.IsNullOrWhiteSpace(editor.Text)) return;
        string formatted;
        if (editor == QueryBox)
        {
            formatted = GraphQLFormatter.Format(editor.Text);
        }
        else if (!JsonUtil.TryPretty(editor.Text, out formatted))
        {
            if (editor == ResponseBodyBox || (editor == BodyBox && BodyModeBox.SelectedIndex != (int)BodyMode.Json)) return;
            ShowInfo(InfoBarSeverity.Warning, "Not valid JSON", "The content could not be parsed as JSON.");
            return;
        }

        if (editor.IsReadOnly) editor.Text = formatted;
        else editor.ReplaceText(formatted);
        MarkChanged();
    }
    private void Save_Click(object sender, RoutedEventArgs e) => SaveRequested?.Invoke(this, EventArgs.Empty);

    private void CopyCurl_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var curl = CurlExporter.Export(GetRequest(), _variables(Collection));
            var package = new DataPackage();
            package.SetText(curl);
            Clipboard.SetContent(package);
            ShowInfo(InfoBarSeverity.Success, "Copied", "cURL command copied to the clipboard (variables resolved).");
        }
        catch (Exception ex)
        {
            ShowInfo(InfoBarSeverity.Error, "Couldn't build cURL command", ex.Message);
        }
    }

    private async void ImportCurl_Click(object sender, RoutedEventArgs e)
    {
        var text = await Dialogs.PromptCurlAsync(XamlRoot);
        if (text != null) ApplyCurl(text);
    }

    // ------------------------------------------------------------------ sending

    private async void Send_Click(object sender, RoutedEventArgs e)
    {
        if (_sendCts != null) _sendCts.Cancel();
        else await SendAsync();
    }

    // Tunnels before the text boxes, so multi-line editors don't turn Ctrl+Enter into a newline.
    private async void Root_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.F5 || (e.Key == VirtualKey.Enter && IsDown(VirtualKey.Control)))
        {
            e.Handled = true;
            await SendAsync();
        }
    }

    /// <summary>Sends the request; ignored while a request is already in flight (use the Cancel button).</summary>
    public async Task SendAsync()
    {
        if (_sendCts != null) return;

        Info.IsOpen = false;
        var request = GetRequest();
        var vars = _variables(Collection);
        _sendCts = new CancellationTokenSource();
        SendButton.Content = "Cancel";
        Busy.IsActive = true;
        StatusText.Text = "Sending…";
        StatusText.Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        TimeText.Text = SizeText.Text = "";

        try
        {
            var result = await HttpService.SendAsync(request, vars, _sendCts.Token);
            ShowResponse(result);
        }
        finally
        {
            _sendCts.Dispose();
            _sendCts = null;
            SendButton.Content = "Send";
            Busy.IsActive = false;
        }
    }

    private void ShowResponse(ResponseResult r)
    {
        TimeText.Text = $"{r.ElapsedMs:N0} ms";
        if (r.Error != null)
        {
            StatusText.Text = "Error";
            StatusText.Foreground = new SolidColorBrush(Color.FromArgb(255, 0xD1, 0x34, 0x38));
            SizeText.Text = "";
            ResponseBodyBox.CodeLanguage = "plaintext";
            ResponseBodyBox.Text = r.Error;
            ResponseHeadersList.ItemsSource = null;
            return;
        }

        StatusText.Text = $"{r.StatusCode} {r.ReasonPhrase}";
        StatusText.Foreground = new SolidColorBrush(r.StatusCode switch
        {
            < 300 => Color.FromArgb(255, 0x2E, 0xA0, 0x43),
            < 400 => Color.FromArgb(255, 0x25, 0x75, 0xD6),
            < 500 => Color.FromArgb(255, 0xD9, 0x7B, 0x06),
            _ => Color.FromArgb(255, 0xD1, 0x34, 0x38),
        });
        SizeText.Text = FormatSize(r.SizeBytes);
        ResponseBodyBox.CodeLanguage = ResponseLanguage(r);
        ResponseBodyBox.Text = r.Body.Length > MaxDisplayChars
            ? r.Body[..MaxDisplayChars] + $"\n\n… truncated ({r.Body.Length:N0} characters total)"
            : r.Body;
        ResponseHeadersList.ItemsSource = r.Headers;
        ResponseHeadersTab.Text = $"Headers ({r.Headers.Count})";
    }

    private static string ResponseLanguage(ResponseResult r)
    {
        var type = r.Headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value ?? "";
        if (type.Contains("json", StringComparison.OrdinalIgnoreCase)) return "json";
        if (type.Contains("html", StringComparison.OrdinalIgnoreCase)) return "html";
        if (type.Contains("xml", StringComparison.OrdinalIgnoreCase)) return "xml";
        var body = r.Body.TrimStart();
        if (body.StartsWith('{') || body.StartsWith('[')) return "json";
        if (body.StartsWith('<')) return body.StartsWith("<!doctype html", StringComparison.OrdinalIgnoreCase) || body.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ? "html" : "xml";
        return "plaintext";
    }

    private static string FormatSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.0} KB",
        _ => $"{bytes / 1024.0 / 1024.0:0.00} MB",
    };

    // ------------------------------------------------------------------ GraphQL schema & docs

    private string ResolvedUrl(string url) => VariableResolver.Resolve(url, _variables(Collection)).Trim();

    private async void FetchSchema_Click(object sender, RoutedEventArgs e) => await FetchSchemaAsync();

    /// <param name="quiet">Don't open the docs panel or report failures.</param>
    private async Task<bool> FetchSchemaAsync(bool quiet = false)
    {
        var request = GetRequest();
        FetchSchemaButton.IsEnabled = false;
        SchemaStatus.Text = "Loading schema…";
        try
        {
            var schema = await GraphQLService.FetchSchemaAsync(request, _variables(Collection), CancellationToken.None);
            SchemaCache[ResolvedUrl(request.Url)] = schema;
            SetSchema(schema);
            if (!quiet) DocsToggle.IsChecked = true;
            return true;
        }
        catch (Exception ex)
        {
            SchemaStatus.Text = "Schema not loaded";
            if (!quiet) ShowInfo(InfoBarSeverity.Error, "Couldn't load GraphQL schema", ex.Message);
            return false;
        }
        finally
        {
            FetchSchemaButton.IsEnabled = true;
        }
    }

    private void SetSchema(GqlSchema schema)
    {
        _schema = schema;
        DocsPanel.Schema = schema;
        SchemaStatus.Text = $"{schema.Types.Count(t => !t.Key.StartsWith("__"))} types";
    }

    private void DocsToggle_Changed(object sender, RoutedEventArgs e) => UpdateDocsVisibility();

    private const double DocsMinWidth = 260;
    private GridLength _docsWidth = new(380);

    private void UpdateDocsVisibility()
    {
        var show = DocsToggle.IsChecked == true && BodyModeBox.SelectedIndex == (int)BodyMode.GraphQL;
        if (show == (DocsPanel.Visibility == Visibility.Visible)) return;
        if (!show && DocsColumn.ActualWidth > 0) _docsWidth = DocsColumn.Width;
        DocsPanel.Visibility = DocsSplitter.Visibility = Vis(show);
        DocsColumn.MinWidth = show ? DocsMinWidth : 0;
        DocsColumn.Width = show ? _docsWidth : new GridLength(0);
    }

    private void DocsPanel_CloseRequested(object? sender, EventArgs e) => DocsToggle.IsChecked = false;

    private async Task LookupDocsAtCursorAsync(int offset)
    {
        if (_schema == null && !await FetchSchemaAsync()) return;
        DocsToggle.IsChecked = true;
        var target = GraphQLContextResolver.Resolve(_schema!, QueryBox.Text, offset);
        if (target.Kind == DocTargetKind.None)
            ShowInfo(InfoBarSeverity.Informational, "No documentation", "Nothing in the schema matches the cursor position.");
        else
            DocsPanel.ShowTarget(target, pushHistory: true);
    }

    // Live "follow cursor" docs while the docs panel is open.
    private void QueryBox_CursorMoved(object? sender, int offset)
    {
        if (_schema == null || DocsPanel.Visibility != Visibility.Visible) return;
        var target = GraphQLContextResolver.Resolve(_schema, QueryBox.Text, offset);
        if (target.Kind != DocTargetKind.None) DocsPanel.ShowTarget(target, pushHistory: false);
    }

    // ------------------------------------------------------------------ GraphQL variables <-> literals

    private void InlineVariables_Click(object sender, RoutedEventArgs e) =>
        TransformVariables(GraphQLVariableTransformer.Inline, "Moved into query");

    private void ExtractVariables_Click(object sender, RoutedEventArgs e) =>
        TransformVariables(GraphQLVariableTransformer.Extract, "Moved out of query");

    private async void TransformVariables(
        Func<string, string, GqlSchema?, VariableTransformResult> transform, string title)
    {
        // The schema gives exact types (ID!, enums, input objects); without it scalars are inferred.
        if (_schema == null && !string.IsNullOrWhiteSpace(UrlBox.Text)) await FetchSchemaAsync(quiet: true);

        VariableTransformResult result;
        try
        {
            result = transform(QueryBox.Text, VariablesBox.Text, _schema);
        }
        catch (FormatException ex)
        {
            ShowInfo(InfoBarSeverity.Warning, "Variables aren't valid JSON", ex.Message);
            return;
        }

        if (result.Changed > 0)
        {
            var query = result.Query;
            try
            {
                query = GraphQLFormatter.Format(query);
            }
            catch (Exception)
            {
                // Leave unformatted rather than fail the move.
            }
            if (query != QueryBox.Text) QueryBox.ReplaceText(query);
            if (result.Variables != VariablesBox.Text) VariablesBox.ReplaceText(result.Variables);
            MarkChanged();
        }
        ShowInfo(result.Skipped > 0 || result.Changed == 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Informational,
            title, result.Message);
    }

    // ------------------------------------------------------------------ GraphQL autocompletion

    private async Task<CompletionResult> GetCompletionsAsync(string text, int offset, bool explicitRequest)
    {
        if (_schema == null && (!explicitRequest || !await FetchSchemaAsync())) return CompletionResult.Empty;
        var result = GraphQLCompletionProvider.GetCompletions(_schema!, text, offset);
        // Don't pop up a list that only repeats what was already typed.
        if (!explicitRequest && result.Items.Count == 1 && result.Items[0].InsertText.Trim() == result.Prefix) return CompletionResult.Empty;
        return result;
    }
    // ------------------------------------------------------------------ helpers

    private void ShowInfo(InfoBarSeverity severity, string title, string message)
    {
        Info.Severity = severity;
        Info.Title = title;
        Info.Message = message;
        Info.IsOpen = true;
    }

    private static Visibility Vis(bool visible) => visible ? Visibility.Visible : Visibility.Collapsed;
}

