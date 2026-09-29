namespace Darbot.Memory.Mcp.Core.Graph;

/// <summary>Configuration for the knowledge-graph memory layer.</summary>
public class GraphMemoryConfiguration
{
    public const string SectionName = "Graph";

    public string RootPath { get; set; } = "./data/graphs";
    public string DefaultGraph { get; set; } = "default";
    public int MaxImportBytes { get; set; } = 20_000_000;
}
