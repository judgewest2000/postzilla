using System.Text.Json;
using Brunozilla.Models;

namespace Brunozilla.Services;

/// <summary>Imports Postman v2.0/v2.1 collection exports.</summary>
public static class PostmanImporter
{
    public static bool IsPostman(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object
        && root.TryGetProperty("info", out var info)
        && root.TryGetProperty("item", out _)
        && (!info.TryGetProperty("schema", out var schema) || (schema.GetString() ?? "").Contains("getpostman", StringComparison.OrdinalIgnoreCase));

    public static ApiCollection Import(JsonElement root)
    {
        var info = root.GetProperty("info");
        var collection = new ApiCollection { Name = Str(info, "name") ?? "Imported collection" };

        foreach (var v in Arr(root, "variable"))
            collection.Variables.Add(new KeyValueItem(Str(v, "key") ?? "", ValueString(v), !Bool(v, "disabled")));

        root.TryGetProperty("auth", out var auth);
        collection.Items = ReadItems(root, auth);
        return collection;
    }

    private static List<CollectionNode> ReadItems(JsonElement parent, JsonElement inheritedAuth)
    {
        var nodes = new List<CollectionNode>();
        foreach (var item in Arr(parent, "item"))
        {
            var name = Str(item, "name") ?? "Untitled";
            var auth = item.TryGetProperty("auth", out var a) ? a : inheritedAuth;
            if (item.TryGetProperty("item", out _))
            {
                var folder = CollectionNode.Folder(name);
                folder.Children = ReadItems(item, auth);
                nodes.Add(folder);
            }
            else if (item.TryGetProperty("request", out var req))
            {
                nodes.Add(CollectionNode.ForRequest(name, ReadRequest(req, auth)));
            }
        }
        return nodes;
    }

    private static ApiRequest ReadRequest(JsonElement req, JsonElement inheritedAuth)
    {
        var r = new ApiRequest();
        if (req.ValueKind == JsonValueKind.String)
        {
            r.Url = req.GetString() ?? "";
            r.Params = UrlQuery.Parse(r.Url);
            return r;
        }

        r.Method = (Str(req, "method") ?? "GET").ToUpperInvariant();
        if (req.TryGetProperty("url", out var url))
        {
            r.Url = url.ValueKind == JsonValueKind.String ? url.GetString() ?? "" : Str(url, "raw") ?? "";
            var disabledParams = url.ValueKind == JsonValueKind.Object
                ? Arr(url, "query").Where(q => Bool(q, "disabled")).Select(q => new KeyValueItem(Str(q, "key") ?? "", Str(q, "value") ?? "", false))
                : Enumerable.Empty<KeyValueItem>();
            r.Params = UrlQuery.Parse(r.Url).Concat(disabledParams).ToList();
        }

        foreach (var h in Arr(req, "header"))
            r.Headers.Add(new KeyValueItem(Str(h, "key") ?? "", Str(h, "value") ?? "", !Bool(h, "disabled")));

        if (req.TryGetProperty("body", out var body) && body.ValueKind == JsonValueKind.Object) ReadBody(r, body);

        var auth = req.TryGetProperty("auth", out var a) ? a : inheritedAuth;
        if (auth.ValueKind == JsonValueKind.Object) ReadAuth(r, auth);
        return r;
    }

    private static void ReadBody(ApiRequest r, JsonElement body)
    {
        switch (Str(body, "mode"))
        {
            case "raw":
                var raw = Str(body, "raw") ?? "";
                var language = body.TryGetProperty("options", out var o) && o.TryGetProperty("raw", out var ro) ? Str(ro, "language") : null;
                var isJson = language == "json" || (language == null && JsonUtil.TryPretty(raw, out _));
                r.BodyMode = isJson ? BodyMode.Json : BodyMode.Text;
                r.Body = raw;
                break;
            case "urlencoded":
                r.BodyMode = BodyMode.FormUrlEncoded;
                r.FormFields = Arr(body, "urlencoded").Select(f => new KeyValueItem(Str(f, "key") ?? "", Str(f, "value") ?? "", !Bool(f, "disabled"))).ToList();
                break;
            case "formdata":
                r.BodyMode = BodyMode.Multipart;
                r.FormFields = Arr(body, "formdata").Select(f =>
                {
                    var value = Str(f, "type") == "file" ? "@" + (Str(f, "src") ?? "") : Str(f, "value") ?? "";
                    return new KeyValueItem(Str(f, "key") ?? "", value, !Bool(f, "disabled"));
                }).ToList();
                break;
            case "graphql":
                r.BodyMode = BodyMode.GraphQL;
                if (body.TryGetProperty("graphql", out var g))
                {
                    r.GraphQLQuery = Str(g, "query") ?? "";
                    r.GraphQLVariables = JsonUtil.Pretty(Str(g, "variables") ?? "");
                }
                break;
        }
    }

    private static void ReadAuth(ApiRequest r, JsonElement auth)
    {
        var type = Str(auth, "type");
        string AuthValue(string key) =>
            type != null && auth.TryGetProperty(type, out var list)
                ? (list.ValueKind == JsonValueKind.Array
                    ? Arr(list).Where(e => Str(e, "key") == key).Select(ValueString).FirstOrDefault()
                    : Str(list, key)) ?? ""
                : "";

        switch (type)
        {
            case "bearer":
                r.AuthMode = AuthMode.Bearer;
                r.AuthToken = AuthValue("token");
                break;
            case "basic":
                r.AuthMode = AuthMode.Basic;
                r.AuthUsername = AuthValue("username");
                r.AuthPassword = AuthValue("password");
                break;
            case "apikey" when AuthValue("in") != "query":
                r.AuthMode = AuthMode.ApiKey;
                r.ApiKeyName = AuthValue("key");
                r.ApiKeyValue = AuthValue("value");
                break;
        }
    }

    private static string ValueString(JsonElement e) =>
        e.TryGetProperty("value", out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.GetRawText()) : "";

    private static string? Str(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool Bool(JsonElement el, string prop) =>
        el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.True;

    private static IEnumerable<JsonElement> Arr(JsonElement el, string? prop = null)
    {
        if (prop != null && !(el.ValueKind == JsonValueKind.Object && el.TryGetProperty(prop, out el)))
            return Enumerable.Empty<JsonElement>();
        return el.ValueKind == JsonValueKind.Array ? el.EnumerateArray() : Enumerable.Empty<JsonElement>();
    }
}
