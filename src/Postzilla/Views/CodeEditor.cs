using Postzilla.GraphQL;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using VirtualKey = Windows.System.VirtualKey;
using WinUIEditor;

namespace Postzilla.Views;

public enum EditorCommand { Send, Save, Format, Docs }

public sealed class EditorCommandEventArgs(EditorCommand command, int offset) : EventArgs
{
    public EditorCommand Command { get; } = command;
    public int Offset { get; } = offset;
}

/// <summary>
/// Native Scintilla code editor (WinUIEdit). Offsets exposed to callers are UTF-16 offsets into LF-normalised <see cref="Text"/>.
/// </summary>
public sealed partial class CodeEditor : UserControl
{
    private const string FontName = "Cascadia Mono";
    private const int FontPoints = 10;
    private const char ListSeparator = '\n';
    private const string CompletionTriggers = "$@(:{";

    private readonly CodeEditorControl _control = new();
    private readonly TextBlock _placeholderBlock;
    private readonly DispatcherQueueTimer _cursorTimer;
    private string? _text = "";   // null = stale, re-read from the editor on demand
    private string _language = "plaintext";
    private bool _readOnly;
    private bool _wordWrap;
    private bool _hostEdit;
    private bool _closed;
    private int _version;
    private Dictionary<string, CompletionItem> _shownItems = new();

    public CodeEditor()
    {
        _placeholderBlock = new TextBlock
        {
            IsHitTestVisible = false,
            FontFamily = new FontFamily(FontName),
            FontSize = 13,
            Opacity = 0.5,
            TextWrapping = TextWrapping.Wrap,
        };
        Content = new Border
        {
            Background = (Brush)Application.Current.Resources["ControlFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["ControlStrokeColorDefaultBrush"],
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(0, 1, 1, 1),
            Child = new Grid { Children = { _control, _placeholderBlock, CreateFindBar() } },
        };
        HorizontalAlignment = HorizontalAlignment.Stretch;
        VerticalAlignment = VerticalAlignment.Stretch;

        _cursorTimer = DispatcherQueue.GetForCurrentThread().CreateTimer();
        _cursorTimer.Interval = TimeSpan.FromMilliseconds(120);
        _cursorTimer.IsRepeating = false;
        _cursorTimer.Tick += (_, _) =>
        {
            if (!_closed && Editor.SelectionEmpty) CursorMoved?.Invoke(this, CaretOffset);
        };

        InitEditor();
        PreviewKeyDown += OnPreviewKeyDown;
        _control.Loaded += (_, _) => UpdatePlaceholder();
    }

    public event EventHandler? TextChanged;
    public event EventHandler<int>? CursorMoved;
    public event EventHandler<EditorCommandEventArgs>? CommandInvoked;

    /// <summary>Supplies GraphQL completions: (text, caret offset, explicitly requested) → result.</summary>
    public Func<string, int, bool, Task<CompletionResult>>? CompletionSource { get; set; }

    public string Text
    {
        get => _text ??= Normalize(Editor.GetText(Editor.Length));
        set
        {
            value = Normalize(value);
            if (value == Text) return;
            HostEdit(() =>
            {
                Editor.SetText(value);
                Editor.EmptyUndoBuffer();
                Editor.GotoPos(0);
            });
            _text = value;
            UpdatePlaceholder();
        }
    }

    public string CodeLanguage
    {
        get => _language;
        set
        {
            if (value == _language) return;
            _language = value;
            ApplyLanguage();
        }
    }

    public string Placeholder
    {
        get => _placeholderBlock.Text;
        set => _placeholderBlock.Text = value;
    }

    public bool IsReadOnly
    {
        get => _readOnly;
        set { _readOnly = value; Editor.ReadOnly = value; }
    }

    public bool WordWrap
    {
        get => _wordWrap;
        set { _wordWrap = value; Editor.WrapMode = value ? Wrap.Word : Wrap.None; }
    }

    /// <summary>Replaces the whole document as a single undoable edit, keeping the caret.</summary>
    public void ReplaceText(string text)
    {
        text = Normalize(text);
        if (text == Text) return;
        var caret = CaretOffset;
        var firstLine = Editor.FirstVisibleLine;
        Editor.BeginUndoAction();
        Editor.TargetWholeDocument();
        Editor.ReplaceTarget(-1, text);
        Editor.EndUndoAction();
        var pos = Editor.PositionRelativeCodeUnits(0, Math.Min(caret, text.Length));
        Editor.SetSel(pos, pos);
        Editor.FirstVisibleLine = firstLine;
    }

    public void FocusEditor()
    {
        _control.Focus(FocusState.Programmatic);
        Editor.Focus = true;
    }

    public void Close()
    {
        _closed = true;
        _cursorTimer.Stop();
        Editor.AutoCCancel();
    }

    private Editor Editor => _control.Editor;

    private int CaretOffset => (int)Editor.CountCodeUnits(0, Editor.CurrentPos);

    private static string Normalize(string? text) => (text ?? "").Replace("\r\n", "\n").Replace('\r', '\n');

    private void InitEditor()
    {
        var e = Editor;
        e.EOLMode = EndOfLine.Lf;
        e.PasteConvertEndings = true;
        e.TabWidth = 2;
        e.UseTabs = false;
        e.ModEventMask = ModificationFlags.InsertText | ModificationFlags.DeleteText;
        e.AutoCSeparator = ListSeparator;
        e.AutoCTypeSeparator = 0x1E;
        e.AutoCOrder = Ordering.Custom;
        e.AutoCIgnoreCase = true;
        e.AutoCAutoHide = false;
        e.AutoCDropRestOfWord = false;
        e.AutoCMaxHeight = 12;
        e.AutoCMaxWidth = 0;

        e.Modified += OnModified;
        e.CharAdded += OnCharAdded;
        e.AutoCSelection += OnAutoCSelection;
        e.AutoCCharDeleted += (_, _) => _ = ShowCompletionsAsync(false);
        e.UpdateUI += (_, args) =>
        {
            if ((args.Updated & (int)Update.Selection) == 0 || _closed) return;
            _cursorTimer.Stop();
            _cursorTimer.Start();
        };
        e.StyleNeeded += (_, args) => StyleGraphQL(args.Position);

        _control.SyntaxHighlightingApplied += (_, _) => ApplyStyles();
        _control.DefaultColorsChanged += (_, _) => { ApplyStyles(); ApplyFindColors(); };
        InitFindIndicator();
        ApplyLanguage();
    }

    private void ApplyLanguage()
    {
        _control.HighlightingLanguage = _language switch
        {
            "json" or "html" or "xml" or "yaml" or "javascript" => _language,
            "graphql" => "graphql",   // unknown to WinUIEdit → container lexer, styled by StyleGraphQL
            _ => "plaintext",
        };
        ApplyStyles();
    }

    // Style slots used by the GraphQL container lexer (indexes < 32 so they don't clash with Scintilla's predefined styles).
    private static readonly (GraphQLTokenKind Kind, int Dark, int Light, bool Italic)[] GraphQLStyles =
    [
        (GraphQLTokenKind.Comment, 0x6A9955, 0x008000, true),
        (GraphQLTokenKind.String, 0xCE9178, 0xA31515, false),
        (GraphQLTokenKind.Number, 0xB5CEA8, 0x098658, false),
        (GraphQLTokenKind.Keyword, 0x569CD6, 0x0000FF, false),
        (GraphQLTokenKind.Constant, 0x569CD6, 0x0000FF, false),
        (GraphQLTokenKind.Variable, 0x4FC1FF, 0x0070C1, false),
        (GraphQLTokenKind.Directive, 0xDCDCAA, 0x795E26, false),
        (GraphQLTokenKind.Type, 0x4EC9B0, 0x267F99, false),
        (GraphQLTokenKind.Field, 0x9CDCFE, 0x001080, false),
        (GraphQLTokenKind.OperationName, 0xDCDCAA, 0x795E26, false),
    ];

    private static int Bgr(int rgb) => ((rgb & 0xFF) << 16) | (rgb & 0xFF00) | ((rgb >> 16) & 0xFF);

    private void ApplyStyles()
    {
        var e = Editor;
        for (var style = 0; style < 40; style++)
        {
            e.StyleSetFont(style, FontName);
            e.StyleSetSize(style, FontPoints);
        }
        if (_language == "graphql")
        {
            var dark = ActualTheme != ElementTheme.Light;
            foreach (var (kind, darkColor, lightColor, italic) in GraphQLStyles)
            {
                e.StyleSetFore((int)kind, Bgr(dark ? darkColor : lightColor));
                e.StyleSetItalic((int)kind, italic);
            }
            StyleGraphQL(e.Length);
        }
        DispatcherQueue.TryEnqueue(UpdatePlaceholder);
    }

    private void StyleGraphQL(long _)
    {
        if (_language != "graphql") return;
        // Tokenising the whole document keeps brace context correct; queries are small enough for this to be cheap.
        var e = Editor;
        var text = e.GetText(e.Length);
        e.StartStyling(0, 0);
        var pos = 0L;
        var offset = 0;
        foreach (var token in GraphQLHighlighter.Tokenize(text))
        {
            if (token.Start > offset)
            {
                var gap = e.PositionRelativeCodeUnits(pos, token.Start - offset) - pos;
                e.SetStyling(gap, 0);
                pos += gap;
            }
            var len = e.PositionRelativeCodeUnits(pos, token.Length) - pos;
            e.SetStyling(len, token.Kind == GraphQLTokenKind.Punctuation ? 0 : (int)token.Kind);
            pos += len;
            offset = token.Start + token.Length;
        }
        if (e.Length > pos) e.SetStyling(e.Length - pos, 0);
    }

    private void HostEdit(Action edit)
    {
        _hostEdit = true;
        Editor.ReadOnly = false;
        try { edit(); }
        finally
        {
            Editor.ReadOnly = _readOnly;
            _hostEdit = false;
        }
    }

    private void OnModified(Editor sender, ModifiedEventArgs args)
    {
        if ((args.ModificationType & (int)(ModificationFlags.InsertText | ModificationFlags.DeleteText)) == 0) return;
        _text = null;
        _version++;
        QueueFindRefresh();
        if (_hostEdit) return;
        UpdatePlaceholder();
        TextChanged?.Invoke(this, EventArgs.Empty);
    }

    private void UpdatePlaceholder()
    {
        _placeholderBlock.Visibility = Editor.Length == 0 && !string.IsNullOrEmpty(_placeholderBlock.Text) ? Visibility.Visible : Visibility.Collapsed;
        if (_placeholderBlock.Visibility == Visibility.Collapsed || XamlRoot == null) return;
        var px = 0;
        for (var m = 0; m < 5; m++) px += Editor.GetMarginWidthN(m);
        var scale = XamlRoot.RasterizationScale;
        _placeholderBlock.Margin = new Thickness((px + 4) / scale, 1, 4, 0);
    }

    private void OnPreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        var ctrl = IsDown(VirtualKey.Control);
        var shift = IsDown(VirtualKey.Shift);
        var alt = IsDown(VirtualKey.Menu);
        EditorCommand? command = e.Key switch
        {
            VirtualKey.Enter when ctrl && !shift && !alt => EditorCommand.Send,
            VirtualKey.F5 when !ctrl && !shift && !alt => EditorCommand.Send,
            VirtualKey.S when ctrl && !shift && !alt => EditorCommand.Save,
            VirtualKey.F when shift && alt && !ctrl => EditorCommand.Format,
            VirtualKey.F1 => EditorCommand.Docs,
            _ => null,
        };
        if (command != null)
        {
            e.Handled = true;
            Editor.AutoCCancel();
            CommandInvoked?.Invoke(this, new EditorCommandEventArgs(command.Value, CaretOffset));
        }
        else if (HandleFindKey(e.Key, ctrl, shift, alt))
        {
            e.Handled = true;
        }
        else if (e.Key == VirtualKey.Space && ctrl && !shift && !alt && _findBox.FocusState == FocusState.Unfocused)
        {
            e.Handled = true;
            _ = ShowCompletionsAsync(true);
        }
    }

    private static bool IsDown(VirtualKey key) =>
        Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);

    private void OnCharAdded(Editor sender, CharAddedEventArgs args)
    {
        var c = (char)args.Ch;
        if (char.IsLetterOrDigit(c) || c == '_' || CompletionTriggers.Contains(c)) _ = ShowCompletionsAsync(false);
        else if (Editor.AutoCActive()) Editor.AutoCCancel();
    }

    private async Task ShowCompletionsAsync(bool explicitRequest)
    {
        if (CompletionSource == null || _readOnly || _closed) return;
        var version = _version;
        var text = Text;
        var offset = CaretOffset;
        CompletionResult result;
        try { result = await CompletionSource(text, offset, explicitRequest); }
        catch { return; }
        if (_closed || version != _version || offset != CaretOffset) return;

        var e = Editor;
        if (result.Items.Count == 0)
        {
            e.AutoCCancel();
            return;
        }

        var shown = new Dictionary<string, CompletionItem>();
        var lines = new List<string>(result.Items.Count);
        foreach (var item in result.Items)
        {
            var display = CompletionRow.Encode(item);
            if (shown.TryAdd(display, item)) lines.Add(display);
        }
        _shownItems = shown;

        var start = Math.Clamp(result.ReplaceStart, 0, offset);
        var startPos = e.PositionRelativeCodeUnits(0, start);
        e.AutoCShow(e.CurrentPos - startPos, string.Join(ListSeparator, lines));
        DispatcherQueue.TryEnqueue(() => FitCompletionPopup(lines.Count));
    }

    // WinUIEdit sizes the popup (and its shadow) for 32 dip rows; our template uses compact rows, so shrink it to fit.
    private const double CompletionRowHeight = 22;
    private const int CompletionVisibleRows = 12;

    private void FitCompletionPopup(int itemCount)
    {
        if (XamlRoot == null || !Editor.AutoCActive()) return;
        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is not AutocompletionControl list || double.IsNaN(list.Height)) continue;
            var fitted = Math.Min(itemCount, CompletionVisibleRows) * CompletionRowHeight + 6;
            var excess = list.Height - fitted;
            if (excess <= 0) continue;
            var caretTop = _control.TransformToVisual(null).TransformPoint(
                new Windows.Foundation.Point(0, Editor.PointYFromPosition(Editor.CurrentPos) / XamlRoot.RasterizationScale)).Y;
            if (popup.VerticalOffset < caretTop) popup.VerticalOffset += excess;   // shown above the caret: keep it attached
            list.Height = fitted;
        }
    }

    private void OnAutoCSelection(Editor sender, AutoCSelectionEventArgs args)
    {
        if (!_shownItems.TryGetValue(args.Text, out var item)) return;
        var e = Editor;
        e.AutoCCancel();   // stop Scintilla inserting the display text; insert the real text instead
        var start = args.Position;
        var end = e.CurrentPos;
        e.SetTargetRange(start, end);
        e.ReplaceTarget(-1, item.InsertText);
        var caret = e.PositionRelativeCodeUnits(start, item.InsertText.Length);
        e.SetSel(caret, caret);
        if (item.InsertText.EndsWith(": ", StringComparison.Ordinal))
            DispatcherQueue.TryEnqueue(() => _ = ShowCompletionsAsync(false));
    }
}
