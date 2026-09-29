using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json.Nodes;
using Darbot.Memory.Mcp.Api.Graph;
using Darbot.Memory.Mcp.Core.Graph;
using FluentAssertions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace Darbot.Memory.Mcp.Tests.Graph;

public class GraphEndpointTests : IAsyncLifetime
{
    private readonly TempDir _dir = new();
    private WebApplication _app = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Darbot:Graph:RootPath"] = _dir.Path,
            ["Darbot:Graph:MaxImportBytes"] = "2000"
        });
        builder.Services.AddGraphMemory(builder.Configuration);
        builder.Services.AddAuthorization(o => o.AddPolicy("DarbotMemoryWriter", p => p.RequireAssertion(_ => true)));
        _app = builder.Build();
        _app.UseAuthorization();
        _app.MapGraphMemory();
        await _app.StartAsync();
        _client = _app.GetTestClient();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _app.DisposeAsync();
        _dir.Dispose();
    }

    [Fact]
    public async Task FullWorkflow_ThroughRestSurface()
    {
        var schemas = await _client.GetFromJsonAsync<JsonArray>("/v1/graph-schemas");
        schemas!.Count.Should().Be(13);

        var remember = await _client.PostAsJsonAsync("/v1/graphs/demo/nodes:remember", new { text = "Coffee is great", tags = new[] { "drink" } });
        remember.StatusCode.Should().Be(HttpStatusCode.OK);
        var aId = (await remember.Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();
        var bId = (await (await _client.PostAsJsonAsync("/v1/graphs/demo/nodes:remember", new { text = "Tea is fine" })).Content.ReadFromJsonAsync<JsonNode>())!["id"]!.GetValue<string>();

        (await _client.PostAsJsonAsync("/v1/graphs/demo/edges:relate", new { sourceId = aId, targetId = bId, type = "contrasts" })).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.PostAsJsonAsync("/v1/graphs/demo/edges:relate", new { sourceId = aId, targetId = "ghost", type = "x" })).StatusCode.Should().Be(HttpStatusCode.NotFound);

        var search = await _client.PostAsJsonAsync("/v1/graphs/demo:search", new { text = "coffee" });
        (await search.Content.ReadFromJsonAsync<JsonArray>())!.Count.Should().Be(1);

        var hood = await _client.GetFromJsonAsync<JsonNode>($"/v1/graphs/demo/nodes/{aId}/neighborhood?depth=2");
        hood!["nodes"]!.AsArray().Count.Should().Be(1);

        var list = await _client.GetFromJsonAsync<JsonArray>("/v1/graphs");
        list![0]!["nodeCount"]!.GetValue<int>().Should().Be(2);

        var export = await _client.GetAsync("/v1/graphs/demo/export?schema=obsidian");
        var files = (await export.Content.ReadFromJsonAsync<JsonNode>())!["files"]!.AsObject();
        files.Count.Should().Be(3);

        var jsonld = await _client.GetAsync("/v1/graphs/demo/export?schema=jsonld");
        jsonld.Content.Headers.ContentType!.MediaType.Should().Be("application/ld+json");

        (await _client.DeleteAsync($"/v1/graphs/demo/nodes/{aId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.DeleteAsync($"/v1/graphs/demo/nodes/{aId}")).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await _client.GetAsync("/v1/graphs/demo")).StatusCode.Should().Be(HttpStatusCode.OK);
        (await _client.DeleteAsync("/v1/graphs/demo")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await _client.GetAsync("/v1/graphs/demo")).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Import_EnforcesLimitAndReportsWarnings()
    {
        var ok = await _client.PostAsync("/v1/graphs/imp/import?schema=mcp-memory&merge=true",
            new StringContent("{\"type\":\"entity\",\"name\":\"A\",\"entityType\":\"t\",\"observations\":[]}\nbad", Encoding.UTF8, "text/plain"));
        ok.StatusCode.Should().Be(HttpStatusCode.OK);
        var body = (await ok.Content.ReadFromJsonAsync<JsonNode>())!;
        body["graph"]!["nodeCount"]!.GetValue<int>().Should().Be(1);
        body["warnings"]!.AsArray().Count.Should().BeGreaterThan(0);

        (await _client.PostAsync("/v1/graphs/imp/import?schema=mcp-memory", new StringContent(new string('x', 3000)))).StatusCode
            .Should().Be(HttpStatusCode.RequestEntityTooLarge);
        (await _client.PostAsync("/v1/graphs/imp/import", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsync("/v1/graphs/imp/import?schema=nope", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await _client.PostAsync("/v1/graphs/imp/import?schema=cypher", new StringContent("{}"))).StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
