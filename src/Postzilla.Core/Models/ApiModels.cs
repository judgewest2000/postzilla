using System.Text.Json;
using Postzilla.Services;

namespace Postzilla.Models;

public enum BodyMode { None, Json, Text, FormUrlEncoded, Multipart, GraphQL }

public enum AuthMode { None, Bearer, Basic, ApiKey }

public sealed class ApiRequest
{
    public string Method { get; set; } = "GET";
    public string Url { get; set; } = "";
    public List<KeyValueItem> Params { get; set; } = new();
    public List<KeyValueItem> Headers { get; set; } = new();
    public BodyMode BodyMode { get; set; }
    public string Body { get; set; } = "";
    public List<KeyValueItem> FormFields { get; set; } = new();
    public string GraphQLQuery { get; set; } = "";
    public string GraphQLVariables { get; set; } = "";
    public AuthMode AuthMode { get; set; }
    public string AuthToken { get; set; } = "";
    public string AuthUsername { get; set; } = "";
    public string AuthPassword { get; set; } = "";
    public string ApiKeyName { get; set; } = "X-API-Key";
    public string ApiKeyValue { get; set; } = "";

    public ApiRequest Clone() =>
        JsonSerializer.Deserialize<ApiRequest>(JsonSerializer.Serialize(this, JsonUtil.Options), JsonUtil.Options)!;
}

public sealed class CollectionNode
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "";
    public bool IsFolder { get; set; }
    public List<CollectionNode> Children { get; set; } = new();
    public ApiRequest? Request { get; set; }

    public static CollectionNode Folder(string name) => new() { Name = name, IsFolder = true };
    public static CollectionNode ForRequest(string name, ApiRequest request) => new() { Name = name, Request = request };

    public CollectionNode DeepClone()
    {
        var copy = JsonSerializer.Deserialize<CollectionNode>(JsonSerializer.Serialize(this, JsonUtil.Options), JsonUtil.Options)!;
        copy.ReassignIds();
        return copy;
    }

    private void ReassignIds()
    {
        Id = Guid.NewGuid().ToString("N");
        foreach (var c in Children) c.ReassignIds();
    }
}

public sealed class ApiCollection
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Collection";
    public List<KeyValueItem> Variables { get; set; } = new();
    public List<CollectionNode> Items { get; set; } = new();

    [System.Text.Json.Serialization.JsonIgnore]
    public string? FilePath { get; set; }

    /// <summary>Returns the list that directly contains <paramref name="node"/>.</summary>
    public List<CollectionNode>? FindParentList(CollectionNode node) => FindParentList(Items, node);

    private static List<CollectionNode>? FindParentList(List<CollectionNode> list, CollectionNode node)
    {
        if (list.Contains(node)) return list;
        foreach (var child in list)
        {
            var found = FindParentList(child.Children, node);
            if (found != null) return found;
        }
        return null;
    }
}

public sealed class AppEnvironment
{
    public string Id { get; set; } = Guid.NewGuid().ToString("N");
    public string Name { get; set; } = "New Environment";
    public List<KeyValueItem> Variables { get; set; } = new();

    public AppEnvironment Clone() => new() { Id = Id, Name = Name, Variables = KeyValueItem.CloneList(Variables) };
}
