using System.Text.Json;
using Postzilla.Models;

namespace Postzilla.Services;

/// <summary>Stores each collection as a JSON file plus a single environments file.</summary>
public sealed class CollectionStore
{
    public CollectionStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? DefaultRootDirectory();
    }

    /// <summary>%LOCALAPPDATA%\Postzilla, moving data over from the app's former name (Brunozilla) on first run.</summary>
    private static string DefaultRootDirectory()
    {
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var root = Path.Combine(localAppData, "Postzilla");
        var legacy = Path.Combine(localAppData, "Brunozilla");
        if (!Directory.Exists(root) && Directory.Exists(legacy))
        {
            try { Directory.Move(legacy, root); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
        return root;
    }

    public string RootDirectory { get; }
    public string CollectionsDirectory => Path.Combine(RootDirectory, "collections");
    private string EnvironmentsFile => Path.Combine(RootDirectory, "environments.json");
    private string ExpandedFile => Path.Combine(RootDirectory, "expanded.json");

    /// <summary>Creates the data folder and an example collection on first run.</summary>
    public void EnsureInitialized()
    {
        if (Directory.Exists(CollectionsDirectory)) return;
        Directory.CreateDirectory(CollectionsDirectory);
        Save(SampleData.CreateExamples());
    }

    public List<ApiCollection> LoadCollections()
    {
        var result = new List<ApiCollection>();
        if (!Directory.Exists(CollectionsDirectory)) return result;
        foreach (var file in Directory.EnumerateFiles(CollectionsDirectory, "*.json"))
        {
            try
            {
                var c = JsonSerializer.Deserialize<ApiCollection>(File.ReadAllText(file), JsonUtil.Options);
                if (c == null) continue;
                c.FilePath = file;
                result.Add(c);
            }
            catch (JsonException)
            {
                // Skip unreadable files rather than failing startup.
            }
        }
        return result.OrderBy(c => c.Name, StringComparer.CurrentCultureIgnoreCase).ToList();
    }

    public void Save(ApiCollection c)
    {
        Directory.CreateDirectory(CollectionsDirectory);
        c.FilePath ??= Path.Combine(CollectionsDirectory, $"{SafeFileName(c.Name)}-{c.Id[..8]}.json");
        WriteAtomic(c.FilePath, JsonSerializer.Serialize(c, JsonUtil.Options));
    }

    public void Delete(ApiCollection c)
    {
        if (c.FilePath != null && File.Exists(c.FilePath)) File.Delete(c.FilePath);
    }

    public List<AppEnvironment> LoadEnvironments()
    {
        try
        {
            return File.Exists(EnvironmentsFile)
                ? JsonSerializer.Deserialize<List<AppEnvironment>>(File.ReadAllText(EnvironmentsFile), JsonUtil.Options) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    public void SaveEnvironments(List<AppEnvironment> environments)
    {
        Directory.CreateDirectory(RootDirectory);
        WriteAtomic(EnvironmentsFile, JsonSerializer.Serialize(environments, JsonUtil.Options));
    }

    /// <summary>Ids of expanded collections/folders in the sidebar, or null if never saved.</summary>
    public HashSet<string>? LoadExpandedIds()
    {
        try
        {
            return File.Exists(ExpandedFile)
                ? JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(ExpandedFile), JsonUtil.Options)
                : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void SaveExpandedIds(IEnumerable<string> ids)
    {
        Directory.CreateDirectory(RootDirectory);
        WriteAtomic(ExpandedFile, JsonSerializer.Serialize(ids.ToList(), JsonUtil.Options));
    }

    /// <summary>Imports a Postzilla or Postman collection file. The result is not yet saved.</summary>
    public static ApiCollection Import(string json)
    {
        using var doc = JsonDocument.Parse(json, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        if (PostmanImporter.IsPostman(doc.RootElement)) return PostmanImporter.Import(doc.RootElement);

        var c = doc.RootElement.Deserialize<ApiCollection>(JsonUtil.Options);
        if (c == null || (c.Items.Count == 0 && !doc.RootElement.TryGetProperty("items", out _)))
            throw new InvalidOperationException("The file is not a Postzilla or Postman (v2.x) collection.");
        c.Id = Guid.NewGuid().ToString("N");
        c.FilePath = null;
        return c;
    }

    public static string Export(ApiCollection c) => JsonSerializer.Serialize(c, JsonUtil.Options);

    private static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var safe = new string(name.Select(ch => invalid.Contains(ch) ? '_' : ch).ToArray()).Trim();
        return safe.Length == 0 ? "collection" : safe.Length > 50 ? safe[..50] : safe;
    }

    private static void WriteAtomic(string path, string content)
    {
        var temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }
}

public static class SampleData
{
    public static ApiCollection CreateExamples()
    {
        var collection = new ApiCollection
        {
            Name = "Examples",
            Variables = { new KeyValueItem("httpbin", "https://httpbin.org") },
        };

        var http = CollectionNode.Folder("HTTP");
        http.Children.Add(CollectionNode.ForRequest("Get with params", new ApiRequest
        {
            Url = "{{httpbin}}/get?hello=world",
            Params = { new KeyValueItem("hello", "world") },
        }));
        http.Children.Add(CollectionNode.ForRequest("Post JSON", new ApiRequest
        {
            Method = "POST",
            Url = "{{httpbin}}/post",
            BodyMode = BodyMode.Json,
            Body = "{\n  \"id\": \"{{$guid}}\",\n  \"name\": \"Postzilla\"\n}",
        }));
        collection.Items.Add(http);

        collection.Items.Add(CollectionNode.ForRequest("Countries (GraphQL)", new ApiRequest
        {
            Method = "POST",
            Url = "https://countries.trevorblades.com/",
            BodyMode = BodyMode.GraphQL,
            GraphQLQuery = "query Country($code: ID!) {\n  country(code: $code) {\n    name\n    capital\n    currency\n    languages {\n      name\n    }\n  }\n}",
            GraphQLVariables = "{\n  \"code\": \"GB\"\n}",
        }));
        return collection;
    }
}
