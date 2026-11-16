namespace Postzilla.GraphQL;

public enum DocTargetKind { None, Type, Field, Argument }

/// <summary>What the documentation panel should show for a cursor position.</summary>
public sealed record DocTarget(DocTargetKind Kind, string? TypeName = null, string? FieldName = null, string? ArgumentName = null)
{
    public static readonly DocTarget None = new(DocTargetKind.None);
}

/// <summary>
/// Lightweight, error-tolerant walk over a GraphQL document that works out which
/// schema type/field/argument sits under the caret (so it works on half-typed queries).
/// </summary>
public static class GraphQLContextResolver
{
    internal enum TokKind { None, Name, Punct, Spread, Other }

    internal readonly record struct Tok(TokKind Kind, string Text, int Start, int End);

    public static DocTarget Resolve(GqlSchema schema, string text, int caret)
    {
        var toks = Lex(text ?? "");
        var target = toks.FindIndex(t => t.Kind == TokKind.Name && caret >= t.Start && caret <= t.End);

        var stack = new Stack<string?>();
        string? pending = null;
        string? argParent = null;
        GqlField? argField = null;
        var parenDepth = 0;
        var valueNesting = 0;
        var varDefs = false;

        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (target < 0 && t.Start >= caret) break;

            var isTarget = i == target;
            var prev = i > 0 ? toks[i - 1] : default;
            var next = i + 1 < toks.Count ? toks[i + 1] : default;

            if (t.Kind == TokKind.Punct)
            {
                switch (t.Text)
                {
                    case "(":
                        parenDepth++;
                        if (parenDepth == 1) varDefs = stack.Count == 0;
                        break;
                    case ")":
                        parenDepth = Math.Max(0, parenDepth - 1);
                        if (parenDepth == 0) valueNesting = 0;
                        break;
                    case "{":
                        if (parenDepth > 0) valueNesting++;
                        else
                        {
                            stack.Push(pending ?? (stack.Count == 0 ? schema.QueryTypeName : null));
                            pending = null;
                        }
                        break;
                    case "}":
                        if (parenDepth > 0) valueNesting = Math.Max(0, valueNesting - 1);
                        else if (stack.Count > 0) stack.Pop();
                        break;
                }
                continue;
            }

            if (t.Kind != TokKind.Name) continue;

            // Variables ($name) and directives (@name) are never schema fields.
            if (prev.Kind == TokKind.Punct && prev.Text is "$" or "@")
            {
                if (isTarget) return DocTarget.None;
                continue;
            }

            if (parenDepth > 0)
            {
                if (varDefs)
                {
                    if (isTarget) return TypeOrNone(schema, t.Text);
                    continue;
                }
                if (valueNesting == 0 && next.Text == ":")
                {
                    if (isTarget && argField != null) return new DocTarget(DocTargetKind.Argument, argParent, argField.Name, t.Text);
                    if (isTarget) return DocTarget.None;
                    continue;
                }
                if (isTarget) return TypeOrNone(schema, t.Text);
                continue;
            }

            if (stack.Count == 0)
            {
                switch (t.Text)
                {
                    case "query":
                        pending = schema.QueryTypeName;
                        if (isTarget) return TypeOrNone(schema, pending);
                        continue;
                    case "mutation":
                        pending = schema.MutationTypeName;
                        if (isTarget) return TypeOrNone(schema, pending);
                        continue;
                    case "subscription":
                        pending = schema.SubscriptionTypeName;
                        if (isTarget) return TypeOrNone(schema, pending);
                        continue;
                }
                if (prev.Text == "on" && prev.Kind == TokKind.Name)
                {
                    pending = t.Text;
                    if (isTarget) return TypeOrNone(schema, t.Text);
                    continue;
                }
                if (isTarget) return TypeOrNone(schema, t.Text);
                continue;
            }

            // Inside a selection set.
            var parent = stack.Peek();
            if (t.Text == "on" && prev.Kind == TokKind.Spread) continue;
            if (prev.Text == "on" && i >= 2 && toks[i - 2].Kind == TokKind.Spread)
            {
                pending = t.Text;
                if (isTarget) return TypeOrNone(schema, t.Text);
                continue;
            }
            if (prev.Kind == TokKind.Spread)
            {
                if (isTarget) return DocTarget.None;
                continue;
            }
            if (next.Text == ":")
            {
                // Alias: resolve docs for the aliased field that follows the colon.
                if (isTarget && i + 2 < toks.Count && toks[i + 2].Kind == TokKind.Name)
                    return FieldOrNone(schema, parent, toks[i + 2].Text);
                continue;
            }

            var field = schema.GetField(parent, t.Text);
            pending = field?.Type.NamedType;
            argField = field;
            argParent = parent;
            if (isTarget) return field != null ? new DocTarget(DocTargetKind.Field, parent, t.Text) : TypeOrNone(schema, t.Text);
        }

        if (stack.Count > 0 && stack.Peek() is { } current && schema.GetType(current) != null)
            return new DocTarget(DocTargetKind.Type, current);
        return DocTarget.None;
    }

    private static DocTarget TypeOrNone(GqlSchema schema, string? name) =>
        schema.GetType(name) != null ? new DocTarget(DocTargetKind.Type, name) : DocTarget.None;

    private static DocTarget FieldOrNone(GqlSchema schema, string? parent, string name) =>
        schema.GetField(parent, name) != null ? new DocTarget(DocTargetKind.Field, parent, name) : DocTarget.None;

    internal static List<Tok> Lex(string s)
    {
        var toks = new List<Tok>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ',' || c == '\uFEFF') { i++; continue; }
            if (c == '#')
            {
                while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
                continue;
            }
            if (c == '"')
            {
                var start = i;
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
                toks.Add(new Tok(TokKind.Other, "", start, i));
                continue;
            }
            if (c == '.' && i + 2 < s.Length && s[i + 1] == '.' && s[i + 2] == '.')
            {
                toks.Add(new Tok(TokKind.Spread, "...", i, i + 3));
                i += 3;
                continue;
            }
            if (c == '_' || char.IsAsciiLetter(c))
            {
                var start = i;
                while (i < s.Length && (s[i] == '_' || char.IsAsciiLetterOrDigit(s[i]))) i++;
                toks.Add(new Tok(TokKind.Name, s[start..i], start, i));
                continue;
            }
            if (c == '-' || char.IsAsciiDigit(c))
            {
                var start = i;
                i++;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '.' or '+' or '-')) i++;
                toks.Add(new Tok(TokKind.Other, s[start..i], start, i));
                continue;
            }
            toks.Add(new Tok(TokKind.Punct, c.ToString(), i, i + 1));
            i++;
        }
        return toks;
    }
}
