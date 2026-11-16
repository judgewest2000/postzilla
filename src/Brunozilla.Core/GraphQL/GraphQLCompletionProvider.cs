using static Brunozilla.GraphQL.GraphQLContextResolver;

namespace Brunozilla.GraphQL;

public enum CompletionKind { Keyword, Field, Argument, InputField, Type, EnumValue, Variable, Fragment, Directive }

public sealed record CompletionItem(
    string Label,
    CompletionKind Kind,
    string InsertText,
    string? Detail = null,
    string? Description = null,
    bool IsDeprecated = false,
    DocTarget? Target = null);

/// <summary>Suggestions for the caret position; <see cref="Prefix"/> is the text that an accepted item replaces.</summary>
public sealed record CompletionResult(int ReplaceStart, string Prefix, IReadOnlyList<CompletionItem> Items)
{
    public static readonly CompletionResult Empty = new(0, "", Array.Empty<CompletionItem>());
}

/// <summary>
/// Schema-aware autocompletion for GraphQL documents. Like <see cref="GraphQLContextResolver"/>
/// it walks tokens tolerantly so it works on incomplete queries.
/// </summary>
public static class GraphQLCompletionProvider
{
    private static readonly GqlTypeRef NonNullBoolean = new() { Kind = "NON_NULL", OfType = new GqlTypeRef { Kind = "SCALAR", Name = "Boolean" } };

    private static readonly GqlField DirectiveIf = new()
    {
        Name = "@directive",
        Args = { new GqlInputValue { Name = "if", Type = NonNullBoolean, Description = "Condition for including or skipping the field." } },
    };

    private static readonly (string Name, string Description)[] Directives =
    {
        ("include", "Include this field or fragment only when the `if` argument is true."),
        ("skip", "Skip this field or fragment when the `if` argument is true."),
    };

    private sealed class Container
    {
        public bool IsList;
        public GqlTypeRef? Type;
        public IReadOnlyList<GqlInputValue>? Keys;
        public string? FieldParent;
        public string? FieldName;
        public GqlTypeRef? KeyType;
        public readonly HashSet<string> Used = new(StringComparer.Ordinal);
    }

    public static CompletionResult GetCompletions(GqlSchema schema, string text, int caret)
    {
        text ??= "";
        caret = Math.Clamp(caret, 0, text.Length);
        if (InStringOrComment(text, caret)) return CompletionResult.Empty;

        var toks = Lex(text);
        var replaceStart = caret;
        var prefix = "";
        var limit = toks.Count;
        var prefixIndex = -1;
        for (var i = 0; i < toks.Count; i++)
        {
            var t = toks[i];
            if (t.Start >= caret) { limit = i; break; }
            if (t.End < caret) continue;
            if (t.Kind == TokKind.Name)
            {
                prefix = text[t.Start..caret];
                replaceStart = t.Start;
                prefixIndex = limit = i;
            }
            else if (t.Kind == TokKind.Other) return CompletionResult.Empty;
            else limit = i + 1;
            break;
        }

        var followingIndex = prefixIndex >= 0 ? prefixIndex + 1 : limit;
        var following = followingIndex < toks.Count ? toks[followingIndex] : default;

        var (variables, fragments) = CollectDefinitions(toks, prefixIndex);

        var sel = new Stack<string?>();
        string? pending = null;
        GqlField? lastField = null;
        string? lastFieldParent = null;
        var paren = 0;
        var varDefs = false;
        var args = new List<Container>();

        for (var i = 0; i < limit; i++)
        {
            var t = toks[i];
            var prev = i > 0 ? toks[i - 1] : default;
            var next = i + 1 < toks.Count ? toks[i + 1] : default;

            if (paren > 0)
            {
                if (t.Kind == TokKind.Punct)
                {
                    switch (t.Text)
                    {
                        case "(":
                            paren++;
                            break;
                        case ")":
                            paren--;
                            if (paren == 0)
                            {
                                args.Clear();
                                varDefs = false;
                            }
                            break;
                        case "{":
                        case "[":
                            if (!varDefs && args.Count > 0)
                            {
                                var vt = ValueType(args[^1], prev);
                                args.Add(t.Text == "{"
                                    ? new Container { Type = vt, Keys = schema.GetType(vt?.NamedType)?.InputFields }
                                    : new Container { IsList = true, Type = vt });
                            }
                            break;
                        case "}":
                        case "]":
                            if (!varDefs && args.Count > 1) args.RemoveAt(args.Count - 1);
                            break;
                    }
                }
                else if (t.Kind == TokKind.Name && !varDefs && args.Count > 0 && next.Text == ":" && prev.Text != "$")
                {
                    var top = args[^1];
                    top.Used.Add(t.Text);
                    top.KeyType = top.Keys?.FirstOrDefault(k => k.Name == t.Text)?.Type;
                }
                continue;
            }

            if (t.Kind == TokKind.Punct)
            {
                switch (t.Text)
                {
                    case "(":
                        paren = 1;
                        varDefs = false;
                        if (i >= 2 && toks[i - 2].Text == "@")
                            args.Add(new Container { Keys = DirectiveIf.Args });
                        else if (sel.Count == 0)
                            varDefs = true;
                        else
                            args.Add(new Container { Keys = lastField?.Args, FieldParent = lastFieldParent, FieldName = lastField?.Name });
                        break;
                    case "{":
                        sel.Push(pending ?? (sel.Count == 0 ? schema.QueryTypeName : null));
                        pending = null;
                        break;
                    case "}":
                        if (sel.Count > 0) sel.Pop();
                        break;
                }
                continue;
            }

            if (t.Kind != TokKind.Name) continue;
            if (prev.Kind == TokKind.Punct && prev.Text is "$" or "@") continue;

            if (sel.Count == 0)
            {
                switch (t.Text)
                {
                    case "query": pending = schema.QueryTypeName; continue;
                    case "mutation": pending = schema.MutationTypeName; continue;
                    case "subscription": pending = schema.SubscriptionTypeName; continue;
                }
                if (prev.Kind == TokKind.Name && prev.Text == "on") pending = t.Text;
                continue;
            }

            if (prev.Kind == TokKind.Spread) continue;
            if (prev.Text == "on" && i >= 2 && toks[i - 2].Kind == TokKind.Spread)
            {
                pending = t.Text;
                continue;
            }
            if (next.Text == ":") continue; // alias

            lastFieldParent = sel.Peek();
            lastField = schema.GetField(lastFieldParent, t.Text);
            pending = lastField?.Type.NamedType;
        }

        var p = limit > 0 ? toks[limit - 1] : default;
        var pp = limit > 1 ? toks[limit - 2] : default;
        var items = new List<CompletionItem>();

        if (p.Kind == TokKind.Punct && p.Text == "@")
        {
            items.AddRange(Directives.Select(d => new CompletionItem(d.Name, CompletionKind.Directive, d.Name, "directive", d.Description)));
        }
        else if (paren > 0)
        {
            if (varDefs)
            {
                if (p.Text is ":" or "[") items.AddRange(InputTypes(schema));
            }
            else if (p.Text == "$")
            {
                items.AddRange(variables.Select(v => new CompletionItem(v, CompletionKind.Variable, v, "variable")));
            }
            else if (args.Count > 0)
            {
                var top = args[^1];
                if (p.Text == ":") items.AddRange(Values(schema, top.KeyType, variables));
                else if (top.IsList) items.AddRange(Values(schema, ElementType(top.Type), variables));
                else if (top.Keys != null)
                {
                    var colonFollows = following.Text == ":";
                    var isArgs = args.Count == 1;
                    foreach (var k in top.Keys.Where(k => !top.Used.Contains(k.Name)))
                    {
                        items.Add(new CompletionItem(k.Name,
                            isArgs ? CompletionKind.Argument : CompletionKind.InputField,
                            colonFollows ? k.Name : k.Name + ": ",
                            k.Type.ToString(),
                            k.Description,
                            Target: isArgs && top.FieldName != null
                                ? new DocTarget(DocTargetKind.Argument, top.FieldParent, top.FieldName, k.Name)
                                : TypeTarget(schema, k.Type.NamedType)));
                    }
                }
            }
        }
        else if (sel.Count > 0)
        {
            var parentName = sel.Peek();
            var parent = schema.GetType(parentName);
            if (p.Kind == TokKind.Spread)
            {
                items.Add(new CompletionItem("on", CompletionKind.Keyword, "on ", "inline fragment"));
                items.AddRange(fragments.Select(f => new CompletionItem(f, CompletionKind.Fragment, f, "fragment")));
            }
            else if (p.Kind == TokKind.Name && p.Text == "on" && pp.Kind == TokKind.Spread)
            {
                var names = parent is { Kind: "UNION" or "INTERFACE" } ? parent.PossibleTypes : CompositeTypes(schema).Select(t => t.Name);
                items.AddRange(names.Select(n => TypeItem(schema, n)));
            }
            else if (parent != null && (p.Kind != TokKind.Name || p.Text != "on"))
            {
                foreach (var f in parent.Fields)
                {
                    items.Add(new CompletionItem(f.Name, CompletionKind.Field, f.Name, f.Type.ToString(), f.Description,
                        f.IsDeprecated, new DocTarget(DocTargetKind.Field, parent.Name, f.Name)));
                }
                items.Add(new CompletionItem("__typename", CompletionKind.Field, "__typename", "String!",
                    "The name of the current object type at runtime."));
                if (parent.Kind is "UNION" or "INTERFACE")
                {
                    foreach (var possible in parent.PossibleTypes)
                    {
                        items.Add(new CompletionItem("... on " + possible, CompletionKind.Fragment, "... on " + possible + " ",
                            "inline fragment", schema.GetType(possible)?.Description, Target: TypeTarget(schema, possible)));
                    }
                }
            }
        }
        else
        {
            if (p.Kind == TokKind.Name && p.Text == "on")
                items.AddRange(CompositeTypes(schema).Select(t => TypeItem(schema, t.Name)));
            else if (p.Kind == TokKind.None || p.Text == "}")
                items.AddRange(Keywords(schema));
        }

        return new CompletionResult(replaceStart, prefix, Filter(items, prefix));
    }

    // ------------------------------------------------------------------ candidates

    private static IEnumerable<CompletionItem> Keywords(GqlSchema schema)
    {
        yield return new CompletionItem("query", CompletionKind.Keyword, "query ", schema.QueryTypeName);
        if (schema.MutationTypeName != null) yield return new CompletionItem("mutation", CompletionKind.Keyword, "mutation ", schema.MutationTypeName);
        if (schema.SubscriptionTypeName != null) yield return new CompletionItem("subscription", CompletionKind.Keyword, "subscription ", schema.SubscriptionTypeName);
        yield return new CompletionItem("fragment", CompletionKind.Keyword, "fragment ", "fragment definition");
    }

    private static IEnumerable<GqlType> CompositeTypes(GqlSchema schema) =>
        schema.Types.Values.Where(t => t.Kind is "OBJECT" or "INTERFACE" or "UNION" && !t.Name.StartsWith("__"));

    private static IEnumerable<CompletionItem> InputTypes(GqlSchema schema) =>
        schema.Types.Values
            .Where(t => t.Kind is "SCALAR" or "ENUM" or "INPUT_OBJECT" && !t.Name.StartsWith("__"))
            .Select(t => TypeItem(schema, t.Name));

    private static CompletionItem TypeItem(GqlSchema schema, string name)
    {
        var type = schema.GetType(name);
        return new CompletionItem(name, CompletionKind.Type, name, type?.Kind.ToLowerInvariant().Replace('_', ' '),
            type?.Description, Target: TypeTarget(schema, name));
    }

    private static DocTarget? TypeTarget(GqlSchema schema, string? name) =>
        schema.GetType(name) != null ? new DocTarget(DocTargetKind.Type, name) : null;

    private static IEnumerable<CompletionItem> Values(GqlSchema schema, GqlTypeRef? typeRef, IEnumerable<string> variables)
    {
        var type = Unwrap(typeRef);
        if (type != null && type.Kind != "LIST")
        {
            var named = schema.GetType(type.NamedType);
            if (named?.Kind == "ENUM")
            {
                foreach (var v in named.EnumValues)
                    yield return new CompletionItem(v.Name, CompletionKind.EnumValue, v.Name, named.Name, v.Description, v.IsDeprecated,
                        new DocTarget(DocTargetKind.Type, named.Name));
            }
            else if (type.NamedType == "Boolean")
            {
                yield return new CompletionItem("true", CompletionKind.Keyword, "true", "Boolean");
                yield return new CompletionItem("false", CompletionKind.Keyword, "false", "Boolean");
            }
        }
        foreach (var v in variables)
            yield return new CompletionItem("$" + v, CompletionKind.Variable, "$" + v, "variable");
    }

    private static GqlTypeRef? Unwrap(GqlTypeRef? t) => t?.Kind == "NON_NULL" ? t.OfType : t;

    private static GqlTypeRef? ElementType(GqlTypeRef? t)
    {
        var u = Unwrap(t);
        return u?.Kind == "LIST" ? u.OfType : u;
    }

    private static GqlTypeRef? ValueType(Container top, Tok prev) =>
        prev.Text == ":" ? top.KeyType : top.IsList ? ElementType(top.Type) : null;

    private static (List<string> Variables, List<string> Fragments) CollectDefinitions(List<Tok> toks, int skipIndex)
    {
        var variables = new List<string>();
        var fragments = new List<string>();
        for (var i = 0; i + 1 < toks.Count; i++)
        {
            if (i + 1 == skipIndex) continue;
            if (toks[i].Text == "$" && toks[i + 1].Kind == TokKind.Name && i + 2 < toks.Count && toks[i + 2].Text == ":")
                variables.Add(toks[i + 1].Text);
            else if (toks[i].Kind == TokKind.Name && toks[i].Text == "fragment" && toks[i + 1].Kind == TokKind.Name && toks[i + 1].Text != "on")
                fragments.Add(toks[i + 1].Text);
        }
        return (variables.Distinct().ToList(), fragments.Distinct().ToList());
    }

    // ------------------------------------------------------------------ filtering

    private static List<CompletionItem> Filter(List<CompletionItem> items, string prefix)
    {
        var scored = new List<(CompletionItem Item, int Rank)>();
        foreach (var item in items)
        {
            var rank = Rank(item.Label.TrimStart('$', '.', ' '), prefix);
            if (rank >= 0) scored.Add((item, rank));
        }
        return scored
            .OrderBy(s => s.Rank)
            .ThenBy(s => s.Item.IsDeprecated)
            .ThenBy(s => s.Item.Label.StartsWith("__", StringComparison.Ordinal))
            .ThenBy(s => s.Item.Label, StringComparer.OrdinalIgnoreCase)
            .Select(s => s.Item)
            .ToList();
    }

    /// <summary>0 = prefix match, 1 = substring, 2 = subsequence (e.g. "cnm" → countryName), -1 = no match.</summary>
    private static int Rank(string label, string prefix)
    {
        if (prefix.Length == 0 || label.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        if (label.Contains(prefix, StringComparison.OrdinalIgnoreCase)) return 1;
        var j = 0;
        foreach (var c in label)
            if (j < prefix.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(prefix[j])) j++;
        return j == prefix.Length ? 2 : -1;
    }

    private static bool InStringOrComment(string s, int caret)
    {
        var i = 0;
        while (i < caret)
        {
            var c = s[i];
            if (c == '#')
            {
                while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
                if (i >= caret) return true;
                continue;
            }
            if (c == '"')
            {
                if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
                {
                    var end = s.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    if (end < 0 || end + 3 > caret) return true;
                    i = end + 3;
                    continue;
                }
                i++;
                while (i < s.Length && s[i] != '"' && s[i] != '\n' && s[i] != '\r')
                {
                    if (s[i] == '\\') i++;
                    i++;
                }
                if (i >= caret) return true;
                i++;
                continue;
            }
            i++;
        }
        return false;
    }
}
