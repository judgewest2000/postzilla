using Brunozilla.GraphQL;

namespace Brunozilla.Tests;

public class GraphQLCompletionTests
{
    private const string Introspection = """
    {"data":{"__schema":{
      "queryType":{"name":"Query"},"mutationType":null,"subscriptionType":null,
      "types":[
        {"kind":"OBJECT","name":"Query","fields":[
          {"name":"user","args":[
             {"name":"id","type":{"kind":"NON_NULL","ofType":{"kind":"SCALAR","name":"ID"}}},
             {"name":"role","type":{"kind":"ENUM","name":"Role"}},
             {"name":"filter","type":{"kind":"INPUT_OBJECT","name":"UserFilter"}}],
           "type":{"kind":"OBJECT","name":"User"}},
          {"name":"search","args":[],"type":{"kind":"UNION","name":"SearchResult"}}]},
        {"kind":"OBJECT","name":"User","fields":[
          {"name":"name","args":[],"type":{"kind":"SCALAR","name":"String"}},
          {"name":"email","args":[],"type":{"kind":"SCALAR","name":"String"}},
          {"name":"nickname","args":[],"type":{"kind":"SCALAR","name":"String"},"isDeprecated":true}]},
        {"kind":"OBJECT","name":"Post","fields":[{"name":"title","args":[],"type":{"kind":"SCALAR","name":"String"}}]},
        {"kind":"UNION","name":"SearchResult","possibleTypes":[{"kind":"OBJECT","name":"User"},{"kind":"OBJECT","name":"Post"}]},
        {"kind":"ENUM","name":"Role","enumValues":[{"name":"ADMIN"},{"name":"MEMBER"}]},
        {"kind":"INPUT_OBJECT","name":"UserFilter","inputFields":[
          {"name":"role","type":{"kind":"ENUM","name":"Role"}},
          {"name":"active","type":{"kind":"SCALAR","name":"Boolean"}}]},
        {"kind":"SCALAR","name":"ID"},{"kind":"SCALAR","name":"String"},{"kind":"SCALAR","name":"Boolean"}
      ]}}}
    """;

    internal static readonly GqlSchema Schema = GqlSchema.FromIntrospectionResponse(Introspection);

    /// <summary>Completes at the position of the '|' marker.</summary>
    private static CompletionResult Complete(string query)
    {
        var caret = query.IndexOf('|');
        return GraphQLCompletionProvider.GetCompletions(Schema, query.Remove(caret, 1), caret);
    }

    private static List<string> Labels(string query) => Complete(query).Items.Select(i => i.Label).ToList();

    [Fact]
    public void SuggestsFieldsOfEnclosingTypeFilteredByPrefix()
    {
        var result = Complete("{ user(id: 1) { na| } }");
        Assert.Equal("na", result.Prefix);
        Assert.Equal(16, result.ReplaceStart);
        // Prefix matches first, then substring matches; deprecated fields sort last.
        Assert.Equal(["name", "__typename", "nickname"], result.Items.Select(i => i.Label));
        Assert.Equal(["email", "name", "__typename", "nickname"], Labels("query { user { | } }"));
    }

    [Fact]
    public void SuggestsUnusedArgumentsAndEnumValues()
    {
        var args = Complete("{ user(id: 1, |) }").Items;
        Assert.Equal(["filter", "role"], args.Select(i => i.Label));
        Assert.Equal("role: ", args[1].InsertText);
        Assert.Equal(["ADMIN", "MEMBER"], Labels("{ user(role: |) }"));
        Assert.Equal(["ADMIN"], Labels("{ user(role: AD|) }"));
    }

    [Fact]
    public void SuggestsInputObjectFieldsAndTheirValues()
    {
        Assert.Equal(["active", "role"], Labels("{ user(filter: { | }) }"));
        Assert.Equal(["false", "true"], Labels("{ user(filter: { role: ADMIN, active: |"));
    }

    [Fact]
    public void SuggestsVariablesTypesAndKeywords()
    {
        Assert.Equal(["id"], Labels("query Q($id: ID!) { user(id: $|) }"));
        Assert.Contains("UserFilter", Labels("query Q($f: |) { }"));
        Assert.Equal(["query"], Labels("qu|"));
    }

    [Fact]
    public void SuggestsPossibleTypesForUnionsAndSkipsStringsAndComments()
    {
        Assert.Contains("... on Post", Labels("{ search { | } }"));
        Assert.Equal(["Post", "User"], Labels("{ search { ... on | } }"));
        Assert.Empty(Labels("{ user(id: \"na|\") }"));
        Assert.Empty(Labels("{ user # na|\n }"));
    }
}

public class GraphQLFormatterTests
{
    [Fact]
    public void FormatsSelectionsArgumentsAndFragments()
    {
        const string input = "query Q($id: ID!,$n:Int=5){user(id:$id filter:{a:1,b:[1 2]}){id,name ... on Admin{level} ...Frag @include(if: true)}}  mutation{x}";
        const string expected = """
            query Q($id: ID!, $n: Int = 5) {
              user(id: $id, filter: { a: 1, b: [1, 2] }) {
                id
                name
                ... on Admin {
                  level
                }
                ...Frag @include(if: true)
              }
            }

            mutation {
              x
            }
            """;
        Assert.Equal(expected.Replace("\r\n", "\n"), GraphQLFormatter.Format(input));
    }

    [Fact]
    public void KeepsCommentsAndStringsAndIsIdempotent()
    {
        const string input = "{\r\n  # who\r\n  user(name: \"a, b\") { id }\r\n}";
        var once = GraphQLFormatter.Format(input);
        Assert.Equal("{\n  # who\n  user(name: \"a, b\") {\n    id\n  }\n}", once);
        Assert.Equal(once, GraphQLFormatter.Format(once));
    }

    [Fact]
    public void BreaksLongArgumentsOntoSeparateLines()
    {
        const string input = """query { searchAfter(q: { authors: [{ name: "Kyle Orland" }], domains: ["arstechnica.com"], queryString: "\"myspace\" AND (x OR y)" }, size: 10) { position } }""";
        const string expected = """
            query {
              searchAfter(
                q: {
                  authors: [{ name: "Kyle Orland" }]
                  domains: ["arstechnica.com"]
                  queryString: "\"myspace\" AND (x OR y)"
                }
                size: 10
              ) {
                position
              }
            }
            """;
        var once = GraphQLFormatter.Format(input);
        Assert.Equal(expected.Replace("\r\n", "\n"), once);
        Assert.Equal(once, GraphQLFormatter.Format(once));
    }
}
