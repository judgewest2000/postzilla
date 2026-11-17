using Postzilla.GraphQL;

namespace Postzilla.Tests;

public class GraphQLHighlighterTests
{
    private static Dictionary<string, GraphQLTokenKind> Classify(string text) =>
        GraphQLHighlighter.Tokenize(text)
            .GroupBy(t => text.Substring(t.Start, t.Length))
            .ToDictionary(g => g.Key, g => g.First().Kind);

    [Fact]
    public void ClassifiesOperationParts()
    {
        var k = Classify("""
            # get a user
            query GetUser($id: ID!, $tags: [String!]) {
              user(id: $id, active: true) @include(if: true) {
                name
                ... on Admin { level }
                bio(format: "md", max: 120)
              }
            }
            """);

        Assert.Equal(GraphQLTokenKind.Comment, k["# get a user"]);
        Assert.Equal(GraphQLTokenKind.Keyword, k["query"]);
        Assert.Equal(GraphQLTokenKind.OperationName, k["GetUser"]);
        Assert.Equal(GraphQLTokenKind.Variable, k["$id"]);
        Assert.Equal(GraphQLTokenKind.Type, k["ID"]);
        Assert.Equal(GraphQLTokenKind.Type, k["String"]);
        Assert.Equal(GraphQLTokenKind.Field, k["user"]);
        Assert.Equal(GraphQLTokenKind.Field, k["active"]);
        Assert.Equal(GraphQLTokenKind.Constant, k["true"]);
        Assert.Equal(GraphQLTokenKind.Directive, k["@include"]);
        Assert.Equal(GraphQLTokenKind.Field, k["name"]);
        Assert.Equal(GraphQLTokenKind.Keyword, k["on"]);
        Assert.Equal(GraphQLTokenKind.Type, k["Admin"]);
        Assert.Equal(GraphQLTokenKind.String, k["\"md\""]);
        Assert.Equal(GraphQLTokenKind.Number, k["120"]);
    }

    [Fact]
    public void KeywordLikeFieldNamesStayFields()
    {
        var k = Classify("{ type query on: x name }");

        Assert.Equal(GraphQLTokenKind.Field, k["type"]);
        Assert.Equal(GraphQLTokenKind.Field, k["query"]);
        Assert.Equal(GraphQLTokenKind.Field, k["on"]);
        Assert.Equal(GraphQLTokenKind.Field, k["name"]);
    }

    [Fact]
    public void ToleratesUnterminatedStringsAndBlockStrings()
    {
        var text = "{ a(x: \"open\n  b \"\"\"block\nstring\"\"\" c }";
        var tokens = GraphQLHighlighter.Tokenize(text);

        Assert.Contains(tokens, t => t.Kind == GraphQLTokenKind.String && text.Substring(t.Start, t.Length) == "\"open");
        Assert.Contains(tokens, t => t.Kind == GraphQLTokenKind.String && text.Substring(t.Start, t.Length) == "\"\"\"block\nstring\"\"\"");
        Assert.All(tokens, t => Assert.True(t.Start + t.Length <= text.Length));
    }
}
