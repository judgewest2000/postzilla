using System.Text;

namespace Postzilla.GraphQL;

/// <summary>Pretty-prints GraphQL documents: one selection per line; argument lists break onto lines past 80 columns.</summary>
public static class GraphQLFormatter
{
    private enum K { Name, Number, String, Comment, Punct, Spread }

    private readonly record struct T(K Kind, string Text);

    public static string Format(string text, int indentSize = 2)
    {
        var toks = Tokenize(text ?? "");
        var sb = new StringBuilder();
        var blocks = new Stack<bool>(); // true = selection set / definition body, false = input object value
        var indent = 0;
        var paren = 0;
        var lineStart = true;
        var blankLine = false;
        T? prev = null, prev2 = null;

        void NewLine()
        {
            while (sb.Length > 0 && sb[^1] == ' ') sb.Length--;
            if (sb.Length > 0) sb.Append('\n');
            lineStart = true;
        }

        void Emit(string sep, string s)
        {
            if (lineStart)
            {
                if (blankLine && sb.Length > 0) sb.Append('\n');
                blankLine = false;
                sb.Append(' ', indent * indentSize);
                lineStart = false;
            }
            else sb.Append(sep);
            sb.Append(s);
        }

        for (var ti = 0; ti < toks.Count; ti++)
        {
            var t = toks[ti];
            var inValue = paren > 0 || (blocks.Count > 0 && !blocks.Peek());

            // Argument / variable-definition lists: laid out as a whole, breaking onto lines when too long.
            if (t.Kind == K.Punct && t.Text == "(" && !inValue)
            {
                var j = ti;
                var group = ParseGroup(toks, ref j);
                if (group != null)
                {
                    var sep = prev == null ? "" : BlockSeparator(prev.Value, prev2, t, blocks.Count > 0);
                    if (sep == "\n")
                    {
                        NewLine();
                        sep = "";
                    }
                    var column = lineStart
                        ? indent * indentSize
                        : sb.Length - (sb.ToString().LastIndexOf('\n') + 1) + sep.Length;
                    var trailing = j < toks.Count && toks[j].Text == "{" ? 2 : 0;
                    Emit(sep, Render(group, indent, column, trailing, indentSize));
                    prev2 = toks[j - 2];
                    prev = toks[j - 1];
                    ti = j - 1;
                    continue;
                }
            }

            if (t.Kind == K.Comment)
            {
                if (!lineStart) NewLine();
                Emit("", t.Text);
                NewLine();
                continue;
            }

            if (t.Kind == K.Punct && t.Text == "{" && !inValue)
            {
                Emit(" ", "{");
                indent++;
                blocks.Push(true);
                NewLine();
            }
            else if (t.Kind == K.Punct && t.Text == "}" && blocks.Count > 0 && blocks.Peek() && paren == 0)
            {
                if (!lineStart) NewLine();
                indent = Math.Max(0, indent - 1);
                Emit("", "}");
                blocks.Pop();
                NewLine();
                if (!blocks.Contains(true)) blankLine = true;
            }
            else
            {
                if (t.Kind == K.Punct)
                {
                    switch (t.Text)
                    {
                        case "(": paren++; break;
                        case ")": paren = Math.Max(0, paren - 1); break;
                        case "{": blocks.Push(false); break;
                        case "}": if (blocks.Count > 0 && !blocks.Peek()) blocks.Pop(); break;
                    }
                }

                var sep = prev == null ? "" : inValue ? ValueSeparator(prev.Value, t) : BlockSeparator(prev.Value, prev2, t, blocks.Count > 0);
                if (sep == "\n")
                {
                    NewLine();
                    sep = "";
                }
                Emit(sep, t.Text);
            }

            prev2 = prev;
            prev = t;
        }

        while (sb.Length > 0 && char.IsWhiteSpace(sb[^1])) sb.Length--;
        return sb.ToString();
    }

    private const int MaxWidth = 80;

    /// <summary>A bracketed list — (args), {object}, [list] — split into items; elements are tokens or nested groups.</summary>
    private sealed record Group(string Open, string Close, List<List<object>> Items)
    {
        public bool HasComment => Items.Any(i => i.Any(e => e is T { Kind: K.Comment } || e is Group { HasComment: true }));
    }

    private static T First(object e) => e is Group g ? new T(K.Punct, g.Open) : (T)e;
    private static T Last(object e) => e is Group g ? new T(K.Punct, g.Close) : (T)e;

    /// <summary>Parses the group opening at <paramref name="i"/>; leaves <paramref name="i"/> after its close. Null if unbalanced.</summary>
    private static Group? ParseGroup(List<T> toks, ref int i)
    {
        var open = toks[i].Text;
        var close = open switch { "(" => ")", "[" => "]", _ => "}" };
        var items = new List<List<object>>();
        var current = new List<object>();
        i++;
        while (i < toks.Count)
        {
            var t = toks[i];
            if (t.Kind == K.Punct && t.Text is ")" or "]" or "}")
            {
                if (t.Text != close) return null;
                i++;
                if (current.Count > 0) items.Add(current);
                return new Group(open, close, items);
            }
            if (t.Kind == K.Comment)
            {
                if (current.Count > 0) items.Add(current);
                items.Add([t]);
                current = [];
                i++;
                continue;
            }
            if (current.Count > 0 && IsValueEnd(Last(current[^1])) && IsValueStart(t))
            {
                items.Add(current);
                current = [];
            }
            if (t.Kind == K.Punct && t.Text is "(" or "[" or "{")
            {
                var nested = ParseGroup(toks, ref i);
                if (nested == null) return null;
                current.Add(nested);
            }
            else
            {
                current.Add(t);
                i++;
            }
        }
        return null;
    }

    private static string Flat(Group g)
    {
        var pad = g.Open == "{" && g.Items.Count > 0 ? " " : "";
        return g.Open + pad + string.Join(", ", g.Items.Select(i => RenderItem(i, null, 0, 0))) + pad + g.Close;
    }

    /// <summary>Renders flat when it fits within <see cref="MaxWidth"/>, otherwise one item per line.</summary>
    private static string Render(Group g, int level, int column, int trailing, int indentSize)
    {
        if (!g.HasComment)
        {
            var flat = Flat(g);
            if (g.Items.Count == 0 || column + flat.Length + trailing <= MaxWidth) return flat;
        }
        var sb = new StringBuilder(g.Open);
        var itemIndent = new string(' ', (level + 1) * indentSize);
        foreach (var item in g.Items)
            sb.Append('\n').Append(itemIndent).Append(RenderItem(item, level + 1, itemIndent.Length, indentSize));
        sb.Append('\n').Append(' ', level * indentSize).Append(g.Close);
        return sb.ToString();
    }

    /// <summary>Renders one item; <paramref name="level"/> null means everything flat.</summary>
    private static string RenderItem(List<object> item, int? level, int column, int indentSize)
    {
        var sb = new StringBuilder();
        object? prev = null;
        foreach (var e in item)
        {
            if (prev != null) sb.Append(ValueSeparator(Last(prev), First(e)));
            if (e is Group g)
            {
                var lastNl = sb.ToString().LastIndexOf('\n');
                var col = lastNl < 0 ? column + sb.Length : sb.Length - lastNl - 1;
                sb.Append(level is { } l ? Render(g, l, col, 0, indentSize) : Flat(g));
            }
            else sb.Append(((T)e).Text);
            prev = e;
        }
        return sb.ToString();
    }

    private static bool IsValueEnd(T t) =>
        t.Kind is K.Name or K.Number or K.String || (t.Kind == K.Punct && t.Text is "]" or "}" or "!");

    private static bool IsValueStart(T t) =>
        t.Kind is K.Name or K.Number or K.String || (t.Kind == K.Punct && t.Text is "[" or "{" or "$");

    private static string ValueSeparator(T prev, T cur)
    {
        if (IsValueEnd(prev) && IsValueStart(cur)) return ", ";
        if (prev.Kind == K.Punct && prev.Text is "(" or "[" or "$" or "@") return "";
        if (cur.Kind == K.Punct && cur.Text is ")" or "]" or "!" or ":" or "(") return "";
        return " ";
    }

    private static string BlockSeparator(T prev, T? prev2, T cur, bool inBlock)
    {
        var afterOnKeyword = prev.Kind == K.Name && prev.Text == "on" && prev2?.Kind == K.Spread;
        var startsItem = cur.Kind is K.Name or K.Spread or K.String;
        var endsItem = prev.Kind is K.Name or K.Number or K.String || (prev.Kind == K.Punct && prev.Text is ")" or "]" or "!");
        if (inBlock && startsItem && endsItem && !afterOnKeyword) return "\n";

        if (prev.Kind == K.Punct && prev.Text is "$" or "@" or "(" or "[") return "";
        if (prev.Kind == K.Spread && !(cur.Kind == K.Name && cur.Text == "on")) return "";
        if (cur.Kind == K.Punct && cur.Text is ":" or "!" or ")" or "]" or "(") return "";
        return " ";
    }

    private static List<T> Tokenize(string s)
    {
        var toks = new List<T>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ',' || c == '\uFEFF') { i++; continue; }
            var start = i;
            if (c == '#')
            {
                while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
                toks.Add(new T(K.Comment, s[start..i].TrimEnd()));
                continue;
            }
            if (c == '"')
            {
                if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
                {
                    var end = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    i = end < 0 ? s.Length : end + 3;
                }
                else
                {
                    i++;
                    while (i < s.Length && s[i] != '"' && s[i] != '\n' && s[i] != '\r')
                    {
                        if (s[i] == '\\') i++;
                        i++;
                    }
                    i = Math.Min(s.Length, i + 1);
                }
                toks.Add(new T(K.String, s[start..i]));
                continue;
            }
            if (c == '.' && i + 2 < s.Length && s[i + 1] == '.' && s[i + 2] == '.')
            {
                toks.Add(new T(K.Spread, "..."));
                i += 3;
                continue;
            }
            if (c == '_' || char.IsAsciiLetter(c))
            {
                while (i < s.Length && (s[i] == '_' || char.IsAsciiLetterOrDigit(s[i]))) i++;
                toks.Add(new T(K.Name, s[start..i]));
                continue;
            }
            if (c == '-' || char.IsAsciiDigit(c))
            {
                i++;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '.' or '+' or '-')) i++;
                toks.Add(new T(K.Number, s[start..i]));
                continue;
            }
            toks.Add(new T(K.Punct, c.ToString()));
            i++;
        }
        return toks;
    }
}
