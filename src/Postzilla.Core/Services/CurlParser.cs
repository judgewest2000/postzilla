using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Postzilla.Models;

namespace Postzilla.Services;

public sealed record CurlParseResult(ApiRequest Request, string Name);

/// <summary>
/// Parses cURL commands as copied from browsers / docs: bash ("Copy as cURL (bash)"),
/// Windows cmd ("Copy as cURL (cmd)") and PowerShell-style backtick continuations.
/// </summary>
public static partial class CurlParser
{
    private static readonly HashSet<string> IgnoredOptionsWithValue = new(StringComparer.Ordinal)
    {
        "-o", "--output", "-m", "--max-time", "--connect-timeout", "-x", "--proxy", "-U", "--proxy-user",
        "-w", "--write-out", "--cacert", "--capath", "-E", "--cert", "--key", "--cert-type", "--key-type",
        "--retry", "--retry-delay", "--retry-max-time", "-r", "--range", "-c", "--cookie-jar", "-T",
        "--upload-file", "--resolve", "--interface", "--max-redirs", "-K", "--config", "--limit-rate",
        "-y", "--speed-time", "-Y", "--speed-limit", "--proxy-header", "--ciphers", "--tls-max",
        "--dns-servers", "-z", "--time-cond", "--trace", "--trace-ascii", "--stderr", "--unix-socket",
        "--abstract-unix-socket", "-P", "--ftp-port", "-Q", "--quote", "--connect-to", "--aws-sigv4",
    };

    // Short options taking a value that may be glued to the flag, e.g. -XPOST or -H'Accept: x'.
    private const string ShortOptionsWithValue = "XHdubAeFomxwr";

    [GeneratedRegex(@"^\s*curl(\.exe)?\s", RegexOptions.IgnoreCase)]
    private static partial Regex CurlPrefix();

    [GeneratedRegex(@"^curl(\.exe)?$", RegexOptions.IgnoreCase)]
    private static partial Regex CurlToken();

    [GeneratedRegex(@"^[^=&\s]+=[^&]*(&[^=&\s]+=[^&]*)*$")]
    private static partial Regex FormLike();

    public static bool LooksLikeCurl(string? text) => text != null && CurlPrefix().IsMatch(text);

    public static CurlParseResult Parse(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) throw new FormatException("The cURL command is empty.");

        var tokens = Tokenize(input);
        if (tokens.Count > 0 && CurlToken().IsMatch(tokens[0])) tokens.RemoveAt(0);

        string? method = null, url = null, user = null, bearer = null;
        var headers = new List<KeyValueItem>();
        var data = new List<string>();
        var form = new List<KeyValueItem>();
        bool hasData = false, isJson = false, forceGet = false, head = false;

        for (var i = 0; i < tokens.Count; i++)
        {
            var tok = tokens[i];
            var opt = tok;
            string? inline = null;

            if (tok.StartsWith("--", StringComparison.Ordinal) && tok.Contains('='))
            {
                var eq = tok.IndexOf('=');
                opt = tok[..eq];
                inline = tok[(eq + 1)..];
            }
            else if (tok.Length > 2 && tok[0] == '-' && tok[1] != '-' && ShortOptionsWithValue.Contains(tok[1]))
            {
                opt = tok[..2];
                inline = tok[2..];
            }

            string Value()
            {
                if (inline != null) return inline;
                if (i + 1 >= tokens.Count) throw new FormatException($"Option '{opt}' requires a value.");
                return tokens[++i];
            }

            switch (opt)
            {
                case "-X":
                case "--request":
                    method = Value().ToUpperInvariant();
                    break;
                case "-H":
                case "--header":
                    AddHeader(headers, Value());
                    break;
                case "-d":
                case "--data":
                case "--data-ascii":
                case "--data-binary":
                case "--data-raw":
                    data.Add(Value());
                    hasData = true;
                    break;
                case "--data-urlencode":
                    data.Add(EncodeDataUrlencode(Value()));
                    hasData = true;
                    break;
                case "--json":
                    data.Add(Value());
                    hasData = true;
                    isJson = true;
                    break;
                case "-F":
                case "--form":
                case "--form-string":
                    AddForm(form, Value());
                    break;
                case "-u":
                case "--user":
                    user = Value();
                    break;
                case "--oauth2-bearer":
                    bearer = Value();
                    break;
                case "-A":
                case "--user-agent":
                    headers.Add(new KeyValueItem("User-Agent", Value()));
                    break;
                case "-b":
                case "--cookie":
                    var cookie = Value();
                    if (cookie.Contains('=')) headers.Add(new KeyValueItem("Cookie", cookie));
                    break;
                case "-e":
                case "--referer":
                    headers.Add(new KeyValueItem("Referer", Value()));
                    break;
                case "-G":
                case "--get":
                    forceGet = true;
                    break;
                case "-I":
                case "--head":
                    head = true;
                    break;
                case "--url":
                    url = Value();
                    break;
                default:
                    if (tok.Length > 1 && tok[0] == '-')
                    {
                        if (inline == null && IgnoredOptionsWithValue.Contains(opt)) i++;
                    }
                    else
                    {
                        url ??= tok;
                    }
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(url)) throw new FormatException("No URL found in the cURL command.");
        if (!url.Contains("://") && !url.StartsWith("{{", StringComparison.Ordinal)) url = "http://" + url;

        var body = string.Join(isJson ? "" : "&", data);
        if (forceGet && hasData)
        {
            url += (url.Contains('?') ? "&" : "?") + body;
            body = "";
            hasData = false;
        }

        var request = new ApiRequest
        {
            Url = url,
            Method = method ?? (head ? "HEAD" : forceGet ? "GET" : hasData || form.Count > 0 ? "POST" : "GET"),
        };

        if (user != null)
        {
            var colon = user.IndexOf(':');
            request.AuthMode = AuthMode.Basic;
            request.AuthUsername = colon < 0 ? user : user[..colon];
            request.AuthPassword = colon < 0 ? "" : user[(colon + 1)..];
        }
        if (bearer != null)
        {
            request.AuthMode = AuthMode.Bearer;
            request.AuthToken = bearer;
        }
        ExtractAuthorizationHeader(request, headers);

        if (isJson)
        {
            if (!HasHeader(headers, "Content-Type")) headers.Add(new KeyValueItem("Content-Type", "application/json"));
            if (!HasHeader(headers, "Accept")) headers.Add(new KeyValueItem("Accept", "application/json"));
        }
        request.Headers = headers;

        var contentType = headers.FirstOrDefault(h => h.Key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase))?.Value;
        if (form.Count > 0)
        {
            request.BodyMode = BodyMode.Multipart;
            request.FormFields = form;
        }
        else if (hasData)
        {
            var trimmed = body.Trim();
            var jsonish = isJson
                || (contentType?.Contains("json", StringComparison.OrdinalIgnoreCase) ?? false)
                || trimmed.StartsWith('{') || trimmed.StartsWith('[');

            if (jsonish && TryGraphQL(trimmed, out var query, out var variables))
            {
                request.BodyMode = BodyMode.GraphQL;
                request.GraphQLQuery = query;
                request.GraphQLVariables = variables;
            }
            else if (jsonish && JsonUtil.TryPretty(body, out var pretty))
            {
                request.BodyMode = BodyMode.Json;
                request.Body = pretty;
            }
            else if ((contentType == null || contentType.Contains("x-www-form-urlencoded", StringComparison.OrdinalIgnoreCase))
                     && FormLike().IsMatch(body))
            {
                request.BodyMode = BodyMode.FormUrlEncoded;
                request.FormFields = ParseForm(body);
            }
            else
            {
                request.BodyMode = BodyMode.Text;
                request.Body = body;
            }
        }

        request.Params = UrlQuery.Parse(url);
        return new CurlParseResult(request, BuildName(request.Method, url));
    }

    private static bool HasHeader(List<KeyValueItem> headers, string name) =>
        headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

    private static void ExtractAuthorizationHeader(ApiRequest request, List<KeyValueItem> headers)
    {
        if (request.AuthMode != AuthMode.None) return;
        var auth = headers.FirstOrDefault(h => h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase));
        if (auth == null) return;

        if (auth.Value.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            request.AuthMode = AuthMode.Bearer;
            request.AuthToken = auth.Value[7..].Trim();
            headers.Remove(auth);
        }
        else if (auth.Value.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var decoded = Encoding.UTF8.GetString(Convert.FromBase64String(auth.Value[6..].Trim()));
                var colon = decoded.IndexOf(':');
                if (colon < 0) return;
                request.AuthMode = AuthMode.Basic;
                request.AuthUsername = decoded[..colon];
                request.AuthPassword = decoded[(colon + 1)..];
                headers.Remove(auth);
            }
            catch (FormatException)
            {
                // Leave the header untouched when it isn't valid base64.
            }
        }
    }

    private static bool TryGraphQL(string json, out string query, out string variables)
    {
        query = variables = "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("query", out var q)
                || q.ValueKind != JsonValueKind.String)
                return false;

            query = q.GetString() ?? "";
            if (root.TryGetProperty("variables", out var v) && v.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
                variables = JsonUtil.Pretty(v.GetRawText());
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static void AddHeader(List<KeyValueItem> headers, string raw)
    {
        var colon = raw.IndexOf(':');
        if (colon < 0)
        {
            // curl's "Name;" syntax sends a header with an empty value.
            headers.Add(new KeyValueItem(raw.TrimEnd(';').Trim(), ""));
            return;
        }
        var name = raw[..colon].Trim();
        var value = raw[(colon + 1)..].Trim();
        // "Name:" with no value tells curl to remove the header.
        if (name.Length == 0 || value.Length == 0) return;
        headers.Add(new KeyValueItem(name, value));
    }

    private static void AddForm(List<KeyValueItem> form, string raw)
    {
        var eq = raw.IndexOf('=');
        if (eq <= 0) return;
        var value = raw[(eq + 1)..];
        if (value.StartsWith('@') || value.StartsWith('<')) value = "@" + value[1..].Split(';')[0].Trim('"');
        form.Add(new KeyValueItem(raw[..eq], value));
    }

    private static string EncodeDataUrlencode(string value)
    {
        if (value.StartsWith('=')) return Uri.EscapeDataString(value[1..]);
        var eq = value.IndexOf('=');
        return eq > 0 ? value[..eq] + "=" + Uri.EscapeDataString(value[(eq + 1)..]) : Uri.EscapeDataString(value);
    }

    private static List<KeyValueItem> ParseForm(string body) =>
        body.Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(p =>
            {
                var eq = p.IndexOf('=');
                return eq < 0
                    ? new KeyValueItem(WebUtility.UrlDecode(p), "")
                    : new KeyValueItem(WebUtility.UrlDecode(p[..eq]), WebUtility.UrlDecode(p[(eq + 1)..]));
            })
            .ToList();

    private static string BuildName(string method, string url)
    {
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri))
        {
            var segment = uri.Segments.Select(s => s.Trim('/')).LastOrDefault(s => s.Length > 0);
            return $"{method} {Uri.UnescapeDataString(segment ?? uri.Host)}";
        }
        return $"{method} {(url.Length > 40 ? url[..40] + "…" : url)}";
    }

    // ---------------------------------------------------------------- tokenizing

    public static List<string> Tokenize(string input)
    {
        var cmdStyle = Regex.IsMatch(input, @"\^\r?\n|\^""");
        var normalized = cmdStyle
            ? Regex.Replace(input, @"\^\r?\n", " ")
            : Regex.Replace(input, @"(\\|`)\r?\n", " ");
        return cmdStyle ? TokenizeCmd(normalized) : TokenizePosix(normalized);
    }

    private static List<string> TokenizePosix(string s)
    {
        var tokens = new List<string>();
        var sb = new StringBuilder();
        var inToken = false;
        var i = 0;

        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c))
            {
                if (inToken)
                {
                    tokens.Add(sb.ToString());
                    sb.Clear();
                    inToken = false;
                }
                i++;
                continue;
            }

            inToken = true;
            if (c == '\'')
            {
                var end = s.IndexOf('\'', i + 1);
                if (end < 0) throw new FormatException("Unterminated single quote in cURL command.");
                sb.Append(s, i + 1, end - i - 1);
                i = end + 1;
            }
            else if (c == '$' && i + 1 < s.Length && s[i + 1] == '\'')
            {
                i = ReadAnsiCString(s, i + 2, sb);
            }
            else if (c == '"')
            {
                i++;
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && "\"\\$`\n".Contains(s[i + 1])) i++;
                    sb.Append(s[i]);
                    i++;
                }
                if (i >= s.Length) throw new FormatException("Unterminated double quote in cURL command.");
                i++;
            }
            else if (c == '\\' && i + 1 < s.Length)
            {
                sb.Append(s[i + 1]);
                i += 2;
            }
            else
            {
                sb.Append(c);
                i++;
            }
        }

        if (inToken) tokens.Add(sb.ToString());
        return tokens;
    }

    private static int ReadAnsiCString(string s, int i, StringBuilder sb)
    {
        while (i < s.Length && s[i] != '\'')
        {
            if (s[i] == '\\' && i + 1 < s.Length)
            {
                i++;
                switch (s[i])
                {
                    case 'n': sb.Append('\n'); break;
                    case 't': sb.Append('\t'); break;
                    case 'r': sb.Append('\r'); break;
                    case '0': sb.Append('\0'); break;
                    case 'x' or 'u':
                        var len = s[i] == 'x' ? 2 : 4;
                        var start = i + 1;
                        var n = 0;
                        while (n < len && start + n < s.Length && Uri.IsHexDigit(s[start + n])) n++;
                        if (n == 0) { sb.Append(s[i]); break; }
                        sb.Append((char)Convert.ToInt32(s.Substring(start, n), 16));
                        i += n;
                        break;
                    default: sb.Append(s[i]); break;
                }
            }
            else
            {
                sb.Append(s[i]);
            }
            i++;
        }
        if (i >= s.Length) throw new FormatException("Unterminated $'...' string in cURL command.");
        return i + 1;
    }

    private static List<string> TokenizeCmd(string s)
    {
        // Layer 1: cmd.exe caret escapes (only outside cmd quotes).
        var unescaped = new StringBuilder();
        var cmdQuoted = false;
        for (var i = 0; i < s.Length; i++)
        {
            var c = s[i];
            if (c == '"') { cmdQuoted = !cmdQuoted; unescaped.Append(c); }
            else if (c == '^' && !cmdQuoted && i + 1 < s.Length) unescaped.Append(s[++i]);
            else unescaped.Append(c);
        }

        // Layer 2: MSVCRT argv rules.
        var text = unescaped.ToString();
        var tokens = new List<string>();
        var sb = new StringBuilder();
        bool inQuotes = false, inToken = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (c == '\\')
            {
                var n = 0;
                while (i < text.Length && text[i] == '\\') { n++; i++; }
                if (i < text.Length && text[i] == '"')
                {
                    sb.Append('\\', n / 2);
                    if (n % 2 == 1) sb.Append('"');
                    else inQuotes = !inQuotes;
                }
                else
                {
                    sb.Append('\\', n);
                    i--;
                }
                inToken = true;
            }
            else if (c == '"')
            {
                if (inQuotes && i + 1 < text.Length && text[i + 1] == '"') { sb.Append('"'); i++; }
                else inQuotes = !inQuotes;
                inToken = true;
            }
            else if (char.IsWhiteSpace(c) && !inQuotes)
            {
                if (inToken) { tokens.Add(sb.ToString()); sb.Clear(); inToken = false; }
            }
            else
            {
                sb.Append(c);
                inToken = true;
            }
        }
        if (inToken) tokens.Add(sb.ToString());
        return tokens;
    }
}

public static class CurlExporter
{
    public static string Export(ApiRequest r, IReadOnlyDictionary<string, string> vars)
    {
        string R(string? s) => VariableResolver.Resolve(s, vars);
        var lines = new List<string>();
        var method = string.IsNullOrWhiteSpace(r.Method) ? "GET" : r.Method.ToUpperInvariant();
        var first = "curl";
        if (method != "GET") first += " -X " + method;
        first += " " + Q(R(r.Url).Trim());
        lines.Add(first);

        var headers = r.Headers.Where(h => h.Enabled && h.Key.Length > 0).Select(h => (Key: R(h.Key), Value: R(h.Value))).ToList();
        bool Has(string name) => headers.Any(h => h.Key.Equals(name, StringComparison.OrdinalIgnoreCase));

        switch (r.AuthMode)
        {
            case AuthMode.Bearer when !Has("Authorization"):
                headers.Add(("Authorization", "Bearer " + R(r.AuthToken)));
                break;
            case AuthMode.ApiKey when r.ApiKeyName.Length > 0:
                headers.Add((R(r.ApiKeyName), R(r.ApiKeyValue)));
                break;
        }
        if (r.BodyMode is BodyMode.Json or BodyMode.GraphQL && !Has("Content-Type"))
            headers.Add(("Content-Type", "application/json"));

        foreach (var (k, v) in headers) lines.Add("-H " + Q($"{k}: {v}"));
        if (r.AuthMode == AuthMode.Basic) lines.Add("-u " + Q($"{R(r.AuthUsername)}:{R(r.AuthPassword)}"));

        switch (r.BodyMode)
        {
            case BodyMode.Json or BodyMode.Text when r.Body.Length > 0:
                lines.Add("--data-raw " + Q(R(r.Body)));
                break;
            case BodyMode.FormUrlEncoded:
                foreach (var f in r.FormFields.Where(f => f.Enabled && f.Key.Length > 0))
                    lines.Add("--data-urlencode " + Q($"{R(f.Key)}={R(f.Value)}"));
                break;
            case BodyMode.Multipart:
                foreach (var f in r.FormFields.Where(f => f.Enabled && f.Key.Length > 0))
                    lines.Add("-F " + Q($"{R(f.Key)}={R(f.Value)}"));
                break;
            case BodyMode.GraphQL:
                lines.Add("--data-raw " + Q(HttpService.BuildGraphQLPayload(R(r.GraphQLQuery), R(r.GraphQLVariables))));
                break;
        }

        return string.Join(" \\\n  ", lines);
    }

    private static string Q(string s) => "'" + s.Replace("'", "'\\''") + "'";
}
