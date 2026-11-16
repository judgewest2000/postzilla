using Brunozilla.Models;

namespace Brunozilla.Services;

/// <summary>Keeps the query-string of a URL and a params table in sync (raw, un-encoded text).</summary>
public static class UrlQuery
{
    public static (string Base, string Query, string Fragment) Split(string url)
    {
        url ??= "";
        var fragment = "";
        var hash = url.IndexOf('#');
        if (hash >= 0)
        {
            fragment = url[hash..];
            url = url[..hash];
        }
        var q = url.IndexOf('?');
        return q < 0 ? (url, "", fragment) : (url[..q], url[(q + 1)..], fragment);
    }

    public static List<KeyValueItem> Parse(string url)
    {
        var (_, query, _) = Split(url);
        var items = new List<KeyValueItem>();
        if (query.Length == 0) return items;
        foreach (var part in query.Split('&'))
        {
            if (part.Length == 0) continue;
            var eq = part.IndexOf('=');
            items.Add(eq < 0 ? new KeyValueItem(part, "") : new KeyValueItem(part[..eq], part[(eq + 1)..]));
        }
        return items;
    }

    public static string WithParams(string url, IEnumerable<KeyValueItem> items)
    {
        var (baseUrl, _, fragment) = Split(url);
        var parts = items
            .Where(i => i.Enabled && i.Key.Length > 0)
            .Select(i => i.Value.Length > 0 ? $"{i.Key}={i.Value}" : i.Key)
            .ToList();
        return baseUrl + (parts.Count > 0 ? "?" + string.Join("&", parts) : "") + fragment;
    }
}
