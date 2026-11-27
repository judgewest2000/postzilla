using Postzilla.Models;

namespace Postzilla.Tests;

public class ResolveColorTests
{
    [Fact]
    public void RequestUsesNearestColouredFolderElseCollection()
    {
        var deep = CollectionNode.ForRequest("deep", new ApiRequest());
        var inner = CollectionNode.Folder("inner");
        inner.Children.Add(deep);
        var outer = CollectionNode.Folder("outer");
        outer.Color = "#2575D6";
        outer.Children.Add(inner);
        var top = CollectionNode.ForRequest("top", new ApiRequest());
        var c = new ApiCollection { Color = "#C42B1C", Items = { outer, top } };

        Assert.Equal("#2575D6", c.ResolveColor(deep));
        Assert.Equal("#C42B1C", c.ResolveColor(top));

        inner.Color = "#2EA043";
        Assert.Equal("#2EA043", c.ResolveColor(deep));
    }

    [Fact]
    public void NoColourWhenNothingIsColouredOrNodeIsMissing()
    {
        var req = CollectionNode.ForRequest("r", new ApiRequest());
        var c = new ApiCollection { Items = { req } };

        Assert.Null(c.ResolveColor(req));
        c.Color = "#C42B1C";
        Assert.Equal("#C42B1C", c.ResolveColor(null));
    }
}
