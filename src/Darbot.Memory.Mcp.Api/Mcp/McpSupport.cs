using System.Text.Json;
using System.Text.Json.Serialization;
using Darbot.Memory.Mcp.Core.Graph;
using ModelContextProtocol;

namespace Darbot.Memory.Mcp.Api.Mcp;

internal static class McpJson
{
    private static readonly JsonSerializerOptions Options = Create();

    private static JsonSerializerOptions Create()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        };
        options.Converters.Add(new JsonStringEnumConverter());
        return options;
    }

    public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

    /// <summary>Embeddings are large and useless to an LLM client.</summary>
    public static GraphNode Slim(GraphNode node) => node with { Embedding = null };

    public static object Summarize(KnowledgeGraph graph) => new
    {
        graph.Name,
        graph.Namespace,
        graph.SchemaVersion,
        nodeCount = graph.Nodes.Count,
        edgeCount = graph.Edges.Count,
        clusterCount = graph.Clusters.Count,
        graph.UpdatedAt
    };

    /// <summary>Only <see cref="McpException"/> messages reach the client, so translate expected failures.</summary>
    public static async Task<T> GuardAsync<T>(Func<Task<T>> action)
    {
        try
        {
            return await action().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or FormatException or JsonException or KeyNotFoundException or NotSupportedException)
        {
            throw new McpException(ex.Message, ex);
        }
    }
}
