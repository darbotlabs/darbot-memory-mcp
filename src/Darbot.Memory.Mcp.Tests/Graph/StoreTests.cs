using Darbot.Memory.Mcp.Core.Graph;
using FluentAssertions;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Graph;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "darbot-graph-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class KnowledgeGraphStoreTests : IDisposable
{
    private readonly TempDir _dir = new();

    public void Dispose() => _dir.Dispose();

    private JsonFileKnowledgeGraphStore NewStore() => new(_dir.Path);

    private static GraphNode Node(string id, string name, Action<GraphNodeBuilder>? configure = null)
    {
        var b = new GraphNodeBuilder(id, name);
        configure?.Invoke(b);
        return b.Build();
    }

    private sealed class GraphNodeBuilder(string id, string name)
    {
        public string Type = "entity";
        public string? Description, Content, Scope;
        public List<string> Tags = new(), Aliases = new(), Observations = new();
        public bool IsLatest = true;
        public DateTimeOffset? ValidFrom, ValidTo;

        public GraphNode Build() => new()
        {
            Id = id, Name = name, Type = Type, Description = Description, Content = Content, Scope = Scope, Tags = Tags,
            Aliases = Aliases, Observations = Observations, IsLatest = IsLatest, ValidFrom = ValidFrom, ValidTo = ValidTo
        };
    }

    [Fact]
    public async Task Persists_AcrossInstances_AndWritesAtomicFile()
    {
        var store = NewStore();
        await store.UpsertNodeAsync("My Graph", Node("a", "Alpha"));
        await store.UpsertNodeAsync("My Graph", Node("b", "Beta"));
        await store.UpsertEdgeAsync("My Graph", new GraphEdge { Id = "e", SourceId = "a", TargetId = "b" });

        File.Exists(Path.Combine(_dir.Path, "my_graph.kg.json")).Should().BeTrue();
        Directory.GetFiles(_dir.Path, "*.tmp").Should().BeEmpty();

        var reloaded = await NewStore().GetAsync("my graph");
        reloaded.Should().NotBeNull();
        reloaded!.Name.Should().Be("My Graph");
        reloaded.Nodes.Should().HaveCount(2);
        reloaded.Edges.Should().ContainSingle().Which.Type.Should().Be("related_to");
        (await NewStore().ListAsync()).Should().HaveCount(1);
    }

    [Fact]
    public async Task Merge_ReplacesById_AndAddsNew()
    {
        var store = NewStore();
        await store.MergeAsync("g", new KnowledgeGraph { Nodes = { Node("a", "Old"), Node("b", "B") }, Metadata = { ["k"] = System.Text.Json.Nodes.JsonValue.Create(1) } });
        var merged = await store.MergeAsync("g", new KnowledgeGraph { Nodes = { Node("a", "New"), Node("c", "C") } });
        merged.Nodes.Should().HaveCount(3);
        merged.Nodes.Single(n => n.Id == "a").Name.Should().Be("New");
        merged.Metadata.Should().ContainKey("k");
        merged.Name.Should().Be("g");
    }

    [Fact]
    public async Task Save_ReplacesWholeGraph_AndDeleteRemovesFile()
    {
        var store = NewStore();
        await store.SaveAsync(new KnowledgeGraph { Name = "g", Nodes = { Node("a", "A"), Node("b", "B") } });
        await store.SaveAsync(new KnowledgeGraph { Name = "g", Nodes = { Node("z", "Z") } });
        (await store.GetAsync("g"))!.Nodes.Should().ContainSingle();
        (await store.DeleteAsync("g")).Should().BeTrue();
        (await store.DeleteAsync("g")).Should().BeFalse();
        (await store.GetAsync("g")).Should().BeNull();
        Directory.GetFiles(_dir.Path, "*.kg.json").Should().BeEmpty();
    }

    [Fact]
    public async Task Search_TextFiltersAndRanking()
    {
        var store = NewStore();
        await store.UpsertNodeAsync("g", Node("1", "Coffee", b => { b.Type = "food"; b.Tags.Add("drink"); b.Scope = "u1"; }));
        await store.UpsertNodeAsync("g", Node("2", "Tea", b => { b.Type = "food"; b.Description = "Like coffee but leafy"; b.Scope = "u2"; }));
        await store.UpsertNodeAsync("g", Node("3", "Note", b => { b.Content = "remember COFFEE beans"; b.Observations.Add("x"); }));
        await store.UpsertNodeAsync("g", Node("4", "Old coffee", b => b.IsLatest = false));

        var hits = await store.SearchNodesAsync("g", new GraphQuery { Text = "coffee" });
        hits.Select(h => h.Id).Should().Equal("1", "2", "3");

        (await store.SearchNodesAsync("g", new GraphQuery { Text = "coffee", LatestOnly = false })).Should().HaveCount(4);
        (await store.SearchNodesAsync("g", new GraphQuery { Type = "food" })).Should().HaveCount(2);
        (await store.SearchNodesAsync("g", new GraphQuery { Scope = "u2" })).Single().Id.Should().Be("2");
        (await store.SearchNodesAsync("g", new GraphQuery { Tag = "#drink" })).Single().Id.Should().Be("1");
        (await store.SearchNodesAsync("g", new GraphQuery { Text = "coffee leafy" })).Single().Id.Should().Be("2");
        (await store.SearchNodesAsync("g", new GraphQuery { Limit = 1 })).Should().HaveCount(1);
        (await store.SearchNodesAsync("missing", new GraphQuery())).Should().BeEmpty();
    }

    [Fact]
    public async Task Search_AsOfHonoursValidityWindow()
    {
        var store = NewStore();
        await store.UpsertNodeAsync("g", Node("old", "Job", b => { b.ValidFrom = DateTimeOffset.Parse("2010-01-01Z"); b.ValidTo = DateTimeOffset.Parse("2015-01-01Z"); }));
        await store.UpsertNodeAsync("g", Node("new", "Job", b => b.ValidFrom = DateTimeOffset.Parse("2015-01-01Z")));
        await store.UpsertNodeAsync("g", Node("always", "Job"));

        (await store.SearchNodesAsync("g", new GraphQuery { Text = "job", AsOf = DateTimeOffset.Parse("2012-06-01Z") })).Select(n => n.Id).Should().BeEquivalentTo("old", "always");
        (await store.SearchNodesAsync("g", new GraphQuery { Text = "job", AsOf = DateTimeOffset.Parse("2015-01-01Z") })).Select(n => n.Id).Should().BeEquivalentTo("new", "always");
        (await store.SearchNodesAsync("g", new GraphQuery { Text = "job", AsOf = DateTimeOffset.Parse("2005-01-01Z") })).Select(n => n.Id).Should().BeEquivalentTo("always");
    }

    [Fact]
    public async Task Neighborhood_IsBreadthFirstAndCappedAtFour()
    {
        var store = NewStore();
        for (var i = 0; i < 7; i++)
        {
            await store.UpsertNodeAsync("g", Node($"n{i}", $"N{i}"));
            if (i > 0)
            {
                await store.UpsertEdgeAsync("g", new GraphEdge { Id = $"e{i}", SourceId = $"n{i - 1}", TargetId = $"n{i}" });
            }
        }
        var one = await store.GetNeighborhoodAsync("g", "n3", 1);
        one!.Root.Id.Should().Be("n3");
        one.Nodes.Select(n => n.Id).Should().BeEquivalentTo("n2", "n4");
        one.Edges.Should().HaveCount(2);

        var big = await store.GetNeighborhoodAsync("g", "n0", 10);
        big!.Nodes.Should().HaveCount(4);
        big.Edges.Should().HaveCount(4);

        (await store.GetNeighborhoodAsync("g", "nope")).Should().BeNull();
        (await store.GetNeighborhoodAsync("nope", "n0")).Should().BeNull();
    }

    [Fact]
    public async Task DeleteNode_RemovesTouchingEdges()
    {
        var store = NewStore();
        await store.UpsertNodeAsync("g", Node("a", "A"));
        await store.UpsertNodeAsync("g", Node("b", "B"));
        await store.UpsertNodeAsync("g", Node("c", "C"));
        await store.UpsertEdgeAsync("g", new GraphEdge { Id = "1", SourceId = "a", TargetId = "b" });
        await store.UpsertEdgeAsync("g", new GraphEdge { Id = "2", SourceId = "b", TargetId = "c" });
        await store.UpsertEdgeAsync("g", new GraphEdge { Id = "3", SourceId = "a", TargetId = "c" });
        (await store.DeleteNodeAsync("g", "b")).Should().BeTrue();
        (await store.DeleteNodeAsync("g", "b")).Should().BeFalse();
        var g = (await NewStore().GetAsync("g"))!;
        g.Nodes.Should().HaveCount(2);
        g.Edges.Select(e => e.Id).Should().Equal("3");
    }

    [Fact]
    public async Task ConcurrentUpserts_AreThreadSafe()
    {
        var store = NewStore();
        await Task.WhenAll(Enumerable.Range(0, 60).Select(i => Task.Run(() => store.UpsertNodeAsync("g", Node($"n{i}", $"N{i}")))));
        (await store.GetAsync("g"))!.Nodes.Should().HaveCount(60);
        (await NewStore().GetAsync("g"))!.Nodes.Should().HaveCount(60);
    }

    [Fact]
    public async Task GetAsync_ReturnsSnapshot_NotLiveState()
    {
        var store = NewStore();
        await store.UpsertNodeAsync("g", Node("a", "A"));
        var snapshot = (await store.GetAsync("g"))!;
        snapshot.Nodes.Clear();
        (await store.GetAsync("g"))!.Nodes.Should().HaveCount(1);
    }

    [Theory]
    [InlineData("../evil", "_evil")]
    [InlineData("A b", "a_b")]
    [InlineData("", "default")]
    [InlineData(null, "default")]
    public void SafeName_Sanitizes(string? input, string expected)
        => JsonFileKnowledgeGraphStore.SafeName(input).Should().Be(expected);
}
