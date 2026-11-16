using System.Text.RegularExpressions;

namespace Brunozilla.Services;

/// <summary>Substitutes {{name}} placeholders using collection/environment variables.</summary>
public static partial class VariableResolver
{
    [GeneratedRegex(@"\{\{\s*([^{}\s]+)\s*\}\}")]
    private static partial Regex Placeholder();

    public static string Resolve(string? input, IReadOnlyDictionary<string, string> variables)
    {
        if (string.IsNullOrEmpty(input)) return "";
        var current = input;
        // Allow variables that reference other variables, with a bound to stop cycles.
        for (var pass = 0; pass < 5; pass++)
        {
            var next = Placeholder().Replace(current, m =>
            {
                var name = m.Groups[1].Value;
                if (name.StartsWith('$')) return Dynamic(name) ?? m.Value;
                return variables.TryGetValue(name, out var value) ? value : m.Value;
            });
            if (next == current) break;
            current = next;
        }
        return current;
    }

    private static string? Dynamic(string name) => name switch
    {
        "$guid" or "$uuid" or "$randomUUID" => Guid.NewGuid().ToString(),
        "$timestamp" => DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString(),
        "$isoTimestamp" => DateTimeOffset.UtcNow.ToString("o"),
        "$randomInt" => Random.Shared.Next(0, 1001).ToString(),
        _ => null,
    };
}
