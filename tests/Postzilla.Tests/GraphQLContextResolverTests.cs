using Postzilla.GraphQL;

namespace Postzilla.Tests;

public class GraphQLContextResolverTests
{
    private const string Introspection = """
    {"data":{"__schema":{
      "queryType":{"name":"Query"},"mutationType":{"name":"Mutation"},"subscriptionType":null,
      "types":[
        {"kind":"OBJECT","name":"Query","fields":[
          {"name":"user","args":[{"name":"id","type":{"kind":"NON_NULL","name":null,"ofType":{"kind":"SCALAR","name":"ID","ofType":null}}}],
           "type":{"kind":"OBJECT","name":"User","ofType":null}}]},
        {"kind":"OBJECT","name":"Mutation","fields":[
          {"name":"rename","args":[],"type":{"kind":"OBJECT","name":"User","ofType":null}}]},
        {"kind":"OBJECT","name":"User","description":"A person","fields":[
          {"name":"name","args":[],"type":{"kind":"SCALAR","name":"String","ofType":null}},
          {"name":"friends","args":[{"name":"first","type":{"kind":"SCALAR","name":"Int","ofType":null}}],
           "type":{"kind":"LIST","name":null,"ofType":{"kind":"OBJECT","name":"User","ofType":null}}}]},
        {"kind":"SCALAR","name":"ID"},{"kind":"SCALAR","name":"String"},{"kind":"SCALAR","name":"Int"}
      ]}}}
    """;

    private static readonly GqlSchema Schema = GqlSchema.FromIntrospectionResponse(Introspection);

    private static DocTarget At(string query, string marker, int offset = 1)
    {
        var caret = query.IndexOf(marker, StringComparison.Ordinal) + offset;
        return GraphQLContextResolver.Resolve(Schema, query, caret);
    }

    [Fact]
    public void ResolvesNestedFieldThroughListType()
    {
        const string q = "query { user(id: \"1\") { friends(first: 2) { name } } }";
        Assert.Equal(new DocTarget(DocTargetKind.Field, "User", "name"), At(q, "name"));
        Assert.Equal(new DocTarget(DocTargetKind.Field, "User", "friends"), At(q, "friends"));
    }

    [Fact]
    public void ResolvesArgumentsAndVariableTypes()
    {
        const string q = "query Q($id: ID!) { user(id: $id) { name } }";
        Assert.Equal(new DocTarget(DocTargetKind.Argument, "Query", "user", "id"), At(q, "id: $"));
        Assert.Equal(new DocTarget(DocTargetKind.Type, "ID"), At(q, "ID!"));
    }

    [Fact]
    public void ResolvesAliasesMutationsAndFragments()
    {
        Assert.Equal(new DocTarget(DocTargetKind.Field, "Query", "user"), At("{ me: user { name } }", "me"));
        Assert.Equal(new DocTarget(DocTargetKind.Field, "Mutation", "rename"), At("mutation { rename { name } }", "rename"));
        Assert.Equal(new DocTarget(DocTargetKind.Field, "User", "name"), At("fragment F on User { name }", "name"));
    }

    [Fact]
    public void WhitespaceCaretReportsEnclosingTypeEvenWhenIncomplete()
    {
        const string q = "{ user { \n";
        Assert.Equal(new DocTarget(DocTargetKind.Type, "User"), GraphQLContextResolver.Resolve(Schema, q, q.Length));
    }

    [Fact]
    public void TypeRefRendersWrappers()
    {
        var friends = Schema.GetField("User", "friends")!;
        Assert.Equal("[User]", friends.Type.ToString());
        Assert.Equal("ID!", Schema.GetField("Query", "user")!.Args[0].Type.ToString());
    }
}
