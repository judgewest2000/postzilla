namespace Postzilla.GraphQL;

public enum GraphQLTokenKind { Default, Comment, String, Number, Keyword, Constant, Variable, Directive, Type, Field, Punctuation, OperationName }

public readonly record struct GraphQLToken(int Start, int Length, GraphQLTokenKind Kind);

/// <summary>Lightweight, error-tolerant GraphQL tokenizer used for syntax highlighting.</summary>
public static class GraphQLHighlighter
{
    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "query", "mutation", "subscription", "fragment", "on",
        "type", "input", "enum", "interface", "union", "scalar", "schema", "extend", "directive", "implements", "repeatable",
    };

    private static readonly HashSet<string> Constants = new(StringComparer.Ordinal) { "true", "false", "null" };

    private static readonly HashSet<string> TypeIntroducers = new(StringComparer.Ordinal)
    {
        "on", "type", "input", "enum", "interface", "union", "scalar", "implements",
    };

    public static List<GraphQLToken> Tokenize(string text)
    {
        var tokens = new List<GraphQLToken>();
        var braceDepth = 0;
        var parenDepth = 0;
        var previous = "";   // previous significant token text
        var previousKind = GraphQLTokenKind.Default;
        var i = 0;

        while (i < text.Length)
        {
            var c = text[i];
            var start = i;

            if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }

            if (c == '#')
            {
                while (i < text.Length && text[i] is not ('\n' or '\r')) i++;
                tokens.Add(new(start, i - start, GraphQLTokenKind.Comment));
                continue;
            }

            if (c == '"')
            {
                if (string.CompareOrdinal(text, i, "\"\"\"", 0, 3) == 0)
                {
                    var end = text.IndexOf("\"\"\"", i + 3, StringComparison.Ordinal);
                    i = end < 0 ? text.Length : end + 3;
                }
                else
                {
                    i++;
                    while (i < text.Length && text[i] is not ('"' or '\n' or '\r')) i += text[i] == '\\' ? 2 : 1;
                    i = Math.Min(text.Length, i < text.Length && text[i] == '"' ? i + 1 : i);
                }
                tokens.Add(new(start, i - start, GraphQLTokenKind.String));
                previous = "\""; previousKind = GraphQLTokenKind.String;
                continue;
            }

            if (char.IsDigit(c) || (c == '-' && i + 1 < text.Length && char.IsDigit(text[i + 1])))
            {
                i++;
                while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] is '.' or '+' or '-')) i++;
                tokens.Add(new(start, i - start, GraphQLTokenKind.Number));
                previous = "0"; previousKind = GraphQLTokenKind.Number;
                continue;
            }

            if (c is '$' or '@' || IsNameStart(c))
            {
                i++;
                while (i < text.Length && IsNamePart(text[i])) i++;
                var word = text[start..i];
                var kind = c switch
                {
                    '$' => GraphQLTokenKind.Variable,
                    '@' => GraphQLTokenKind.Directive,
                    _ => ClassifyName(word, previous, previousKind == GraphQLTokenKind.Keyword, braceDepth, parenDepth, NextSignificant(text, i)),
                };
                tokens.Add(new(start, i - start, kind));
                previous = word;
                previousKind = kind;
                continue;
            }

            if (c == '.' && string.CompareOrdinal(text, i, "...", 0, 3) == 0) i += 3;
            else i++;

            switch (c)
            {
                case '{': braceDepth++; break;
                case '}': braceDepth = Math.Max(0, braceDepth - 1); break;
                case '(': parenDepth++; break;
                case ')': parenDepth = Math.Max(0, parenDepth - 1); break;
            }
            tokens.Add(new(start, i - start, GraphQLTokenKind.Punctuation));
            previous = text[start..i]; previousKind = GraphQLTokenKind.Punctuation;
        }
        return tokens;
    }

    private static GraphQLTokenKind ClassifyName(string word, string previous, bool previousIsKeyword, int braceDepth, int parenDepth, char next)
    {
        if (Constants.Contains(word)) return GraphQLTokenKind.Constant;
        if (Keywords.Contains(word) && next != ':' && !(braceDepth > 0 && word != "on")) return GraphQLTokenKind.Keyword;
        if (previousIsKeyword && TypeIntroducers.Contains(previous)) return GraphQLTokenKind.Type;
        if (previousIsKeyword && previous is "query" or "mutation" or "subscription" or "fragment") return GraphQLTokenKind.OperationName;
        // Variable definitions: "($id: ID!, $ids: [ID!])" before the operation's selection set.
        if (braceDepth == 0 && parenDepth > 0 && previous is ":" or "[") return GraphQLTokenKind.Type;
        if (braceDepth > 0 && (parenDepth == 0 || next == ':')) return GraphQLTokenKind.Field;
        return GraphQLTokenKind.Default;
    }

    private static char NextSignificant(string text, int i)
    {
        while (i < text.Length && (char.IsWhiteSpace(text[i]) || text[i] == ',')) i++;
        return i < text.Length ? text[i] : '\0';
    }

    private static bool IsNameStart(char c) => c == '_' || char.IsAsciiLetter(c);

    private static bool IsNamePart(char c) => c == '_' || char.IsAsciiLetterOrDigit(c);
}
