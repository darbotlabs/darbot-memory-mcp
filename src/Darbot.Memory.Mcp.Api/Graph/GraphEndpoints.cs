using Darbot.Memory.Mcp.Core.Graph;
using Microsoft.AspNetCore.Mvc;

namespace Darbot.Memory.Mcp.Api.Graph;

public sealed record RememberRequest(string Text, string? Name = null, string? Type = null, string? Scope = null, List<string>? Tags = null);

public sealed record RelateRequest(string SourceId, string TargetId, string Type, string? Fact = null);

public static class GraphEndpoints
{
    private const string Policy = "DarbotMemoryWriter";

    public static IEndpointRouteBuilder MapGraphMemory(this IEndpointRouteBuilder app)
    {
        app.MapGet("/v1/graphs", async (IGraphMemoryService svc, CancellationToken ct) =>
            Results.Ok((await svc.ListGraphsAsync(ct)).Select(Summary)))
            .WithName("ListGraphs")
            .WithSummary("List knowledge graphs")
            .WithDescription("Lists stored knowledge graphs with node and edge counts.")
            .RequireAuthorization(Policy);

        app.MapGet("/v1/graphs/{name}", async (string name, IGraphMemoryService svc, CancellationToken ct) =>
            await svc.GetGraphAsync(name, ct) is { } graph ? Results.Ok(graph) : NotFound(name))
            .WithName("GetGraph")
            .WithSummary("Get a knowledge graph")
            .WithDescription("Returns the full canonical graph (nodes, edges, clusters, metadata).")
            .RequireAuthorization(Policy);

        app.MapDelete("/v1/graphs/{name}", async (string name, IKnowledgeGraphStore store, CancellationToken ct) =>
            await store.DeleteAsync(name, ct) ? Results.NoContent() : NotFound(name))
            .WithName("DeleteGraph")
            .WithSummary("Delete a knowledge graph")
            .WithDescription("Deletes a graph and its persisted file.")
            .RequireAuthorization(Policy);

        app.MapGet("/v1/graph-schemas", (IGraphMemoryService svc) =>
            Results.Ok(svc.Schemas.Select(a => new
            {
                a.Name,
                a.DisplayName,
                a.Description,
                a.ContentType,
                a.CanImport,
                a.CanExport
            })))
            .WithName("ListGraphSchemas")
            .WithSummary("List graph schema adapters")
            .WithDescription("Lists supported import/export schemas and their capabilities.")
            .RequireAuthorization(Policy);

        app.MapPost("/v1/graphs/{name}/import", async (string name, string? schema, bool? merge, HttpRequest request,
                IGraphMemoryService svc, GraphMemoryConfiguration config, CancellationToken ct) =>
            {
                if (string.IsNullOrWhiteSpace(schema))
                {
                    return Results.BadRequest(new { error = "The 'schema' query parameter is required." });
                }
                var max = config.MaxImportBytes;
                if (request.ContentLength > max)
                {
                    return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                }
                using var buffer = new MemoryStream();
                var chunk = new byte[81920];
                int read;
                while ((read = await request.Body.ReadAsync(chunk, ct)) > 0)
                {
                    if (buffer.Length + read > max)
                    {
                        return Results.StatusCode(StatusCodes.Status413PayloadTooLarge);
                    }
                    buffer.Write(chunk, 0, read);
                }
                var payload = System.Text.Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
                try
                {
                    var graph = await svc.ImportAsync(schema, payload, name, merge ?? true, ct);
                    var warnings = graph.Metadata.TryGetValue(GraphMemoryService.ImportWarningsKey, out var w) ? w : null;
                    return Results.Ok(new
                    {
                        graph = Summary(graph),
                        warnings = warnings ?? new System.Text.Json.Nodes.JsonArray()
                    });
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
                catch (NotSupportedException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            })
            .WithName("ImportGraph")
            .WithSummary("Import a graph from an external schema")
            .WithDescription("Body is the raw payload in the given schema (JSON, JSONL, GraphML, or a JSON map of path to content for Obsidian). Use merge=false to replace the graph.")
            .Accepts<string>("text/plain", "application/json")
            .RequireAuthorization(Policy);

        app.MapGet("/v1/graphs/{name}/export", async (string name, string? schema, IGraphMemoryService svc, CancellationToken ct) =>
            {
                try
                {
                    var result = await svc.ExportAsync(string.IsNullOrWhiteSpace(schema) ? "darbot-kg" : schema, name, ct);
                    return result.Files is not null
                        ? Results.Json(new { files = result.Files, warnings = result.Warnings })
                        : Results.Content(result.Content ?? string.Empty, result.ContentType);
                }
                catch (KeyNotFoundException)
                {
                    return NotFound(name);
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            })
            .WithName("ExportGraph")
            .WithSummary("Export a graph to an external schema")
            .WithDescription("Returns the graph rendered in the requested schema; multi-file schemas (obsidian) return {files:{path:content}}.")
            .RequireAuthorization(Policy);

        app.MapPost("/v1/graphs/{name}:search", async (string name, GraphQuery query, IGraphMemoryService svc, CancellationToken ct) =>
            Results.Ok(await svc.SearchAsync(name, query ?? new GraphQuery(), ct)))
            .WithName("SearchGraph")
            .WithSummary("Search graph nodes")
            .WithDescription("Substring search over name, aliases, description, observations, content and tags with type/scope/tag/asOf filters.")
            .RequireAuthorization(Policy);

        app.MapPost("/v1/graphs/{name}/nodes:remember", async (string name, RememberRequest body, IGraphMemoryService svc, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await svc.RememberAsync(name, body.Text, body.Name, body.Type, body.Scope, body.Tags, ct));
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            })
            .WithName("RememberNode")
            .WithSummary("Remember a memory node")
            .WithDescription("Creates (or updates) a memory node with a deterministic id derived from its content.")
            .RequireAuthorization(Policy);

        app.MapPost("/v1/graphs/{name}/edges:relate", async (string name, RelateRequest body, IGraphMemoryService svc, CancellationToken ct) =>
            {
                try
                {
                    return Results.Ok(await svc.RelateAsync(name, body.SourceId, body.TargetId, body.Type, body.Fact, ct));
                }
                catch (KeyNotFoundException ex)
                {
                    return Results.NotFound(new { error = ex.Message });
                }
                catch (ArgumentException ex)
                {
                    return Results.BadRequest(new { error = ex.Message });
                }
            })
            .WithName("RelateNodes")
            .WithSummary("Create a relation between two nodes")
            .WithDescription("Creates an edge between two existing nodes.")
            .RequireAuthorization(Policy);

        app.MapGet("/v1/graphs/{name}/nodes/{id}/neighborhood", async (string name, string id, int? depth, IGraphMemoryService svc, CancellationToken ct) =>
            await svc.NeighborhoodAsync(name, id, depth ?? 1, ct) is { } hood ? Results.Ok(hood) : Results.NotFound(new { error = $"Node '{id}' was not found in graph '{name}'." }))
            .WithName("GetNodeNeighborhood")
            .WithSummary("Get a node neighborhood")
            .WithDescription("Breadth-first neighborhood around a node; depth is capped at 4.")
            .RequireAuthorization(Policy);

        app.MapDelete("/v1/graphs/{name}/nodes/{id}", async (string name, string id, IGraphMemoryService svc, CancellationToken ct) =>
            await svc.ForgetAsync(name, id, ct) ? Results.NoContent() : Results.NotFound(new { error = $"Node '{id}' was not found in graph '{name}'." }))
            .WithName("ForgetNode")
            .WithSummary("Delete a node and its edges")
            .WithDescription("Removes the node and every edge that touches it.")
            .RequireAuthorization(Policy);

        return app;
    }

    private static IResult NotFound(string name) => Results.NotFound(new { error = $"Graph '{name}' was not found." });

    private static object Summary(KnowledgeGraph g) => new
    {
        g.Id,
        g.Name,
        g.Namespace,
        NodeCount = g.Nodes.Count,
        EdgeCount = g.Edges.Count,
        g.CreatedAt,
        g.UpdatedAt
    };
}
