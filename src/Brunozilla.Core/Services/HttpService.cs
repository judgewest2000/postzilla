using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Brunozilla.Models;

namespace Brunozilla.Services;

public sealed class ResponseResult
{
    public int StatusCode { get; init; }
    public string ReasonPhrase { get; init; } = "";
    public long ElapsedMs { get; init; }
    public long SizeBytes { get; init; }
    public List<KeyValueItem> Headers { get; init; } = new();
    public string Body { get; init; } = "";
    public string? ContentType { get; init; }
    public bool IsJson { get; init; }
    public string? Error { get; init; }
}

public static class HttpService
{
    private static readonly HttpClient Client = new(new SocketsHttpHandler
    {
        AutomaticDecompression = DecompressionMethods.All,
        AllowAutoRedirect = true,
        MaxAutomaticRedirections = 10,
        UseCookies = false,
        PooledConnectionLifetime = TimeSpan.FromMinutes(5),
    })
    {
        Timeout = Timeout.InfiniteTimeSpan,
    };

    public static TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(120);

    public static string BuildGraphQLPayload(string query, string? variablesJson)
    {
        var payload = new JsonObject { ["query"] = query };
        if (!string.IsNullOrWhiteSpace(variablesJson))
        {
            try
            {
                payload["variables"] = JsonNode.Parse(variablesJson);
            }
            catch (JsonException ex)
            {
                throw new InvalidOperationException("GraphQL variables are not valid JSON: " + ex.Message, ex);
            }
        }
        return payload.ToJsonString();
    }

    public static HttpRequestMessage BuildMessage(ApiRequest r, IReadOnlyDictionary<string, string> vars)
    {
        string R(string? s) => VariableResolver.Resolve(s, vars);

        var url = R(r.Url).Trim();
        if (url.Length == 0) throw new InvalidOperationException("Enter a URL.");
        if (!url.Contains("://")) url = "http://" + url;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri)) throw new InvalidOperationException($"Invalid URL: {url}");

        var method = string.IsNullOrWhiteSpace(r.Method) ? "GET" : r.Method.Trim().ToUpperInvariant();
        var msg = new HttpRequestMessage(new HttpMethod(method), uri) { Content = BuildContent(r, R) };

        foreach (var h in r.Headers.Where(h => h.Enabled && h.Key.Trim().Length > 0))
        {
            var name = R(h.Key).Trim();
            var value = R(h.Value);
            if (msg.Headers.TryAddWithoutValidation(name, value)) continue;
            if (msg.Content == null) continue;
            msg.Content.Headers.Remove(name);
            msg.Content.Headers.TryAddWithoutValidation(name, value);
        }

        if (!msg.Headers.Contains("Authorization"))
        {
            switch (r.AuthMode)
            {
                case AuthMode.Bearer:
                    msg.Headers.TryAddWithoutValidation("Authorization", "Bearer " + R(r.AuthToken).Trim());
                    break;
                case AuthMode.Basic:
                    var raw = Encoding.UTF8.GetBytes($"{R(r.AuthUsername)}:{R(r.AuthPassword)}");
                    msg.Headers.TryAddWithoutValidation("Authorization", "Basic " + Convert.ToBase64String(raw));
                    break;
            }
        }
        if (r.AuthMode == AuthMode.ApiKey && r.ApiKeyName.Trim().Length > 0)
            msg.Headers.TryAddWithoutValidation(R(r.ApiKeyName).Trim(), R(r.ApiKeyValue));

        if (!msg.Headers.Contains("User-Agent")) msg.Headers.TryAddWithoutValidation("User-Agent", "Brunozilla/1.0");
        if (!msg.Headers.Contains("Accept")) msg.Headers.TryAddWithoutValidation("Accept", "*/*");
        return msg;
    }

    private static HttpContent? BuildContent(ApiRequest r, Func<string?, string> resolve)
    {
        switch (r.BodyMode)
        {
            case BodyMode.Json when r.Body.Trim().Length > 0:
                return new StringContent(resolve(r.Body), Encoding.UTF8, "application/json");
            case BodyMode.Text when r.Body.Length > 0:
                return new StringContent(resolve(r.Body), Encoding.UTF8, "text/plain");
            case BodyMode.FormUrlEncoded:
                return new FormUrlEncodedContent(r.FormFields
                    .Where(f => f.Enabled && f.Key.Length > 0)
                    .Select(f => new KeyValuePair<string, string>(resolve(f.Key), resolve(f.Value))));
            case BodyMode.Multipart:
                var multipart = new MultipartFormDataContent();
                foreach (var f in r.FormFields.Where(f => f.Enabled && f.Key.Length > 0))
                {
                    var key = resolve(f.Key);
                    var value = resolve(f.Value);
                    if (value.StartsWith('@'))
                    {
                        var path = value[1..].Trim();
                        if (!File.Exists(path)) throw new InvalidOperationException($"Form file not found: {path}");
                        var file = new StreamContent(File.OpenRead(path));
                        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
                        multipart.Add(file, key, Path.GetFileName(path));
                    }
                    else
                    {
                        multipart.Add(new StringContent(value), key);
                    }
                }
                return multipart;
            case BodyMode.GraphQL:
                var payload = BuildGraphQLPayload(resolve(r.GraphQLQuery), resolve(r.GraphQLVariables));
                return new StringContent(payload, Encoding.UTF8, "application/json");
            default:
                return null;
        }
    }

    public static async Task<ResponseResult> SendAsync(ApiRequest r, IReadOnlyDictionary<string, string> vars, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        try
        {
            using var msg = BuildMessage(r, vars);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(RequestTimeout);

            using var resp = await Client.SendAsync(msg, HttpCompletionOption.ResponseHeadersRead, timeout.Token).ConfigureAwait(false);
            var bytes = await resp.Content.ReadAsByteArrayAsync(timeout.Token).ConfigureAwait(false);
            sw.Stop();

            var headers = resp.Headers.Concat(resp.Content.Headers)
                .SelectMany(h => h.Value.Select(v => new KeyValueItem(h.Key, v)))
                .ToList();
            var contentType = resp.Content.Headers.ContentType;
            var body = Decode(bytes, contentType?.CharSet, contentType?.MediaType, out var isJson);

            return new ResponseResult
            {
                StatusCode = (int)resp.StatusCode,
                ReasonPhrase = resp.ReasonPhrase ?? resp.StatusCode.ToString(),
                ElapsedMs = sw.ElapsedMilliseconds,
                SizeBytes = bytes.LongLength,
                Headers = headers,
                Body = body,
                ContentType = contentType?.MediaType,
                IsJson = isJson,
            };
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return new ResponseResult { Error = "Request cancelled.", ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (OperationCanceledException)
        {
            return new ResponseResult { Error = $"Request timed out after {RequestTimeout.TotalSeconds:0}s.", ElapsedMs = sw.ElapsedMilliseconds };
        }
        catch (Exception ex)
        {
            return new ResponseResult { Error = Flatten(ex), ElapsedMs = sw.ElapsedMilliseconds };
        }
    }

    private static string Decode(byte[] bytes, string? charset, string? mediaType, out bool isJson)
    {
        isJson = false;
        mediaType ??= "";
        if (mediaType.StartsWith("image/") || mediaType.StartsWith("audio/") || mediaType.StartsWith("video/")
            || mediaType is "application/octet-stream" or "application/pdf" or "application/zip")
            return $"[Binary content: {mediaType}, {bytes.Length:N0} bytes]";

        Encoding encoding;
        try
        {
            encoding = string.IsNullOrEmpty(charset) ? Encoding.UTF8 : Encoding.GetEncoding(charset.Trim('"'));
        }
        catch (ArgumentException)
        {
            encoding = Encoding.UTF8;
        }

        var text = encoding.GetString(bytes);
        var trimmed = text.TrimStart();
        if ((mediaType.Contains("json") || trimmed.StartsWith('{') || trimmed.StartsWith('['))
            && JsonUtil.TryPretty(text, out var pretty))
        {
            isJson = true;
            return pretty;
        }
        return text;
    }

    private static string Flatten(Exception ex)
    {
        var parts = new List<string>();
        for (var e = ex; e != null; e = e.InnerException)
            if (!parts.Contains(e.Message)) parts.Add(e.Message);
        return string.Join(" → ", parts);
    }
}
