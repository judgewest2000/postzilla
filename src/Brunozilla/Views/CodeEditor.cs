using System.Text.Json;
using System.Text.Json.Nodes;
using Brunozilla.GraphQL;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.Web.WebView2.Core;

namespace Brunozilla.Views;

public enum EditorCommand { Send, Save, Format, Docs }

public sealed class EditorCommandEventArgs(EditorCommand command, int offset) : EventArgs
{
    public EditorCommand Command { get; } = command;
    public int Offset { get; } = offset;
}

/// <summary>
/// Monaco code editor hosted in WebView2. <see cref="Text"/> is mirrored on every edit so it can be read synchronously.
/// </summary>
public sealed class CodeEditor : UserControl
{
    private const string HostName = "brunozilla.assets";
    private static Task<CoreWebView2Environment>? s_environment;

    private readonly WebView2 _web = new();
    private string _text = "";
    private string _language = "plaintext";
    private string _placeholder = "";
    private bool _readOnly;
    private bool _wordWrap;
    private bool _initStarted;
    private bool _ready;
    private bool _focusWhenReady;
    private bool _closed;

    public CodeEditor()
    {
        _web.DefaultBackgroundColor = Microsoft.UI.Colors.Transparent;
        Content = new Border
        {
            Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0, 1, 1, 1),
            Child = _web,
        };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;
        Loaded += async (_, _) => await InitAsync();
        ActualThemeChanged += (_, _) => Post(new JsonObject { ["type"] = "setTheme", ["theme"] = ThemeName });
    }

    public event EventHandler? TextChanged;
    public event EventHandler<int>? CursorMoved;
    public event EventHandler<EditorCommandEventArgs>? CommandInvoked;

    /// <summary>Supplies GraphQL completions: (text, caret offset, explicitly requested) → result.</summary>
    public Func<string, int, bool, Task<CompletionResult>>? CompletionSource { get; set; }

    public string Text
    {
        get => _text;
        set
        {
            value = Normalize(value);
            if (value == _text) return;
            _text = value;
            Post(new JsonObject { ["type"] = "setText", ["text"] = value, ["undoable"] = false });
            Post(new JsonObject { ["type"] = "reveal" });
        }
    }

    public string CodeLanguage
    {
        get => _language;
        set
        {
            if (value == _language) return;
            _language = value;
            Post(new JsonObject { ["type"] = "setLanguage", ["language"] = value });
        }
    }

    public string Placeholder
    {
        get => _placeholder;
        set
        {
            if (value == _placeholder) return;
            _placeholder = value;
            Post(new JsonObject { ["type"] = "setPlaceholder", ["placeholder"] = value });
        }
    }

    // Set these in XAML; they are applied when the editor initialises.
    public bool IsReadOnly { get => _readOnly; set => _readOnly = value; }
    public bool WordWrap { get => _wordWrap; set => _wordWrap = value; }

    /// <summary>Replaces the whole document as a single undoable edit, keeping the caret.</summary>
    public void ReplaceText(string text)
    {
        text = Normalize(text);
        if (text == _text) return;
        _text = text;
        Post(new JsonObject { ["type"] = "setText", ["text"] = text, ["undoable"] = true });
    }

    public void FocusEditor()
    {
        if (!_ready)
        {
            _focusWhenReady = true;
            return;
        }
        _web.Focus(FocusState.Programmatic);
        Post(new JsonObject { ["type"] = "focus" });
    }

    /// <summary>Shuts down the browser instance; call when the owning tab is closed.</summary>
    public void Close()
    {
        _closed = true;
        _ready = false;
        _web.Close();
    }

    private string ThemeName => ActualTheme == ElementTheme.Light ? "light" : "dark";

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private static Task<CoreWebView2Environment> GetEnvironmentAsync() => s_environment ??= CreateEnvironmentAsync();

    private static async Task<CoreWebView2Environment> CreateEnvironmentAsync()
    {
        var dataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Brunozilla", "WebView2");
        return await CoreWebView2Environment.CreateWithOptionsAsync(null, dataFolder, new CoreWebView2EnvironmentOptions());
    }

    private async Task InitAsync()
    {
        if (_initStarted || _closed) return;
        _initStarted = true;
        try
        {
            await _web.EnsureCoreWebView2Async(await GetEnvironmentAsync());
        }
        catch (Exception ex)
        {
            Content = new TextBlock { Text = $"Code editor unavailable (WebView2): {ex.Message}", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(8) };
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.AreBrowserAcceleratorKeysEnabled = false; // F5 must send, not reload
        settings.AreDefaultContextMenusEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.IsZoomControlEnabled = false;
#if !DEBUG
        settings.AreDevToolsEnabled = false;
#endif
        core.SetVirtualHostNameToFolderMapping(HostName, Path.Combine(AppContext.BaseDirectory, "Assets"), CoreWebView2HostResourceAccessKind.Allow);
        core.WebMessageReceived += OnWebMessage;
        core.NavigationCompleted += (_, e) => { if (!e.IsSuccess && !_closed) Reload(); };
        core.ProcessFailed += (_, _) => { if (!_closed) DispatcherQueue.TryEnqueue(Reload); };
        core.Navigate($"https://{HostName}/editor/editor.html");
    }

    private int _reloads;

    private void Reload()
    {
        if (_reloads++ >= 3) return;
        _ready = false;
        _web.CoreWebView2?.Reload();
    }

    private void OnWebMessage(CoreWebView2 sender, CoreWebView2WebMessageReceivedEventArgs args)
    {
        if (JsonNode.Parse(args.WebMessageAsJson) is not JsonObject msg) return;
        switch ((string?)msg["type"])
        {
            case "ready":
                _ready = true;
                Post(new JsonObject
                {
                    ["type"] = "init",
                    ["text"] = _text,
                    ["language"] = _language,
                    ["placeholder"] = _placeholder,
                    ["readOnly"] = _readOnly,
                    ["wordWrap"] = _wordWrap,
                    ["theme"] = ThemeName,
                });
                if (_focusWhenReady)
                {
                    _focusWhenReady = false;
                    FocusEditor();
                }
                break;
            case "loadError":
                Reload();
                break;
            case "change":
                _text = (string?)msg["text"] ?? "";
                TextChanged?.Invoke(this, EventArgs.Empty);
                break;
            case "cursor":
                CursorMoved?.Invoke(this, (int?)msg["offset"] ?? 0);
                break;
            case "command":
                var command = (string?)msg["command"] switch
                {
                    "send" => EditorCommand.Send,
                    "save" => EditorCommand.Save,
                    "format" => EditorCommand.Format,
                    "docs" => EditorCommand.Docs,
                    _ => (EditorCommand?)null,
                };
                if (command != null) CommandInvoked?.Invoke(this, new EditorCommandEventArgs(command.Value, (int?)msg["offset"] ?? 0));
                break;
            case "complete":
                _ = RespondToCompletionAsync(msg);
                break;
        }
    }

    private async Task RespondToCompletionAsync(JsonObject msg)
    {
        var id = (int?)msg["id"] ?? 0;
        var items = new JsonArray();
        var start = 0;
        try
        {
            if (CompletionSource != null)
            {
                var result = await CompletionSource((string?)msg["text"] ?? "", (int?)msg["offset"] ?? 0, (bool?)msg["explicit"] ?? false);
                start = result.ReplaceStart;
                foreach (var item in result.Items)
                {
                    items.Add(new JsonObject
                    {
                        ["label"] = item.Label,
                        ["kind"] = item.Kind.ToString(),
                        ["insertText"] = item.InsertText,
                        ["detail"] = item.Detail,
                        ["description"] = item.Description,
                        ["deprecated"] = item.IsDeprecated,
                    });
                }
            }
        }
        finally
        {
            Post(new JsonObject { ["type"] = "response", ["id"] = id, ["start"] = start, ["items"] = items });
        }
    }

    private void Post(JsonObject message)
    {
        if (!_ready || _closed) return;
        _web.CoreWebView2?.PostWebMessageAsJson(message.ToJsonString());
    }
}
