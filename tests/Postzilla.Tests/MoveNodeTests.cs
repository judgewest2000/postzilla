using Postzilla.Models;

namespace Postzilla.Tests;

public class MoveNodeTests
{
    private static CollectionNode Req(string name) => CollectionNode.ForRequest(name, new ApiRequest());

    [Fact]
    public void MovesRequestIntoAnotherCollection()
    {
        var req = Req("a");
        var from = new ApiCollection { Items = { req, Req("b") } };
        var to = new ApiCollection { Items = { Req("x") } };

        Assert.True(ApiCollection.MoveNode(from, req, to, to.Items, to.Items.Count));

        Assert.Equal(["b"], from.Items.Select(n => n.Name));
        Assert.Equal(["x", "a"], to.Items.Select(n => n.Name));
    }

    [Fact]
    public void ReordersDownwardWithinSameList()
    {
        var a = Req("a");
        var c = new ApiCollection { Items = { a, Req("b"), Req("c") } };

        Assert.True(ApiCollection.MoveNode(c, a, c, c.Items, 2));

        Assert.Equal(["b", "a", "c"], c.Items.Select(n => n.Name));
    }

    [Fact]
    public void RejectsNoOpAndMovingFolderIntoItself()
    {
        var inner = CollectionNode.Folder("inner");
        var folder = CollectionNode.Folder("f");
        folder.Children.Add(inner);
        var c = new ApiCollection { Items = { folder, Req("b") } };

        Assert.False(ApiCollection.MoveNode(c, folder, c, c.Items, 1));
        Assert.False(ApiCollection.MoveNode(c, folder, c, inner.Children, 0));
        Assert.Equal(["f", "b"], c.Items.Select(n => n.Name));
    }

    [Fact]
    public void RejectsTargetListOutsideDestinationCollection()
    {
        var req = Req("a");
        var from = new ApiCollection { Items = { req } };
        var other = new ApiCollection();

        Assert.False(ApiCollection.MoveNode(from, req, new ApiCollection(), other.Items, 0));
        Assert.Single(from.Items);
    }
}
