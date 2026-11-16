using Brunozilla.Models;
using Brunozilla.Services;

namespace Brunozilla.Tests;

public class CurlParserTests
{
    [Fact]
    public void ParsesBashMultilineWithJsonBodyAndBearer()
    {
        var curl = "curl -X PUT 'https://api.example.com/users/1?x=1' \\\n  -H 'Authorization: Bearer abc123' \\\n  -H \"Content-Type: application/json\" \\\n  --data-raw '{\"name\":\"O'\\''Brien\"}'";

        var r = CurlParser.Parse(curl).Request;

        Assert.Equal("PUT", r.Method);
        Assert.Equal("https://api.example.com/users/1?x=1", r.Url);
        Assert.Equal(AuthMode.Bearer, r.AuthMode);
        Assert.Equal("abc123", r.AuthToken);
        Assert.DoesNotContain(r.Headers, h => h.Key == "Authorization");
        Assert.Equal(BodyMode.Json, r.BodyMode);
        Assert.Contains("O'Brien", r.Body);
        Assert.Single(r.Params);
    }

    [Fact]
    public void DataImpliesPostAndFormBodyIsDecoded()
    {
        var r = CurlParser.Parse("curl https://x.test/login -d 'user=bob%20smith&pw=a+b'").Request;

        Assert.Equal("POST", r.Method);
        Assert.Equal(BodyMode.FormUrlEncoded, r.BodyMode);
        Assert.Equal("bob smith", r.FormFields[0].Value);
        Assert.Equal("a b", r.FormFields[1].Value);
    }

    [Fact]
    public void DetectsGraphQLPayload()
    {
        var r = CurlParser.Parse("""curl 'https://g.test/graphql' -H 'content-type: application/json' --data-raw '{"query":"{ me { id } }","variables":{"a":1}}'""").Request;

        Assert.Equal(BodyMode.GraphQL, r.BodyMode);
        Assert.Equal("{ me { id } }", r.GraphQLQuery);
        Assert.Contains("\"a\": 1", r.GraphQLVariables);
    }

    [Fact]
    public void ParsesChromeCmdStyle()
    {
        var curl = "curl ^\"https://x.test/api^\" ^\r\n  -H ^\"accept: */*^\" ^\r\n  --data-raw ^\"^{^\\^\"a^\\^\":1^}^\"";

        var r = CurlParser.Parse(curl).Request;

        Assert.Equal("https://x.test/api", r.Url);
        Assert.Equal("*/*", r.Headers.Single(h => h.Key == "accept").Value);
        Assert.Equal(BodyMode.Json, r.BodyMode);
        Assert.Contains("\"a\": 1", r.Body);
    }

    [Fact]
    public void HandlesGluedOptionsBasicAuthGetAndIgnoredFlags()
    {
        var r = CurlParser.Parse("curl -sSL -XGET -u alice:s3cret -o out.txt --compressed -G example.com/search -d q=hi").Request;

        Assert.Equal("GET", r.Method);
        Assert.Equal("http://example.com/search?q=hi", r.Url);
        Assert.Equal(AuthMode.Basic, r.AuthMode);
        Assert.Equal("alice", r.AuthUsername);
        Assert.Equal("s3cret", r.AuthPassword);
        Assert.Equal(BodyMode.None, r.BodyMode);
    }

    [Fact]
    public void ThrowsWhenUrlMissing() =>
        Assert.Throws<FormatException>(() => CurlParser.Parse("curl -H 'a: b'"));

    [Fact]
    public void ExportRoundTripsThroughParser()
    {
        var original = new ApiRequest
        {
            Method = "POST",
            Url = "{{base}}/items",
            Headers = { new KeyValueItem("X-Trace", "it's") },
            BodyMode = BodyMode.Json,
            Body = "{\n  \"a\": \"b\"\n}",
            AuthMode = AuthMode.Bearer,
            AuthToken = "{{token}}",
        };
        var vars = new Dictionary<string, string> { ["base"] = "https://api.test", ["token"] = "T" };

        var r = CurlParser.Parse(CurlExporter.Export(original, vars)).Request;

        Assert.Equal("https://api.test/items", r.Url);
        Assert.Equal("it's", r.Headers.Single(h => h.Key == "X-Trace").Value);
        Assert.Equal("T", r.AuthToken);
        Assert.Equal(BodyMode.Json, r.BodyMode);
    }
}
