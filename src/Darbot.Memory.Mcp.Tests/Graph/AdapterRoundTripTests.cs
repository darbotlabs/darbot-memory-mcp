using System.Text.Json;
using System.Text.Json.Nodes;
using Darbot.Memory.Mcp.Core.Graph;
using FluentAssertions;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Graph;

public class AdapterRoundTripTests
{
    private static readonly GraphSchemaRegistry Registry = new(GraphServiceCollectionExtensions.CreateAdapters());

    private const string ThreeDkg = """
    {"id":"g1","name":"Demo","version":"1.2.0",
     "entities":[
       {"id":"e1","type":"person","name":"Ada Lovelace","description":"Mathematician","aliases":["Countess of Lovelace"],
        "properties":{"born":1815,"field":"math"},"position":{"x":1,"y":2,"z":3},"cluster":"c1","source":{"documentId":"d1"},"createdAt":"2024-01-01T00:00:00Z"},
       {"id":"e2","type":"concept","name":"Analytical Engine","cluster":"c1"}],
     "relationships":[{"id":"r1","type":"worked_on","sourceId":"e1","targetId":"e2","weight":0.9,"label":"worked on","bidirectional":false}],
     "clusters":[{"id":"c1","name":"Computing","color":"#ffffff"}],
     "flashcards":[{"id":"f1","title":"Note","content":"Body","agentId":"a1","tags":["x"],"zone":"workspace"}],
     "semanticPaths":[{"id":"sp1"}],"viewState":{"zoom":2}}
    """;

    private const string ThreeDkgTriples = """
    [{"subject":"Ada","predicate":"worked_with","object":"Babbage","subjectType":"person","confidence":0.8,"source":"wiki"},
     {"subject":"Babbage","predicate":"designed","object":"Difference Engine"}]
    """;

    private const string KgForge = """
    {"@context":{"@vocab":"https://schema.org/"},"@graph":[
      {"@id":"https://x.org/p/alice","@type":"Person","name":"Alice","knows":{"@id":"https://x.org/p/bob"},"worksFor":{"@id":"https://x.org/o/acme"}},
      {"@id":"https://x.org/p/bob","@type":"Person","name":"Bob","email":"bob@x.org"},
      {"@id":"https://x.org/o/acme","@type":"Organization","name":"Acme","description":"Company"}]}
    """;

    private const string Supermemory = """
    {"documents":[{"id":"doc1","customId":"c1","title":"Spec","content":"Long text","summary":"S","type":"text","status":"done","containerTags":["proj"],"metadata":{"k":"v"},"createdAt":"2024-05-01T10:00:00Z"}],
     "memories":[
       {"id":"m1","memory":"User likes tea","spaceId":"proj","version":1,"isLatest":false,"memoryRelations":{}},
       {"id":"m2","memory":"User likes green tea","spaceId":"proj","version":2,"isLatest":true,"parentMemoryId":"m1","rootMemoryId":"m1","isInference":false,"memoryRelations":{"m1":"updates"}}]}
    """;

    private const string McpMemory = """
    {"type":"entity","name":"John_Smith","entityType":"person","observations":["Speaks fluent Spanish","Graduated in 2019"]}
    {"type":"entity","name":"Anthropic","entityType":"organization","observations":[]}
    {"type":"relation","from":"John_Smith","to":"Anthropic","relationType":"works_at"}
    """;

    private const string Mem0 = """
    {"results":[
       {"id":"mem-1","memory":"Likes pizza","hash":"h1","metadata":{"a":1},"user_id":"alice","categories":["food"],"created_at":"2024-01-01T00:00:00-07:00","updated_at":null,"score":0.9},
       {"id":"mem-2","memory":"Works at Acme","user_id":"alice","agent_id":"bot"}],
     "relations":[
       {"source":"alice","relationship":"works_at","destination":"acme"},
       {"source":"alice","source_type":"person","relationship":"likes","destination":"pizza","destination_type":"food"}]}
    """;

    private const string Graphiti = """
    {"nodes":[
       {"uuid":"n1","name":"Kamala Harris","labels":["Entity","Person"],"summary":"Politician","group_id":"g","created_at":"2024-01-01T00:00:00Z","attributes":{"party":"D"}},
       {"uuid":"n2","name":"California","labels":["Entity"],"summary":"State","group_id":"g"}],
     "edges":[{"uuid":"e1","source_node_uuid":"n1","target_node_uuid":"n2","name":"WAS_ATTORNEY_GENERAL_OF","fact":"Kamala Harris was AG of California",
        "valid_at":"2011-01-03T00:00:00Z","invalid_at":"2017-01-03T00:00:00Z","expired_at":null,"created_at":"2024-01-01T00:00:00Z","episodes":["ep1"]}],
     "episodes":[{"uuid":"ep1","name":"Episode 1","content":"Kamala was AG","source":"text","source_description":"news","valid_at":"2024-01-01T00:00:00Z"}]}
    """;

    private const string Letta = """
    {"blocks":[{"id":"block-1","label":"human","value":"Name: Sam","limit":2000,"description":"about user","read_only":false},
               {"label":"persona","value":"I am helpful"}],
     "passages":[{"id":"passage-1","text":"Sam likes hiking","embedding":[0.5,0.25],"metadata":{"src":"chat"},"created_at":"2024-02-01T00:00:00Z"}]}
    """;

    private const string JsonLd = """
    {"@context":"https://schema.org","@graph":[
      {"@id":"urn:a","@type":"Person","name":"Ann","sameAs":"https://x.org/ann","knows":{"@id":"urn:b"}},
      {"@id":"urn:b","@type":"Person","name":"Ben","alternateName":["Benny"]}]}
    """;

    private const string GraphMl = """
    <?xml version="1.0" encoding="UTF-8"?>
    <graphml xmlns="http://graphml.graphdrawing.org/xmlns">
      <key id="d0" for="node" attr.name="name" attr.type="string"/>
      <key id="d1" for="node" attr.name="color" attr.type="string"/>
      <key id="d2" for="edge" attr.name="weight" attr.type="double"/>
      <graph id="G" edgedefault="directed">
        <node id="n0"><data key="d0">Alpha</data><data key="d1">red</data></node>
        <node id="n1"><data key="d0">Beta</data></node>
        <edge id="e0" source="n0" target="n1"><data key="d2">1.5</data></edge>
      </graph>
    </graphml>
    """;

    public static string ObsidianVault() => JsonSerializer.Serialize(new Dictionary<string, string>
    {
        ["People/Alice.md"] = "---\nid: alice\ntype: person\naliases: [Al]\ntags:\n  - team\ncreated: 2024-01-02\nrole: engineer\n---\nAlice works with [[Bob|my friend]] on #project.\n\n## Observations\n- Likes tea\n\n## Relations\n- manager_of:: [[Bob]]\n",
        ["Bob.md"] = "---\ntype: person\n---\nBob's note.\nreports_to:: [[Alice#Intro]]\n\n## Observations\n- Joined 2023\n",
        ["Board.canvas"] = """{"nodes":[{"id":"alice","type":"text","x":10,"y":20,"width":250,"height":60,"text":"Alice"},{"id":"cX","type":"text","x":300,"y":20,"width":250,"height":60,"text":"Loose idea"}],"edges":[{"id":"e1","fromNode":"alice","toNode":"cX","label":"inspires"}]}"""
    });

    public static IEnumerable<object[]> Fixtures()
    {
        yield return new object[] { "3dkg", ThreeDkg, 3, 1 };
        yield return new object[] { "3dkg", ThreeDkgTriples, 3, 2 };
        yield return new object[] { "kgforge", KgForge, 3, 2 };
        yield return new object[] { "supermemory", Supermemory, 3, 1 };
        yield return new object[] { "mcp-memory", McpMemory, 2, 1 };
        yield return new object[] { "mem0", Mem0, 5, 2 };
        yield return new object[] { "graphiti", Graphiti, 3, 1 };
        yield return new object[] { "letta", Letta, 3, 0 };
        yield return new object[] { "jsonld", JsonLd, 2, 1 };
        yield return new object[] { "graphml", GraphMl, 2, 1 };
        yield return new object[] { "obsidian", ObsidianVault(), 3, 4 };
    }

    private static string ExportPayload(Core.Graph.GraphExportResult export)
        => export.Files is null ? export.Content! : JsonSerializer.Serialize(export.Files);

    [Theory]
    [MemberData(nameof(Fixtures))]
    public void Import_Export_Import_PreservesCounts(string schema, string payload, int nodes, int edges)
    {
        var adapter = Registry.GetRequired(schema);
        var first = adapter.Import(payload, "t");
        first.Graph.Nodes.Should().HaveCount(nodes);
        first.Graph.Edges.Should().HaveCount(edges);
        first.Graph.Nodes.Select(n => n.Id).Should().OnlyHaveUniqueItems();

        var exported = adapter.Export(first.Graph);
        exported.Schema.Should().Be(adapter.Name);
        var second = adapter.Import(ExportPayload(exported), "t");
        second.Graph.Nodes.Should().HaveCount(nodes);
        second.Graph.Edges.Should().HaveCount(edges);
        second.Graph.Nodes.Select(n => n.Id).Should().BeEquivalentTo(first.Graph.Nodes.Select(n => n.Id));
        second.Graph.Nodes.Select(n => n.Name).Should().BeEquivalentTo(first.Graph.Nodes.Select(n => n.Name));
    }

    [Fact]
    public void ThreeDkg_MapsFieldsAndKeepsMetadata()
    {
        var g = Registry.GetRequired("3dkg").Import(ThreeDkg, null).Graph;
        g.Name.Should().Be("Demo");
        g.Id.Should().Be("g1");
        var ada = g.Nodes.Single(n => n.Id == "e1");
        ada.Type.Should().Be("person");
        ada.Aliases.Should().Contain("Countess of Lovelace");
        ada.Position.Should().Be(new GraphPosition(1, 2, 3));
        ada.Cluster.Should().Be("c1");
        ada.Properties["born"]!.GetValue<int>().Should().Be(1815);
        ada.Properties.Should().ContainKey("_source");
        g.Edges.Single().Weight.Should().Be(0.9);
        g.Clusters.Single().NodeIds.Should().BeEquivalentTo("e1", "e2");
        g.Nodes.Single(n => n.Type == "flashcard").Scope.Should().Be("workspace");
        g.Metadata.Should().ContainKeys("semanticPaths", "viewState", "version");

        var exported = JsonNode.Parse(Registry.GetRequired("3dkg").Export(g).Content!)!;
        exported["entities"]!.AsArray().Should().HaveCount(2);
        exported["entities"]![0]!["source"]!["documentId"]!.GetValue<string>().Should().Be("d1");
        exported["viewState"]!["zoom"]!.GetValue<int>().Should().Be(2);
        exported["flashcards"]!.AsArray().Should().HaveCount(1);
    }

    [Fact]
    public void ThreeDkg_SchemaPathPattern_BecomesNode()
    {
        const string pattern = """{"id":"p1","name":"Mail to Teams","scenario":"S","steps":[{"id":"s1"}],"tags":["m365"]}""";
        var g = Registry.GetRequired("3dkg").Import(pattern, "x").Graph;
        var node = g.Nodes.Single();
        node.Type.Should().Be("schema_path_pattern");
        node.Properties.Should().ContainKey("steps");
        var again = Registry.GetRequired("3dkg").Import(Registry.GetRequired("3dkg").Export(g).Content!, "x").Graph;
        again.Nodes.Single().Type.Should().Be("schema_path_pattern");
    }

    [Fact]
    public void ThreeDkg_Triples_ImportEdgesWithConfidence()
    {
        var g = Registry.GetRequired("3dkg").Import(ThreeDkgTriples, "x").Graph;
        g.Edges.First().Confidence.Should().Be(0.8);
        g.Edges.First().Source.Should().Be("wiki");
        g.Nodes.Single(n => n.Name == "Ada").Type.Should().Be("person");
    }

    [Fact]
    public void KgForge_LinksBecomeEdges_AndRichEdgesAreReified()
    {
        var adapter = Registry.GetRequired("kgforge");
        var g = adapter.Import(KgForge, "x").Graph;
        g.Edges.Select(e => e.Type).Should().BeEquivalentTo("knows", "worksFor");
        g.Nodes.Single(n => n.Name == "Bob").Properties["email"]!.GetValue<string>().Should().Be("bob@x.org");

        var rich = g with
        {
            Edges = g.Edges.Concat(new[]
            {
                new GraphEdge { Id = "rich", SourceId = "https://x.org/p/bob", TargetId = "https://x.org/o/acme", Type = "employedBy", Fact = "since 2020", Weight = 0.5 }
            }).ToList()
        };
        var back = adapter.Import(adapter.Export(rich).Content!, "x").Graph;
        back.Edges.Should().HaveCount(3);
        var edge = back.Edges.Single(e => e.Id == "rich");
        edge.Fact.Should().Be("since 2020");
        edge.Weight.Should().Be(0.5);
        JsonNode.Parse(adapter.Export(rich).Content!)!["@context"].Should().NotBeNull();
    }

    [Fact]
    public void KgForge_AcceptsPlainArrayAndIdTypeAliases()
    {
        const string payload = """[{"id":"a","type":"Dataset","label":"Data A","derivation":{"@id":"b"}},{"id":"b","type":"Dataset","label":"Data B"}]""";
        var g = Registry.GetRequired("kgforge").Import(payload, "x").Graph;
        g.Nodes.Should().HaveCount(2);
        g.Nodes.Single(n => n.Id == "a").Name.Should().Be("Data A");
        g.Edges.Single().Type.Should().Be("derivation");
    }

    [Fact]
    public void Supermemory_MapsVersionsAndRelations()
    {
        var g = Registry.GetRequired("supermemory").Import(Supermemory, "x").Graph;
        g.Nodes.Single(n => n.Id == "doc1").Type.Should().Be("document");
        g.Nodes.Single(n => n.Id == "doc1").Content.Should().Be("Long text");
        g.Nodes.Single(n => n.Id == "m1").IsLatest.Should().BeFalse();
        g.Nodes.Single(n => n.Id == "m2").Version.Should().Be(2);
        g.Nodes.Single(n => n.Id == "m2").Scope.Should().Be("proj");
        g.Edges.Single().Type.Should().Be("updates");

        var exported = JsonNode.Parse(Registry.GetRequired("supermemory").Export(g).Content!)!;
        exported["memories"]![1]!["memoryRelations"]!["m1"]!.GetValue<string>().Should().Be("updates");
        exported["documents"]![0]!["type"]!.GetValue<string>().Should().Be("text");
    }

    [Fact]
    public void Supermemory_AcceptsBareArrayAndGraphStyle()
    {
        var bare = Registry.GetRequired("supermemory").Import("""[{"id":"m","memory":"x"},{"id":"d","title":"T","content":"c"}]""", "x").Graph;
        bare.Nodes.Select(n => n.Type).Should().BeEquivalentTo("memory", "document");
        var style = Registry.GetRequired("supermemory").Import("""{"nodes":[{"id":"1","type":"document","title":"A"},{"id":"2","type":"memory","label":"B"}],"edges":[{"source":"1","target":"2","type":"derives"}]}""", "x").Graph;
        style.Nodes.Should().HaveCount(2);
        style.Edges.Single().Type.Should().Be("derives");
    }

    [Fact]
    public void McpMemory_ObjectFormAndDeterministicIds()
    {
        var adapter = Registry.GetRequired("mcp-memory");
        var a = adapter.Import(McpMemory, "x").Graph;
        var b = adapter.Import("""{"entities":[{"name":"John_Smith","entityType":"person","observations":[]},{"name":"Anthropic","entityType":"organization","observations":[]}],"relations":[{"from":"John_Smith","to":"Anthropic","relationType":"works_at"}]}""", "x").Graph;
        a.Nodes.Select(n => n.Id).Should().BeEquivalentTo(b.Nodes.Select(n => n.Id));
        a.Edges.Single().Id.Should().Be(b.Edges.Single().Id);
        a.Nodes.Single(n => n.Name == "John_Smith").Observations.Should().HaveCount(2);
        var lines = adapter.Export(a).Content!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().HaveCount(3);
        lines.Last().Should().Contain("\"relationType\":\"works_at\"");
    }

    [Fact]
    public void McpMemory_MalformedLineIsSkippedWithWarning()
    {
        var result = Registry.GetRequired("mcp-memory").Import("{\"type\":\"entity\",\"name\":\"A\",\"entityType\":\"t\",\"observations\":[]}\nnot json\n{\"type\":\"relation\",\"from\":\"A\",\"to\":\"Ghost\",\"relationType\":\"r\"}", "x");
        result.Graph.Nodes.Should().HaveCount(2);
        result.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void Mem0_ScopeAndProperties()
    {
        var g = Registry.GetRequired("mem0").Import(Mem0, "x").Graph;
        var m1 = g.Nodes.Single(n => n.Id == "mem-1");
        m1.Scope.Should().Be("alice");
        m1.Tags.Should().Contain("food");
        m1.Properties["hash"]!.GetValue<string>().Should().Be("h1");
        m1.CreatedAt.Should().Be(DateTimeOffset.Parse("2024-01-01T00:00:00-07:00"));
        g.Nodes.Single(n => n.Id == "mem-2").Properties["agent_id"]!.GetValue<string>().Should().Be("bot");
        g.Nodes.Single(n => n.Name == "pizza").Type.Should().Be("food");
    }

    [Fact]
    public void Mem0_AcceptsBareArray()
    {
        var g = Registry.GetRequired("mem0").Import("""[{"id":"1","memory":"a","run_id":"r1"}]""", "x").Graph;
        g.Nodes.Single().Scope.Should().Be("r1");
    }

    [Fact]
    public void Graphiti_MapsTemporalFields()
    {
        var adapter = Registry.GetRequired("graphiti");
        var g = adapter.Import(Graphiti, "x").Graph;
        var edge = g.Edges.Single();
        edge.Fact.Should().Contain("AG");
        edge.ValidFrom.Should().Be(DateTimeOffset.Parse("2011-01-03T00:00:00Z"));
        edge.ValidTo.Should().Be(DateTimeOffset.Parse("2017-01-03T00:00:00Z"));
        edge.ExpiredAt.Should().BeNull();
        var kamala = g.Nodes.Single(n => n.Id == "n1");
        kamala.Type.Should().Be("Person");
        kamala.Scope.Should().Be("g");
        kamala.Description.Should().Be("Politician");
        var ep = g.Nodes.Single(n => n.Type == "episode");
        ep.Source.Should().Be("text");
        ep.Content.Should().Be("Kamala was AG");

        var exported = JsonNode.Parse(adapter.Export(g).Content!)!;
        exported["edges"]![0]!["invalid_at"].Should().NotBeNull();
        exported["episodes"]!.AsArray().Should().HaveCount(1);
        exported["nodes"]![0]!["attributes"]!["party"]!.GetValue<string>().Should().Be("D");
    }

    [Fact]
    public void Letta_BlocksAndPassages()
    {
        var adapter = Registry.GetRequired("letta");
        var g = adapter.Import(Letta, "x").Graph;
        var human = g.Nodes.Single(n => n.Name == "human");
        human.Type.Should().Be("memory_block");
        human.Content.Should().Be("Name: Sam");
        human.Properties["limit"]!.GetValue<int>().Should().Be(2000);
        g.Nodes.Single(n => n.Name == "persona").Id.Should().Be("block-persona");
        var passage = g.Nodes.Single(n => n.Type == "passage");
        passage.Embedding.Should().Equal(0.5f, 0.25f);
        var exported = JsonNode.Parse(adapter.Export(g).Content!)!;
        exported["blocks"]!.AsArray().Should().HaveCount(2);
        exported["passages"]![0]!["text"]!.GetValue<string>().Should().Be("Sam likes hiking");
    }

    [Fact]
    public void JsonLd_KeepsSameAsAndAliases()
    {
        var adapter = Registry.GetRequired("jsonld");
        var g = adapter.Import(JsonLd, "x").Graph;
        g.Nodes.Single(n => n.Id == "urn:a").Properties["sameAs"]!.GetValue<string>().Should().Be("https://x.org/ann");
        g.Nodes.Single(n => n.Id == "urn:b").Aliases.Should().Contain("Benny");
        g.Edges.Single().Type.Should().Be("knows");
        var doc = JsonNode.Parse(adapter.Export(g).Content!)!;
        doc["@graph"]![0]!["knows"]!["@id"]!.GetValue<string>().Should().Be("urn:b");
        adapter.ContentType.Should().Be("application/ld+json");
    }

    [Fact]
    public void GraphMl_ForeignKeysBecomeProperties_AndRichExportRoundTrips()
    {
        var adapter = Registry.GetRequired("graphml");
        var g = adapter.Import(GraphMl, "x").Graph;
        g.Nodes.Single(n => n.Id == "n0").Properties["color"]!.GetValue<string>().Should().Be("red");
        g.Edges.Single().Weight.Should().Be(1.5);
        g.Edges.Single().Type.Should().Be("related_to");

        var rich = new KnowledgeGraph
        {
            Name = "rich",
            Nodes =
            {
                new GraphNode { Id = "a", Name = "A <&> \"q\"", Type = "person", Tags = { "t1" }, Observations = { "o1" }, Position = new GraphPosition(1, 2, 3), ValidFrom = DateTimeOffset.Parse("2020-01-01T00:00:00Z"), Version = 3, IsLatest = false, Properties = { ["k"] = JsonValue.Create(5) } },
                new GraphNode { Id = "b", Name = "B" }
            },
            Edges = { new GraphEdge { Id = "e", SourceId = "a", TargetId = "b", Type = "knows", Fact = "f", Weight = 2, Bidirectional = true } }
        };
        var back = adapter.Import(adapter.Export(rich).Content!, null).Graph;
        back.Name.Should().Be("rich");
        var a = back.Nodes.Single(n => n.Id == "a");
        a.Name.Should().Be("A <&> \"q\"");
        a.Tags.Should().Equal("t1");
        a.Observations.Should().Equal("o1");
        a.Position.Should().Be(new GraphPosition(1, 2, 3));
        a.Version.Should().Be(3);
        a.IsLatest.Should().BeFalse();
        a.Properties["k"]!.GetValue<int>().Should().Be(5);
        var e = back.Edges.Single();
        (e.Fact, e.Weight, e.Bidirectional, e.Type).Should().Be(("f", 2d, true, "knows"));
    }

    [Fact]
    public void Obsidian_ParsesFrontmatterLinksTagsFieldsAndCanvas()
    {
        var g = Registry.GetRequired("obsidian").Import(ObsidianVault(), "vault").Graph;
        var alice = g.Nodes.Single(n => n.Id == "alice");
        alice.Type.Should().Be("person");
        alice.Aliases.Should().Contain("Al");
        alice.Tags.Should().BeEquivalentTo("team", "project");
        alice.Observations.Should().Equal("Likes tea");
        alice.Properties["role"]!.GetValue<string>().Should().Be("engineer");
        alice.CreatedAt.Should().Be(DateTimeOffset.Parse("2024-01-02T00:00:00Z"));
        alice.Position.Should().Be(new GraphPosition(10, 20, 0));
        alice.Content.Should().Contain("[[Bob|my friend]]");

        g.Edges.Should().Contain(e => e.SourceId == "alice" && e.TargetId == "bob" && e.Type == "links_to");
        g.Edges.Should().Contain(e => e.SourceId == "alice" && e.TargetId == "bob" && e.Type == "manager_of");
        g.Edges.Should().Contain(e => e.SourceId == "bob" && e.TargetId == "alice" && e.Type == "reports_to");
        g.Edges.Should().Contain(e => e.SourceId == "alice" && e.TargetId == "canvas-cX" && e.Type == "inspires");
    }

    [Fact]
    public void Obsidian_ExportProducesNotesAndCanvas()
    {
        var adapter = Registry.GetRequired("obsidian");
        var g = adapter.Import(ObsidianVault(), "vault").Graph;
        var export = adapter.Export(g);
        export.Content.Should().BeNull();
        export.Files!.Keys.Should().Contain(new[] { "Alice.md", "Bob.md", "vault.canvas" });
        var note = export.Files["Alice.md"];
        note.Should().StartWith("---\nid: \"alice\"");
        note.Should().Contain("## Observations\n- Likes tea");
        note.Should().Contain("- manager_of:: [[Bob]]");
        var canvas = JsonNode.Parse(export.Files["vault.canvas"])!;
        canvas["nodes"]!.AsArray().Should().HaveCount(3);
        canvas["nodes"]![0]!["type"]!.GetValue<string>().Should().Be("text");
        canvas["edges"]![0]!["fromNode"].Should().NotBeNull();
        canvas["edges"]![0]!["label"].Should().NotBeNull();
    }

    [Fact]
    public void Obsidian_DuplicateNamesGetDistinctFiles()
    {
        var g = new KnowledgeGraph { Nodes = { new GraphNode { Id = "1", Name = "Same" }, new GraphNode { Id = "2", Name = "Same" } } };
        var files = Registry.GetRequired("obsidian").Export(g).Files!;
        files.Keys.Count(k => k.EndsWith(".md")).Should().Be(2);
    }

    [Fact]
    public void Cypher_ExportEscapesAndSanitizes()
    {
        var adapter = Registry.GetRequired("cypher");
        adapter.CanImport.Should().BeFalse();
        var g = new KnowledgeGraph
        {
            Nodes =
            {
                new GraphNode { Id = "a", Name = "O'Brien\nline", Type = "my type!" },
                new GraphNode { Id = "b", Name = "B" }
            },
            Edges = { new GraphEdge { Id = "e1", SourceId = "a", TargetId = "b", Type = "worksAt-the office" } }
        };
        var text = adapter.Export(g).Content!;
        text.Should().Contain("MERGE (n:Entity {id: 'a'})");
        text.Should().Contain("O\\'Brien\\nline");
        text.Should().Contain("n:MyType");
        text.Should().Contain("[r:WORKS_AT_THE_OFFICE {id: 'e1'}]");
        text.Split('\n', StringSplitOptions.RemoveEmptyEntries).Where(l => !l.StartsWith("//")).Should().OnlyContain(l => l.EndsWith(";"));
        FluentActions.Invoking(() => adapter.Import("x", null)).Should().Throw<NotSupportedException>();
    }

    [Fact]
    public void NTriples_ExportIsWellFormed()
    {
        var adapter = Registry.GetRequired("ntriples");
        adapter.CanImport.Should().BeFalse();
        var g = new KnowledgeGraph
        {
            Namespace = "My Space",
            Nodes =
            {
                new GraphNode { Id = "a b", Name = "Say \"hi\"\n", Type = "Person", Tags = { "x" }, Properties = { ["n"] = JsonValue.Create(3) } },
                new GraphNode { Id = "c", Name = "C" }
            },
            Edges = { new GraphEdge { Id = "e", SourceId = "a b", TargetId = "c", Type = "knows" } }
        };
        var lines = adapter.Export(g).Content!.Split('\n', StringSplitOptions.RemoveEmptyEntries);
        lines.Should().OnlyContain(l => l.StartsWith("<https://darbotlabs.com/kg/my-space/") && l.EndsWith(" ."));
        lines.Should().Contain(l => l.Contains("<http://www.w3.org/2000/01/rdf-schema#label> \"Say \\\"hi\\\"\\n\""));
        lines.Should().Contain(l => l.Contains("node/a%20b> <https://darbotlabs.com/kg/my-space/rel/knows> <https://darbotlabs.com/kg/my-space/node/c>"));
        lines.Should().Contain(l => l.Contains("22-rdf-syntax-ns#type>"));
    }

    [Fact]
    public void DarbotKg_IsLossless()
    {
        var adapter = Registry.GetRequired("darbot-kg");
        var g = new KnowledgeGraph
        {
            Name = "n",
            Nodes = { new GraphNode { Id = "a", Name = "A", Embedding = new[] { 1f }, Position = new GraphPosition(1, 2, 3), Properties = { ["x"] = JsonNode.Parse("{\"y\":[1,2]}") }, ValidTo = DateTimeOffset.Parse("2030-01-01T00:00:00Z") } },
            Edges = { new GraphEdge { Id = "e", SourceId = "a", TargetId = "a", Fact = "self" } },
            Metadata = { ["m"] = JsonValue.Create("v") }
        };
        var back = adapter.Import(adapter.Export(g).Content!, "renamed").Graph;
        back.Name.Should().Be("renamed");
        back.Nodes.Single().Should().BeEquivalentTo(g.Nodes.Single(), o => o.Excluding(n => n.Properties));
        back.Nodes.Single().Properties["x"]!["y"]![1]!.GetValue<int>().Should().Be(2);
        back.Edges.Single().Fact.Should().Be("self");
        back.Metadata["m"]!.GetValue<string>().Should().Be("v");
    }

    [Theory]
    [InlineData("3dkg")]
    [InlineData("kgforge")]
    [InlineData("obsidian")]
    [InlineData("supermemory")]
    [InlineData("mcp-memory")]
    [InlineData("mem0")]
    [InlineData("graphiti")]
    [InlineData("letta")]
    [InlineData("jsonld")]
    [InlineData("graphml")]
    [InlineData("darbot-kg")]
    public void WhollyUnparseablePayload_ThrowsArgumentException(string schema)
    {
        var adapter = Registry.GetRequired(schema);
        FluentActions.Invoking(() => adapter.Import("<<< definitely not valid {{{", "x"))
            .Should().Throw<ArgumentException>();
        FluentActions.Invoking(() => adapter.Import("   ", "x")).Should().Throw<ArgumentException>();
    }

    [Fact]
    public void MalformedRecords_AreSkippedNotFatal()
    {
        var kg = Registry.GetRequired("3dkg").Import("""{"entities":[{"name":"ok","type":"t"},{"type":"noid"},"junk"],"relationships":[{"id":"r"}]}""", "x");
        kg.Graph.Nodes.Should().HaveCount(1);
        kg.Warnings.Should().HaveCountGreaterThan(1);
        var letta = Registry.GetRequired("letta").Import("""{"blocks":[{"value":"no label"},{"label":"ok","value":"v"}]}""", "x");
        letta.Graph.Nodes.Should().HaveCount(1);
        letta.Warnings.Should().NotBeEmpty();
    }
}
