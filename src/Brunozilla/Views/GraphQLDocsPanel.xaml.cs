using Brunozilla.GraphQL;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using Windows.UI.Text;

namespace Brunozilla.Views;

/// <summary>Schema documentation explorer (GraphiQL "Docs" style) with history.</summary>
public sealed partial class GraphQLDocsPanel : UserControl
{
    private sealed record DocView(string Kind, string? TypeName = null, string? FieldName = null, string? Highlight = null);

    private readonly Stack<DocView> _history = new();
    private GqlSchema? _schema;
    private DocView _current = new("home");

    public GraphQLDocsPanel()
    {
        InitializeComponent();
        Render();
    }

    public GqlSchema? Schema
    {
        get => _schema;
        set
        {
            _schema = value;
            _history.Clear();
            _current = new DocView("home");
            SearchBox.Text = "";
            Render();
        }
    }

    public void ShowTarget(DocTarget target, bool pushHistory)
    {
        if (_schema == null) return;
        DocView? view = target.Kind switch
        {
            DocTargetKind.Type when _schema.GetType(target.TypeName) != null => new DocView("type", target.TypeName),
            DocTargetKind.Field or DocTargetKind.Argument when target.FieldName != null && _schema.GetField(target.TypeName, target.FieldName) != null
                => new DocView("field", target.TypeName, target.FieldName, target.ArgumentName),
            _ => null,
        };
        if (view != null) Navigate(view, pushHistory);
    }

    private void Navigate(DocView view, bool pushHistory = true)
    {
        if (view == _current && SearchBox.Text.Length == 0) return;
        if (pushHistory) _history.Push(_current);
        _current = view;
        if (SearchBox.Text.Length > 0) SearchBox.Text = "";
        Render();
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (_history.Count == 0) return;
        _current = _history.Pop();
        SearchBox.Text = "";
        Render();
    }

    private void Home_Click(object sender, RoutedEventArgs e) => Navigate(new DocView("home"));

    public event EventHandler? CloseRequested;

    private void Close_Click(object sender, RoutedEventArgs e) => CloseRequested?.Invoke(this, EventArgs.Empty);

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) => Render();

    // ------------------------------------------------------------------ rendering

    private void Render()
    {
        BackButton.IsEnabled = _history.Count > 0;
        DocText.Blocks.Clear();

        if (_schema == null)
        {
            TitleText.Text = "Docs";
            Para().Inlines.Add(new Run { Text = "No schema loaded. Use “Fetch schema” to introspect the endpoint, then press F1 in the query editor to look up the item under the cursor." });
            return;
        }

        var search = SearchBox.Text.Trim();
        if (search.Length > 0)
        {
            RenderSearch(search);
            return;
        }

        switch (_current.Kind)
        {
            case "type" when _schema.GetType(_current.TypeName) is { } type:
                RenderType(type);
                break;
            case "field" when _schema.GetField(_current.TypeName, _current.FieldName!) is { } field:
                RenderField(_current.TypeName!, field, _current.Highlight);
                break;
            default:
                RenderHome();
                break;
        }
    }

    private void RenderHome()
    {
        TitleText.Text = "Schema";
        Section("Root types");
        foreach (var (label, name) in new[] { ("query", _schema!.QueryTypeName), ("mutation", _schema.MutationTypeName), ("subscription", _schema.SubscriptionTypeName) })
        {
            if (name == null) continue;
            var p = Para();
            p.Inlines.Add(Keyword(label + ": "));
            p.Inlines.Add(TypeLink(name));
        }

        Section("All types");
        foreach (var t in _schema.Types.Values.Where(t => !t.Name.StartsWith("__")).OrderBy(t => t.Name, StringComparer.OrdinalIgnoreCase))
        {
            var p = Para(2);
            p.Inlines.Add(TypeLink(t.Name));
            p.Inlines.Add(Secondary("  " + t.Kind.ToLowerInvariant().Replace('_', ' ')));
        }
    }

    private void RenderType(GqlType type)
    {
        TitleText.Text = type.Name;
        Para().Inlines.Add(Secondary(type.Kind.Replace('_', ' ')));
        Description(type.Description);

        if (type.Interfaces.Count > 0)
        {
            var p = Para();
            p.Inlines.Add(Keyword("implements "));
            for (var i = 0; i < type.Interfaces.Count; i++)
            {
                if (i > 0) p.Inlines.Add(new Run { Text = " & " });
                p.Inlines.Add(TypeLink(type.Interfaces[i]));
            }
        }

        if (type.Fields.Count > 0)
        {
            Section("Fields");
            foreach (var f in type.Fields) FieldEntry(type.Name, f);
        }

        if (type.InputFields.Count > 0)
        {
            Section("Input fields");
            foreach (var f in type.InputFields) InputValueEntry(f, highlight: false);
        }

        if (type.EnumValues.Count > 0)
        {
            Section("Values");
            foreach (var v in type.EnumValues)
            {
                var p = Para(10);
                p.Inlines.Add(Code(v.Name, bold: true));
                Description(v.Description);
                Deprecation(v.IsDeprecated, v.DeprecationReason);
            }
        }

        if (type.PossibleTypes.Count > 0)
        {
            Section(type.Kind == "UNION" ? "Members" : "Implementations");
            foreach (var name in type.PossibleTypes) Para(2).Inlines.Add(TypeLink(name));
        }
    }

    private void RenderField(string parentType, GqlField field, string? highlightArg)
    {
        TitleText.Text = field.Name;
        var parent = Para();
        parent.Inlines.Add(Secondary("field on "));
        parent.Inlines.Add(TypeLink(parentType));

        var sig = Para(10);
        AddSignature(sig.Inlines, parentType, field, linkName: false);
        Description(field.Description);
        Deprecation(field.IsDeprecated, field.DeprecationReason);

        if (field.Args.Count > 0)
        {
            Section("Arguments");
            foreach (var a in field.Args) InputValueEntry(a, a.Name == highlightArg);
        }

        Section("Type");
        var t = Para();
        AddTypeRef(t.Inlines, field.Type);
        Description(_schema!.GetType(field.Type.NamedType)?.Description);
    }

    private void RenderSearch(string term)
    {
        TitleText.Text = "Search";
        var types = _schema!.Types.Values
            .Where(t => !t.Name.StartsWith("__") && t.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .OrderBy(t => t.Name.Length).Take(50).ToList();
        var fields = _schema.Types.Values
            .Where(t => !t.Name.StartsWith("__"))
            .SelectMany(t => t.Fields.Select(f => (Type: t, Field: f)))
            .Where(x => x.Field.Name.Contains(term, StringComparison.OrdinalIgnoreCase))
            .Take(100).ToList();

        if (types.Count == 0 && fields.Count == 0)
        {
            Para().Inlines.Add(Secondary($"No matches for “{term}”."));
            return;
        }
        if (types.Count > 0)
        {
            Section("Types");
            foreach (var t in types) Para(2).Inlines.Add(TypeLink(t.Name));
        }
        if (fields.Count > 0)
        {
            Section("Fields");
            foreach (var (type, field) in fields)
            {
                var p = Para(2);
                p.Inlines.Add(TypeLink(type.Name));
                p.Inlines.Add(new Run { Text = "." });
                p.Inlines.Add(Link(field.Name, () => Navigate(new DocView("field", type.Name, field.Name))));
                p.Inlines.Add(new Run { Text = ": " });
                AddTypeRef(p.Inlines, field.Type);
            }
        }
    }

    // ------------------------------------------------------------------ building blocks

    private void FieldEntry(string parentType, GqlField f)
    {
        var p = Para(10);
        AddSignature(p.Inlines, parentType, f, linkName: true);
        Description(f.Description);
        Deprecation(f.IsDeprecated, f.DeprecationReason);
    }

    private void InputValueEntry(GqlInputValue v, bool highlight)
    {
        var p = Para(8);
        p.Inlines.Add(Code(v.Name, bold: true, accent: highlight));
        p.Inlines.Add(new Run { Text = ": " });
        AddTypeRef(p.Inlines, v.Type);
        if (v.DefaultValue != null) p.Inlines.Add(Secondary(" = " + v.DefaultValue));
        Description(v.Description);
    }

    private void AddSignature(InlineCollection inlines, string parentType, GqlField f, bool linkName)
    {
        if (linkName) inlines.Add(Link(f.Name, () => Navigate(new DocView("field", parentType, f.Name)), bold: true));
        else inlines.Add(Code(f.Name, bold: true));

        if (f.Args.Count > 0)
        {
            inlines.Add(new Run { Text = "(" });
            for (var i = 0; i < f.Args.Count; i++)
            {
                if (i > 0) inlines.Add(new Run { Text = ", " });
                inlines.Add(new Run { Text = f.Args[i].Name + ": " });
                AddTypeRef(inlines, f.Args[i].Type);
            }
            inlines.Add(new Run { Text = ")" });
        }
        inlines.Add(new Run { Text = ": " });
        AddTypeRef(inlines, f.Type);
    }

    private void AddTypeRef(InlineCollection inlines, GqlTypeRef type)
    {
        var text = type.ToString();
        var named = type.NamedType;
        var idx = named.Length > 0 ? text.IndexOf(named, StringComparison.Ordinal) : -1;
        if (idx < 0)
        {
            inlines.Add(new Run { Text = text });
            return;
        }
        if (idx > 0) inlines.Add(new Run { Text = text[..idx] });
        inlines.Add(TypeLink(named));
        if (idx + named.Length < text.Length) inlines.Add(new Run { Text = text[(idx + named.Length)..] });
    }

    private Hyperlink TypeLink(string name) => Link(name, () => Navigate(new DocView("type", name)));

    private static Hyperlink Link(string text, Action onClick, bool bold = false)
    {
        var link = new Hyperlink { UnderlineStyle = UnderlineStyle.None, FontFamily = CodeFont };
        if (bold) link.FontWeight = FontWeights.SemiBold;
        link.Inlines.Add(new Run { Text = text });
        link.Click += (_, _) => onClick();
        return link;
    }

    private Paragraph Para(double topMargin = 4)
    {
        var p = new Paragraph { Margin = new Thickness(0, topMargin, 0, 0) };
        DocText.Blocks.Add(p);
        return p;
    }

    private void Section(string title)
    {
        var p = Para(18);
        p.Inlines.Add(new Run { Text = title.ToUpperInvariant(), FontWeight = FontWeights.SemiBold, FontSize = 11, Foreground = SecondaryBrush });
    }

    private void Description(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        var p = Para(2);
        p.Inlines.Add(new Run { Text = text.Trim(), Foreground = SecondaryBrush, FontSize = 12 });
    }

    private void Deprecation(bool deprecated, string? reason)
    {
        if (!deprecated) return;
        var p = Para(2);
        p.Inlines.Add(new Run
        {
            Text = "Deprecated" + (string.IsNullOrWhiteSpace(reason) ? "" : ": " + reason),
            Foreground = new SolidColorBrush(Windows.UI.Color.FromArgb(255, 0xD9, 0x7B, 0x06)),
            FontSize = 12,
        });
    }

    private static Run Code(string text, bool bold = false, bool accent = false)
    {
        var run = new Run { Text = text, FontFamily = CodeFont };
        if (bold) run.FontWeight = FontWeights.SemiBold;
        if (accent) run.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        return run;
    }

    private static Run Keyword(string text) => new() { Text = text, FontFamily = CodeFont, Foreground = SecondaryBrush };

    private static Run Secondary(string text) => new() { Text = text, Foreground = SecondaryBrush, FontSize = 12 };

    private static Brush SecondaryBrush => (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];

    private static FontFamily CodeFont => (FontFamily)Application.Current.Resources["CodeFont"];
}

