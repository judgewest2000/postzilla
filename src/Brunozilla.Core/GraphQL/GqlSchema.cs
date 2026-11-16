using System.Text.Json;
using Brunozilla.Models;
using Brunozilla.Services;

namespace Brunozilla.GraphQL;

public sealed class GqlTypeRef
{
    public string Kind { get; init; } = "";
    public string? Name { get; init; }
    public GqlTypeRef? OfType { get; init; }

    public string NamedType => Name ?? OfType?.NamedType ?? "";

    public override string ToString() => Kind switch
    {
        "NON_NULL" => $"{OfType}!",
        "LIST" => $"[{OfType}]",
        _ => Name ?? "",
    };
}

public sealed class GqlInputValue
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public GqlTypeRef Type { get; init; } = new();
    public string? DefaultValue { get; init; }
}

public sealed class GqlField
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<GqlInputValue> Args { get; init; } = new();
    public GqlTypeRef Type { get; init; } = new();
    public bool IsDeprecated { get; init; }
    public string? DeprecationReason { get; init; }
}

public sealed class GqlEnumValue
{
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public bool IsDeprecated { get; init; }
    public string? DeprecationReason { get; init; }
}

public sealed class GqlType
{
    public string Kind { get; init; } = "";
    public string Name { get; init; } = "";
    public string? Description { get; init; }
    public List<GqlField> Fields { get; init; } = new();
    public List<GqlInputValue> InputFields { get; init; } = new();
    public List<GqlEnumValue> EnumValues { get; init; } = new();
    public List<string> Interfaces { get; init; } = new();
    public List<string> PossibleTypes { get; init; } = new();

    public GqlField? GetField(string name) => Fields.FirstOrDefault(f => f.Name == name);
}

public sealed class GqlSchema
{
    private static readonly GqlField TypenameField = new()
    {
        Name = "__typename",
        Description = "The name of the current object type at runtime.",
        Type = new GqlTypeRef { Kind = "NON_NULL", OfType = new GqlTypeRef { Kind = "SCALAR", Name = "String" } },
    };

    public string? QueryTypeName { get; init; }
    public string? MutationTypeName { get; init; }
    public string? SubscriptionTypeName { get; init; }
    public Dictionary<string, GqlType> Types { get; init; } = new(StringComparer.Ordinal);

    public GqlType? GetType(string? name) => name != null && Types.TryGetValue(name, out var t) ? t : null;

    public GqlField? GetField(string? typeName, string fieldName) =>
        fieldName == "__typename" ? TypenameField : GetType(typeName)?.GetField(fieldName);

    public static GqlSchema FromIntrospectionResponse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Object
            && data.TryGetProperty("__schema", out var schema))
            return Parse(schema);
        if (root.TryGetProperty("__schema", out schema))
            return Parse(schema);

        if (root.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
        {
            var messages = errors.EnumerateArray()
                .Select(e => e.TryGetProperty("message", out var m) ? m.GetString() : e.GetRawText());
            throw new InvalidOperationException("Introspection returned errors: " + string.Join("; ", messages));
        }
        throw new InvalidOperationException("The response did not contain an introspection result (data.__schema).");
    }

    public static GqlSchema Parse(JsonElement schema)
    {
        var types = new Dictionary<string, GqlType>(StringComparer.Ordinal);
        if (schema.TryGetProperty("types", out var typesEl) && typesEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var t in typesEl.EnumerateArray())
            {
                var type = new GqlType
                {
                    Kind = Str(t, "kind") ?? "",
                    Name = Str(t, "name") ?? "",
                    Description = Str(t, "description"),
                    Fields = Arr(t, "fields").Select(ParseField).ToList(),
                    InputFields = Arr(t, "inputFields").Select(ParseInputValue).ToList(),
                    EnumValues = Arr(t, "enumValues").Select(e => new GqlEnumValue
                    {
                        Name = Str(e, "name") ?? "",
                        Description = Str(e, "description"),
                        IsDeprecated = Bool(e, "isDeprecated"),
                        DeprecationReason = Str(e, "deprecationReason"),
                    }).ToList(),
                    Interfaces = Arr(t, "interfaces").Select(i => ParseTypeRef(i).NamedType).ToList(),
                    PossibleTypes = Arr(t, "possibleTypes").Select(i => ParseTypeRef(i).NamedType).ToList(),
                };
                if (type.Name.Length > 0) types[type.Name] = type;
            }
        }

        return new GqlSchema
        {
            QueryTypeName = RootName(schema, "queryType"),
            MutationTypeName = RootName(schema, "mutationType"),
            SubscriptionTypeName = RootName(schema, "subscriptionType"),
            Types = types,
        };
    }

    private static string? RootName(JsonElement schema, string prop) =>
        schema.TryGetProperty(prop, out var el) && el.ValueKind == JsonValueKind.Object ? Str(el, "name") : null;

    private static GqlField ParseField(JsonElement f) => new()
    {
        Name = Str(f, "name") ?? "",
        Description = Str(f, "description"),
        Args = Arr(f, "args").Select(ParseInputValue).ToList(),
        Type = f.TryGetProperty("type", out var t) ? ParseTypeRef(t) : new GqlTypeRef(),
        IsDeprecated = Bool(f, "isDeprecated"),
        DeprecationReason = Str(f, "deprecationReason"),
    };

    private static GqlInputValue ParseInputValue(JsonElement v) => new()
    {
        Name = Str(v, "name") ?? "",
        Description = Str(v, "description"),
        Type = v.TryGetProperty("type", out var t) ? ParseTypeRef(t) : new GqlTypeRef(),
        DefaultValue = Str(v, "defaultValue"),
    };

    private static GqlTypeRef ParseTypeRef(JsonElement t) => new()
    {
        Kind = Str(t, "kind") ?? "",
        Name = Str(t, "name"),
        OfType = t.TryGetProperty("ofType", out var o) && o.ValueKind == JsonValueKind.Object ? ParseTypeRef(o) : null,
    };

    private static string? Str(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerable<JsonElement> Arr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.Array ? v.EnumerateArray() : Enumerable.Empty<JsonElement>();
}

public static class GraphQLService
{
    public const string IntrospectionQuery = """
        query IntrospectionQuery {
          __schema {
            queryType { name }
            mutationType { name }
            subscriptionType { name }
            types { ...FullType }
          }
        }
        fragment FullType on __Type {
          kind
          name
          description
          fields(includeDeprecated: true) {
            name
            description
            args { ...InputValue }
            type { ...TypeRef }
            isDeprecated
            deprecationReason
          }
          inputFields { ...InputValue }
          interfaces { ...TypeRef }
          enumValues(includeDeprecated: true) { name description isDeprecated deprecationReason }
          possibleTypes { ...TypeRef }
        }
        fragment InputValue on __InputValue {
          name
          description
          type { ...TypeRef }
          defaultValue
        }
        fragment TypeRef on __Type {
          kind name
          ofType { kind name ofType { kind name ofType { kind name ofType { kind name
            ofType { kind name ofType { kind name ofType { kind name } } } } } } }
        }
        """;

    public static async Task<GqlSchema> FetchSchemaAsync(ApiRequest source, IReadOnlyDictionary<string, string> vars, CancellationToken ct)
    {
        var r = source.Clone();
        r.Method = "POST";
        r.BodyMode = BodyMode.GraphQL;
        r.GraphQLQuery = IntrospectionQuery;
        r.GraphQLVariables = "";

        var res = await HttpService.SendAsync(r, vars, ct).ConfigureAwait(false);
        if (res.Error != null) throw new InvalidOperationException(res.Error);
        if (res.StatusCode >= 400)
        {
            var snippet = res.Body.Length > 500 ? res.Body[..500] + "…" : res.Body;
            throw new InvalidOperationException($"Introspection failed: HTTP {res.StatusCode} {res.ReasonPhrase}\n{snippet}");
        }
        try
        {
            return GqlSchema.FromIntrospectionResponse(res.Body);
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException("The introspection response was not valid JSON: " + ex.Message, ex);
        }
    }
}
