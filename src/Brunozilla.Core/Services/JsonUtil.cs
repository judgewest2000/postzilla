using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Brunozilla.Services;

public static class JsonUtil
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        Converters = { new JsonStringEnumConverter() },
    };

    private static readonly JsonSerializerOptions PrettyOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private static readonly JsonDocumentOptions LenientDocument = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
    };

    public static bool TryPretty(string? text, out string pretty)
    {
        pretty = text ?? "";
        if (string.IsNullOrWhiteSpace(text)) return false;
        try
        {
            var node = JsonNode.Parse(text, documentOptions: LenientDocument);
            pretty = node?.ToJsonString(PrettyOptions) ?? "null";
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public static string Pretty(string? text) => TryPretty(text, out var p) ? p : text ?? "";
}
