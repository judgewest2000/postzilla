using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Postzilla.GraphQL;

public sealed record VariableTransformResult(string Query, string Variables, int Changed, int Skipped, string Message);

/// <summary>
/// Moves values between a GraphQL document and its variables JSON:
/// <see cref="Inline"/> replaces <c>$var</c> usages with literal values, <see cref="Extract"/> turns literal arguments into variables.
/// The schema (optional) supplies types: enum values are written bare and new variables get the argument's declared type.
/// </summary>
public static class GraphQLVariableTransformer
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        IndentSize = 2,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly HashSet<string> BuiltInScalars = ["String", "ID", "Int", "Float", "Boolean"];

    public static VariableTransformResult Inline(string query, string variablesJson, GqlSchema? schema)
    {
        var variables = ParseVariables(variablesJson);
        var doc = new Parser(query ?? "", schema).Parse();
        var types = doc.Operations.SelectMany(o => o.VarDefs).GroupBy(d => d.Name).ToDictionary(g => g.Key, g => g.First().Type);

        var inlined = variables.Select(kv => kv.Key)
            .Where(name => types.ContainsKey(name) || doc.Usages.Any(u => u.Name == name))
            .ToHashSet();
        if (inlined.Count == 0)
            return new(query ?? "", variablesJson ?? "", 0, 0, "No variables from the JSON are used in the query.");

        var edits = new List<Edit>();
        foreach (var usage in doc.Usages.Where(u => inlined.Contains(u.Name)))
            edits.Add(new Edit(usage.Start, usage.End, ToLiteral(variables[usage.Name], types.GetValueOrDefault(usage.Name), schema)));

        foreach (var op in doc.Operations.Where(o => o.ParenOpen >= 0 && o.VarDefs.Any(d => inlined.Contains(d.Name))))
        {
            var remaining = op.VarDefs.Where(d => !inlined.Contains(d.Name)).Select(d => d.Raw).ToList();
            edits.Add(remaining.Count == 0
                ? new Edit(op.BeforeParen, op.ParenClose + 1, "")
                : new Edit(op.ParenOpen, op.ParenClose + 1, "(" + string.Join(", ", remaining) + ")"));
        }

        foreach (var name in inlined) variables.Remove(name);
        var plural = inlined.Count == 1 ? "" : "s";
        return new(Apply(query ?? "", edits), WriteVariables(variables), inlined.Count, 0,
            $"Inlined {inlined.Count} variable{plural} into the query: {string.Join(", ", inlined.Select(n => "$" + n))}.");
    }

    public static VariableTransformResult Extract(string query, string variablesJson, GqlSchema? schema)
    {
        var variables = ParseVariables(variablesJson);
        var doc = new Parser(query ?? "", schema).Parse();
        var taken = new HashSet<string>(variables.Select(kv => kv.Key).Concat(doc.Operations.SelectMany(o => o.VarDefs).Select(d => d.Name)));

        var edits = new List<Edit>();
        var added = new List<string>();
        var skipped = new List<string>();
        foreach (var op in doc.Operations)
        {
            var newDefs = new List<string>();
            foreach (var arg in op.Literals)
            {
                var type = arg.Type ?? (arg.IsEnum ? null : InferType(arg.Value));
                if (type == null)
                {
                    skipped.Add(arg.Name);
                    continue;
                }
                var name = arg.Name;
                for (var n = 2; taken.Contains(name); n++) name = arg.Name + n;
                taken.Add(name);
                variables[name] = arg.Value?.DeepClone();
                newDefs.Add($"${name}: {type}");
                added.Add(name);
                edits.Add(new Edit(arg.Start, arg.End, "$" + name));
            }
            if (newDefs.Count == 0) continue;

            var defs = string.Join(", ", newDefs);
            if (op.ParenOpen >= 0)
                edits.Add(new Edit(op.ParenClose, op.ParenClose, op.VarDefs.Count > 0 ? ", " + defs : defs));
            else if (op.HeaderEnd >= 0)
                edits.Add(new Edit(op.HeaderEnd, op.HeaderEnd, (op.IsNamed ? "(" : " (") + defs + ")"));
            else
                edits.Add(new Edit(op.SelectionStart, op.SelectionStart, "query (" + defs + ") "));
        }

        var message = added.Count == 0
            ? "No literal arguments to extract."
            : $"Extracted {added.Count} argument{(added.Count == 1 ? "" : "s")} into variables: {string.Join(", ", added.Select(n => "$" + n))}.";
        if (skipped.Count > 0)
            message += $" Skipped {string.Join(", ", skipped.Distinct())}: type unknown without the schema.";
        return new(Apply(query ?? "", edits), WriteVariables(variables), added.Count, skipped.Count, message);
    }

    // ------------------------------------------------------------------ JSON <-> GraphQL values

    private static JsonObject ParseVariables(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return new JsonObject();
        try
        {
            return JsonNode.Parse(json, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new FormatException("The variables must be a JSON object.");
        }
        catch (JsonException ex)
        {
            throw new FormatException("The variables are not valid JSON: " + ex.Message, ex);
        }
    }

    private static string WriteVariables(JsonObject variables) =>
        variables.Count == 0 ? "" : variables.ToJsonString(JsonOptions);

    private static string ToLiteral(JsonNode? node, string? type, GqlSchema? schema)
    {
        switch (node)
        {
            case null:
                return "null";
            case JsonArray array:
                var element = ElementType(type);
                return "[" + string.Join(", ", array.Select(item => ToLiteral(item, element, schema))) + "]";
            case JsonObject obj:
                var input = schema?.GetType(NamedType(type));
                return "{" + string.Join(", ", obj.Select(kv =>
                    $"{kv.Key}: {ToLiteral(kv.Value, input?.InputFields.FirstOrDefault(f => f.Name == kv.Key)?.Type.ToString(), schema)}")) + "}";
            case JsonValue value when value.GetValueKind() == JsonValueKind.String:
                var text = value.GetValue<string>();
                return IsEnum(type, schema) && IsName(text) ? text : JsonSerializer.Serialize(text, JsonOptions);
            default:
                return node.ToJsonString();
        }
    }

    private static bool IsEnum(string? type, GqlSchema? schema) => schema?.GetType(NamedType(type))?.Kind == "ENUM";

    private static bool IsName(string s) =>
        s.Length > 0 && (s[0] == '_' || char.IsAsciiLetter(s[0])) && s.All(c => c == '_' || char.IsAsciiLetterOrDigit(c))
        && s is not ("true" or "false" or "null");

    private static string? NamedType(string? type) => type?.Trim('[', ']', '!');

    private static string? ElementType(string? type)
    {
        var t = type?.TrimEnd('!');
        return t != null && t.StartsWith('[') && t.EndsWith(']') ? t[1..^1] : null;
    }

    /// <summary>Type for a scalar literal when there is no schema; objects, lists and null can't be typed.</summary>
    private static string? InferType(JsonNode? value) => value is JsonValue v ? v.GetValueKind() switch
    {
        JsonValueKind.String => "String",
        JsonValueKind.Number => v.ToJsonString().IndexOfAny(['.', 'e', 'E']) >= 0 ? "Float" : "Int",
        JsonValueKind.True or JsonValueKind.False => "Boolean",
        _ => null,
    } : null;


    // ------------------------------------------------------------------ edits

    private readonly record struct Edit(int Start, int End, string Text);

    private static string Apply(string text, List<Edit> edits)
    {
        var sb = new StringBuilder(text);
        foreach (var e in edits.OrderByDescending(e => e.Start).ThenByDescending(e => e.End))
        {
            sb.Remove(e.Start, e.End - e.Start);
            sb.Insert(e.Start, e.Text);
        }
        return sb.ToString();
    }

    // ------------------------------------------------------------------ parser

    private enum TK { Name, Number, String, BlockString, Punct, Spread, End }

    private readonly record struct Tok(TK Kind, string Text, int Start, int End);

    private sealed record VarDef(string Name, string Type, string Raw);

    private sealed record VarUsage(string Name, int Start, int End);

    private sealed record LiteralArg(string Name, string? Type, JsonNode? Value, bool IsEnum, int Start, int End);

    private readonly record struct Value(bool Parsed, JsonNode? Json, bool IsEnum = false);

    private sealed class Operation
    {
        public bool IsNamed;
        public int HeaderEnd = -1;      // end of "query" / operation name, where "(...)" can be inserted
        public int BeforeParen = -1;    // end of the token before "(", so removal leaves no double space
        public int ParenOpen = -1;
        public int ParenClose = -1;
        public int SelectionStart = -1;
        public List<VarDef> VarDefs { get; } = [];
        public List<LiteralArg> Literals { get; } = [];
    }

    private sealed class ParsedDocument
    {
        public List<Operation> Operations { get; } = [];
        public List<VarUsage> Usages { get; } = [];
    }

    /// <summary>Error-tolerant recursive-descent parser that records just what the transforms need.</summary>
    private sealed class Parser(string text, GqlSchema? schema)
    {
        private readonly List<Tok> _toks = Lex(text);
        private readonly ParsedDocument _doc = new();
        private int _i;
        private Operation? _op; // null inside fragment definitions

        private Tok Cur => _toks[_i];
        private Tok Next => _toks[Math.Min(_i + 1, _toks.Count - 1)];
        private bool At(string punct) => Cur.Kind == TK.Punct && Cur.Text == punct;
        private Tok Take() => _toks[_i < _toks.Count - 1 ? _i++ : _i];

        public ParsedDocument Parse()
        {
            while (Cur.Kind != TK.End)
            {
                var start = _i;
                if (Cur.Kind == TK.Name && Cur.Text is "query" or "mutation" or "subscription") ParseOperation();
                else if (Cur.Kind == TK.Name && Cur.Text == "fragment") ParseFragment();
                else if (At("{"))
                {
                    _op = new Operation { SelectionStart = Cur.Start };
                    _doc.Operations.Add(_op);
                    ParseSelectionSet(schema?.QueryTypeName);
                }
                if (_i == start) Take();
            }
            return _doc;
        }

        private void ParseOperation()
        {
            var kind = Take();
            _op = new Operation { HeaderEnd = kind.End };
            _doc.Operations.Add(_op);
            var rootType = kind.Text switch
            {
                "mutation" => schema?.MutationTypeName,
                "subscription" => schema?.SubscriptionTypeName,
                _ => schema?.QueryTypeName,
            };
            if (Cur.Kind == TK.Name)
            {
                _op.IsNamed = true;
                _op.HeaderEnd = Take().End;
            }
            if (At("("))
            {
                _op.BeforeParen = _toks[_i - 1].End;
                _op.ParenOpen = Take().Start;
                ParseVariableDefinitions();
                if (At(")")) _op.ParenClose = Take().Start;
                else _op.ParenOpen = -1;
            }
            ParseDirectives();
            if (At("{"))
            {
                _op.SelectionStart = Cur.Start;
                ParseSelectionSet(rootType);
            }
        }

        private void ParseFragment()
        {
            Take();
            _op = null;
            if (Cur.Kind == TK.Name) Take();
            string? type = null;
            if (Cur.Kind == TK.Name && Cur.Text == "on")
            {
                Take();
                if (Cur.Kind == TK.Name) type = Take().Text;
            }
            ParseDirectives();
            if (At("{")) ParseSelectionSet(type);
        }

        private void ParseVariableDefinitions()
        {
            while (Cur.Kind != TK.End && !At(")"))
            {
                var start = _i;
                if (At("$") && Next.Kind == TK.Name)
                {
                    var defStart = Take().Start;
                    var name = Take().Text;
                    var type = "";
                    if (At(":"))
                    {
                        Take();
                        type = ParseType();
                    }
                    if (At("="))
                    {
                        Take();
                        ParseValue(collectUsages: false);
                    }
                    ParseDirectives();
                    _op!.VarDefs.Add(new VarDef(name, type, text[defStart.._toks[_i - 1].End]));
                }
                if (_i == start) Take();
            }
        }

        private string ParseType()
        {
            string type;
            if (At("["))
            {
                Take();
                type = "[" + ParseType() + "]";
                if (At("]")) Take();
            }
            else type = Cur.Kind == TK.Name ? Take().Text : "";
            if (At("!"))
            {
                Take();
                type += "!";
            }
            return type;
        }

        private void ParseSelectionSet(string? typeName)
        {
            Take(); // {
            while (Cur.Kind != TK.End && !At("}"))
            {
                var start = _i;
                if (Cur.Kind == TK.Spread)
                {
                    Take();
                    if (Cur.Kind == TK.Name && Cur.Text == "on")
                    {
                        Take();
                        var cond = Cur.Kind == TK.Name ? Take().Text : typeName;
                        ParseDirectives();
                        if (At("{")) ParseSelectionSet(cond);
                    }
                    else
                    {
                        if (Cur.Kind == TK.Name) Take();
                        ParseDirectives();
                        if (At("{")) ParseSelectionSet(typeName);
                    }
                }
                else if (Cur.Kind == TK.Name)
                {
                    var name = Take().Text;
                    if (At(":") && Next.Kind == TK.Name)
                    {
                        Take();
                        name = Take().Text;
                    }
                    var field = schema?.GetField(typeName, name);
                    if (At("(")) ParseArguments(field?.Args, collectLiterals: true);
                    ParseDirectives();
                    if (At("{")) ParseSelectionSet(field?.Type.NamedType);
                }
                if (_i == start) Take();
            }
            if (At("}")) Take();
        }

        private void ParseDirectives()
        {
            while (At("@"))
            {
                Take();
                if (Cur.Kind == TK.Name) Take();
                if (At("(")) ParseArguments(null, collectLiterals: false);
            }
        }

        private void ParseArguments(List<GqlInputValue>? defs, bool collectLiterals)
        {
            Take(); // (
            while (Cur.Kind != TK.End && !At(")"))
            {
                var start = _i;
                if (Cur.Kind == TK.Name && Next.Kind == TK.Punct && Next.Text == ":")
                {
                    var name = Take().Text;
                    Take();
                    var valueStart = Cur.Start;
                    var usagesBefore = _doc.Usages.Count;
                    var value = ParseValue(collectUsages: true);
                    var hasVariable = _doc.Usages.Count > usagesBefore;
                    if (collectLiterals && _op != null && !hasVariable && value.Parsed)
                    {
                        var type = defs?.FirstOrDefault(d => d.Name == name)?.Type.ToString();
                        _op.Literals.Add(new LiteralArg(name, type, value.Json, value.IsEnum, valueStart, _toks[_i - 1].End));
                    }
                }
                if (_i == start) Take();
            }
            if (At(")")) Take();
        }

        private Value ParseValue(bool collectUsages)
        {
            var t = Cur;
            switch (t.Kind)
            {
                case TK.Punct when t.Text == "$" && Next.Kind == TK.Name:
                    Take();
                    var name = Take();
                    if (collectUsages) _doc.Usages.Add(new VarUsage(name.Text, t.Start, name.End));
                    return new Value(true, null);
                case TK.String:
                    Take();
                    return new Value(true, JsonValue.Create(UnescapeString(t.Text)));
                case TK.BlockString:
                    Take();
                    return new Value(true, JsonValue.Create(BlockStringValue(t.Text)));
                case TK.Number:
                    Take();
                    try
                    {
                        return new Value(true, JsonNode.Parse(t.Text));
                    }
                    catch (JsonException)
                    {
                        return new Value(false, null);
                    }
                case TK.Name:
                    Take();
                    return t.Text switch
                    {
                        "true" => new Value(true, JsonValue.Create(true)),
                        "false" => new Value(true, JsonValue.Create(false)),
                        "null" => new Value(true, null),
                        _ => new Value(true, JsonValue.Create(t.Text), IsEnum: true), // enum values are strings in JSON
                    };
                case TK.Punct when t.Text == "[":
                {
                    Take();
                    var array = new JsonArray();
                    var ok = true;
                    while (Cur.Kind != TK.End && !At("]"))
                    {
                        var start = _i;
                        var item = ParseValue(collectUsages);
                        ok &= item.Parsed;
                        array.Add(item.Json);
                        if (_i == start) Take();
                    }
                    if (At("]")) Take();
                    return new Value(ok, array);
                }
                case TK.Punct when t.Text == "{":
                {
                    Take();
                    var obj = new JsonObject();
                    var ok = true;
                    while (Cur.Kind != TK.End && !At("}"))
                    {
                        var start = _i;
                        if (Cur.Kind == TK.Name && Next.Kind == TK.Punct && Next.Text == ":")
                        {
                            var key = Take().Text;
                            Take();
                            var item = ParseValue(collectUsages);
                            ok &= item.Parsed;
                            obj[key] = item.Json;
                        }
                        if (_i == start)
                        {
                            ok = false;
                            Take();
                        }
                    }
                    if (At("}")) Take();
                    return new Value(ok, obj);
                }
                default:
                    return new Value(false, null);
            }
        }
    }


    private static string UnescapeString(string raw)
    {
        try
        {
            return JsonSerializer.Deserialize<string>(raw) ?? "";
        }
        catch (JsonException)
        {
            return raw.Trim('"');
        }
    }

    /// <summary>Block string value per the GraphQL spec: common indentation and blank first/last lines removed.</summary>
    private static string BlockStringValue(string raw)
    {
        var lines = raw[3..^3].Replace("\\\"\"\"", "\"\"\"").Replace("\r\n", "\n").Split('\n').ToList();
        var indent = lines.Skip(1).Where(l => l.Trim().Length > 0).Select(l => l.Length - l.TrimStart(' ', '\t').Length).DefaultIfEmpty(0).Min();
        for (var i = 1; i < lines.Count; i++) lines[i] = lines[i].Length >= indent ? lines[i][indent..] : lines[i].TrimStart();
        while (lines.Count > 0 && lines[0].Trim().Length == 0) lines.RemoveAt(0);
        while (lines.Count > 0 && lines[^1].Trim().Length == 0) lines.RemoveAt(lines.Count - 1);
        return string.Join("\n", lines);
    }

    private static List<Tok> Lex(string s)
    {
        var toks = new List<Tok>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c) || c == ',' || c == '\uFEFF') { i++; continue; }
            var start = i;
            if (c == '#')
            {
                while (i < s.Length && s[i] != '\n' && s[i] != '\r') i++;
                continue;
            }
            if (c == '"')
            {
                if (i + 2 < s.Length && s[i + 1] == '"' && s[i + 2] == '"')
                {
                    var end = i + 3;
                    while (end < s.Length && !(s[end] == '"' && end + 2 < s.Length && s[end + 1] == '"' && s[end + 2] == '"' && s[end - 1] != '\\')) end++;
                    i = Math.Min(s.Length, end + 3);
                    toks.Add(new Tok(TK.BlockString, s[start..i], start, i));
                    continue;
                }
                i++;
                while (i < s.Length && s[i] != '"' && s[i] != '\n' && s[i] != '\r')
                {
                    if (s[i] == '\\') i++;
                    i++;
                }
                i = Math.Min(s.Length, i + 1);
                toks.Add(new Tok(TK.String, s[start..i], start, i));
                continue;
            }
            if (c == '.' && i + 2 < s.Length && s[i + 1] == '.' && s[i + 2] == '.')
            {
                toks.Add(new Tok(TK.Spread, "...", i, i + 3));
                i += 3;
                continue;
            }
            if (c == '_' || char.IsAsciiLetter(c))
            {
                while (i < s.Length && (s[i] == '_' || char.IsAsciiLetterOrDigit(s[i]))) i++;
                toks.Add(new Tok(TK.Name, s[start..i], start, i));
                continue;
            }
            if (c == '-' || char.IsAsciiDigit(c))
            {
                i++;
                while (i < s.Length && (char.IsAsciiLetterOrDigit(s[i]) || s[i] is '.' or '+' or '-')) i++;
                toks.Add(new Tok(TK.Number, s[start..i], start, i));
                continue;
            }
            toks.Add(new Tok(TK.Punct, c.ToString(), i, i + 1));
            i++;
        }
        toks.Add(new Tok(TK.End, "", s.Length, s.Length));
        return toks;
    }
}
