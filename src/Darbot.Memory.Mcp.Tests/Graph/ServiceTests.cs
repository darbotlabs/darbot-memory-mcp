using System.Text.Json;
using Darbot.Memory.Mcp.Core.Graph;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using FluentAssertions;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Graph;

public class GraphRegistryTests
{
    private static readonly GraphSchemaRegistry Registry = new(GraphServiceCollectionExtensions.CreateAdapters());

    [Fact]
    public void AllSchemasRegistered()
    {
        Registry.Adapters.Select(a => a.Name).Should().BeEquivalentTo(
            "3dkg", "kgforge", "obsidian", "supermemory", "mcp-memory", "mem0", "graphiti", "letta", "jsonld", "graphml", "cypher", "ntriples", "darbot-kg");
        Registry.Adapters.Where(a => !a.CanImport).Select(a => a.Name).Should().BeEquivalentTo("cypher", "ntriples");
        Registry.Adapters.Should().OnlyContain(a => a.CanExport && a.ContentType.Length > 0 && a.DisplayName.Length > 0);
    }

    [Theory]
    [InlineData("OBSIDIAN", "obsidian")]
    [InlineData("obsidian-vault", "obsidian")]
    [InlineData("nexus-forge", "kgforge")]
    [InlineData("Forge", "kgforge")]
    [InlineData("3d-kg", "3dkg")]
    [InlineData("anthropic-memory", "mcp-memory")]
    [InlineData("zep", "graphiti")]
    [InlineData("memgpt", "letta")]
    [InlineData("rdf", "ntriples")]
    [InlineData("Mem0", "mem0")]
    [InlineData(" graphml ", "graphml")]
    public void Find_ResolvesNamesAndAliases(string input, string expected)
        => Registry.Find(input)!.Name.Should().Be(expected);

    [Fact]
    public void Unknown_ReturnsNullOrThrows()
    {
        Registry.Find("nope").Should().BeNull();
        Registry.Find("").Should().BeNull();
        FluentActions.Invoking(() => Registry.GetRequired("nope")).Should().Throw<ArgumentException>().WithMessage("*Available*");
    }
}

public class GraphMemoryServiceTests : IDisposable
{
    private readonly TempDir _dir = new();
    private readonly GraphMemoryConfiguration _config;
    private readonly GraphMemoryService _service;

    public GraphMemoryServiceTests()
    {
        _config = new GraphMemoryConfiguration { RootPath = _dir.Path, MaxImportBytes = 5_000 };
        _service = new GraphMemoryService(new GraphSchemaRegistry(GraphServiceCollectionExtensions.CreateAdapters()), new JsonFileKnowledgeGraphStore(_dir.Path), _config);
    }

    public void Dispose() => _dir.Dispose();

    [Fact]
    public async Task Remember_IsDeterministicByContent()
    {
        var a = await _service.RememberAsync("g", "Alice prefers tea", scope: "u1", tags: new[] { "#pref", "pref", "drink" });
        var b = await _service.RememberAsync("g", "Alice prefers tea", scope: "u1");
        var c = await _service.RememberAsync("g", "Alice prefers tea", scope: "u2");
        a.Id.Should().Be(b.Id).And.StartWith("mem-");
        c.Id.Should().NotBe(a.Id);
        a.Type.Should().Be("memory");
        a.Content.Should().Be("Alice prefers tea");
        a.Tags.Should().Equal("pref", "drink");
        (await _service.GetGraphAsync("g"))!.Nodes.Should().HaveCount(2);
        await _service.Invoking(s => s.RememberAsync("g", "  ")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Remember_UsesDefaultGraphWhenNameBlank()
    {
        await _service.RememberAsync("", "fact", name: "Fact", type: "insight");
        var g = await _service.GetGraphAsync(_config.DefaultGraph);
        g!.Nodes.Single().Should().Match<GraphNode>(n => n.Type == "insight" && n.Name == "Fact");
    }

    [Fact]
    public async Task Relate_ValidatesNodesAndIsIdempotent()
    {
        var a = await _service.RememberAsync("g", "one");
        var b = await _service.RememberAsync("g", "two");
        var e1 = await _service.RelateAsync("g", a.Id, b.Id, "updates", "two replaces one");
        var e2 = await _service.RelateAsync("g", a.Id, b.Id, "updates");
        e1.Id.Should().Be(e2.Id);
        e1.Fact.Should().Be("two replaces one");
        (await _service.GetGraphAsync("g"))!.Edges.Should().ContainSingle();
        await _service.Invoking(s => s.RelateAsync("g", a.Id, "ghost", "x")).Should().ThrowAsync<KeyNotFoundException>();
        await _service.Invoking(s => s.RelateAsync("g", "ghost", b.Id, "x")).Should().ThrowAsync<KeyNotFoundException>();
        await _service.Invoking(s => s.RelateAsync("g", a.Id, b.Id, " ")).Should().ThrowAsync<ArgumentException>();
    }

    [Fact]
    public async Task Forget_RemovesNodeAndEdges()
    {
        var a = await _service.RememberAsync("g", "one");
        var b = await _service.RememberAsync("g", "two");
        await _service.RelateAsync("g", a.Id, b.Id, "extends");
        (await _service.ForgetAsync("g", a.Id)).Should().BeTrue();
        var g = (await _service.GetGraphAsync("g"))!;
        g.Nodes.Should().ContainSingle().Which.Id.Should().Be(b.Id);
        g.Edges.Should().BeEmpty();
        (await _service.ForgetAsync("g", a.Id)).Should().BeFalse();
    }

    [Fact]
    public async Task SearchAndNeighborhood_Delegate()
    {
        var a = await _service.RememberAsync("g", "espresso machine");
        var b = await _service.RememberAsync("g", "grinder");
        await _service.RelateAsync("g", a.Id, b.Id, "needs");
        (await _service.SearchAsync("g", new GraphQuery { Text = "espresso" })).Single().Id.Should().Be(a.Id);
        (await _service.NeighborhoodAsync("g", a.Id))!.Nodes.Single().Id.Should().Be(b.Id);
    }

    [Fact]
    public async Task Import_MergeAndReplace_AndWarnings()
    {
        const string one = "{\"type\":\"entity\",\"name\":\"A\",\"entityType\":\"t\",\"observations\":[]}\n{\"type\":\"entity\",\"name\":\"B\",\"entityType\":\"t\",\"observations\":[]}";
        const string two = "{\"type\":\"entity\",\"name\":\"C\",\"entityType\":\"t\",\"observations\":[]}\nbroken";
        var first = await _service.ImportAsync("mcp-memory", one, "g");
        first.Nodes.Should().HaveCount(2);
        var merged = await _service.ImportAsync("MCP", two, "g", merge: true);
        merged.Nodes.Should().HaveCount(3);
        merged.Metadata[GraphMemoryService.ImportWarningsKey]!.AsArray().Should().NotBeEmpty();
        (await _service.GetGraphAsync("g"))!.Metadata.Should().NotContainKey(GraphMemoryService.ImportWarningsKey);
        var replaced = await _service.ImportAsync("mcp-memory", two, "g", merge: false);
        replaced.Nodes.Should().ContainSingle();
    }

    [Fact]
    public async Task Import_Rejects_TooLarge_Unknown_And_ExportOnly()
    {
        await _service.Invoking(s => s.ImportAsync("mcp-memory", new string('x', 6_000), "g")).Should().ThrowAsync<ArgumentException>().WithMessage("*limit*");
        await _service.Invoking(s => s.ImportAsync("nope", "{}", "g")).Should().ThrowAsync<ArgumentException>();
        await _service.Invoking(s => s.ImportAsync("cypher", "{}", "g")).Should().ThrowAsync<NotSupportedException>();
        await _service.Invoking(s => s.ExportAsync("darbot-kg", "missing")).Should().ThrowAsync<KeyNotFoundException>();
    }

    [Fact]
    public async Task Export_ConvertsBetweenSchemas()
    {
        await _service.ImportAsync("mcp-memory", "{\"type\":\"entity\",\"name\":\"A\",\"entityType\":\"t\",\"observations\":[\"o\"]}", "g");
        var export = await _service.ExportAsync("obsidian", "g");
        export.Files.Should().ContainKey("A.md");
        var native = await _service.ExportAsync("darbot-kg", "g");
        JsonSerializer.Deserialize<KnowledgeGraph>(native.Content!, GraphJson.Options)!.Nodes.Should().ContainSingle();
    }

    [Fact]
    public void DependencyInjection_RegistersEverything()
    {
        var settings = new Dictionary<string, string?> { ["Darbot:Graph:RootPath"] = "graphs-di", ["Darbot:Graph:DefaultGraph"] = "mine" };
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
        var services = new ServiceCollection();
        services.AddGraphMemory(configuration);
        using var sp = services.BuildServiceProvider();
        var cfg = sp.GetRequiredService<GraphMemoryConfiguration>();
        Path.IsPathRooted(cfg.RootPath).Should().BeTrue();
        cfg.RootPath.Should().EndWith("graphs-di");
        cfg.DefaultGraph.Should().Be("mine");
        sp.GetRequiredService<IGraphMemoryService>().Schemas.Should().HaveCount(13);
        sp.GetRequiredService<IGraphSchemaRegistry>().Find("zep").Should().NotBeNull();
        sp.GetServices<IGraphSchemaAdapter>().Should().HaveCount(13);
    }
}
