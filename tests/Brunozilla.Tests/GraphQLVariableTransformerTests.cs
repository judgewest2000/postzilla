using System.Text.Json.Nodes;
using Brunozilla.GraphQL;

namespace Brunozilla.Tests;

public class GraphQLVariableTransformerTests
{
    private static readonly GqlSchema Schema = GraphQLCompletionTests.Schema;

    [Fact]
    public void InlineWritesTypedLiteralsAndDropsDefinitions()
    {
        var result = GraphQLVariableTransformer.Inline(
            "query Q($id: ID!, $role: Role, $f: UserFilter) { user(id: $id, role: $role, filter: $f) { name } }",
            """{ "id": "7", "role": "ADMIN", "f": { "role": "MEMBER", "active": true } }""",
            Schema);

        Assert.Equal("""query Q { user(id: "7", role: ADMIN, filter: {role: MEMBER, active: true}) { name } }""", result.Query);
        Assert.Equal("", result.Variables);
        Assert.Equal(3, result.Changed);
    }

    [Fact]
    public void InlineKeepsVariablesThatAreNotInTheJson()
    {
        var result = GraphQLVariableTransformer.Inline(
            "query ($id: ID!, $role: Role) { user(id: $id, role: $role) { name } }", """{ "id": 5, "other": 1 }""", Schema);

        Assert.Equal("query ($role: Role) { user(id: 5, role: $role) { name } }", result.Query);
        Assert.Equal(1, JsonNode.Parse(result.Variables)!["other"]!.GetValue<int>());
        Assert.Null(JsonNode.Parse(result.Variables)!["id"]);
    }

    [Fact]
    public void ExtractUsesSchemaTypesAndAddsDefinitions()
    {
        var result = GraphQLVariableTransformer.Extract(
            """{ user(id: 7, role: ADMIN, filter: {active: true}) { name } }""", "", Schema);

        Assert.Equal("query ($id: ID!, $role: Role, $filter: UserFilter) { user(id: $id, role: $role, filter: $filter) { name } }", result.Query);
        var vars = JsonNode.Parse(result.Variables)!;
        Assert.Equal(7, vars["id"]!.GetValue<int>());
        Assert.Equal("ADMIN", vars["role"]!.GetValue<string>());
        Assert.True(vars["filter"]!["active"]!.GetValue<bool>());
    }

    [Fact]
    public void ExtractWithoutSchemaInfersScalarsSkipsEnumsAndAvoidsNameClashes()
    {
        var result = GraphQLVariableTransformer.Extract(
            """query Q($x: Int) { user(id: "a\"b", role: ADMIN) { name } }""", """{ "id": 1 }""", null);

        Assert.Equal("query Q($x: Int, $id2: String) { user(id: $id2, role: ADMIN) { name } }", result.Query);
        Assert.Equal("a\"b", JsonNode.Parse(result.Variables)!["id2"]!.GetValue<string>());
        Assert.Equal(1, result.Skipped);
    }

    [Fact]
    public void ExtractThenInlineRoundTrips()
    {
        const string query = """query Find { user(id: "u1", role: MEMBER, filter: {role: ADMIN, active: false}) { name } }""";
        var extracted = GraphQLVariableTransformer.Extract(query, "", Schema);
        var inlined = GraphQLVariableTransformer.Inline(extracted.Query, extracted.Variables, Schema);

        Assert.Equal(query, inlined.Query);
        Assert.Equal("", inlined.Variables);
    }
}
