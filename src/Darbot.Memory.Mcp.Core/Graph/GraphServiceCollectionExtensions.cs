using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Darbot.Memory.Mcp.Core.Graph.Adapters;

namespace Darbot.Memory.Mcp.Core.Graph;

public static class GraphServiceCollectionExtensions
{
    /// <summary>Registers the schema adapters, registry, JSON-file store and graph memory service.</summary>
    public static IServiceCollection AddGraphMemory(this IServiceCollection services, IConfiguration configuration)
    {
        var config = new GraphMemoryConfiguration();
        configuration.GetSection(GraphMemoryConfiguration.SectionName).Bind(config);
        configuration.GetSection("Darbot:" + GraphMemoryConfiguration.SectionName).Bind(config);

        services.TryAddSingleton(sp =>
        {
            var basePath = sp.GetService<IHostEnvironment>()?.ContentRootPath ?? AppContext.BaseDirectory;
            config.RootPath = Path.IsPathRooted(config.RootPath) ? config.RootPath : Path.GetFullPath(config.RootPath, basePath);
            return config;
        });

        foreach (var adapter in CreateAdapters())
        {
            services.AddSingleton(typeof(IGraphSchemaAdapter), adapter);
        }

        services.TryAddSingleton<IGraphSchemaRegistry, GraphSchemaRegistry>();
        services.TryAddSingleton<IKnowledgeGraphStore>(sp => new JsonFileKnowledgeGraphStore(sp.GetRequiredService<GraphMemoryConfiguration>().RootPath));
        services.TryAddSingleton<IGraphMemoryService, GraphMemoryService>();
        return services;
    }

    /// <summary>Creates one instance of every built-in schema adapter.</summary>
    public static IReadOnlyList<IGraphSchemaAdapter> CreateAdapters() => new IGraphSchemaAdapter[]
    {
        new DarbotKgAdapter(), new ThreeDkgAdapter(), new KgForgeAdapter(), new ObsidianAdapter(), new SupermemoryAdapter(),
        new McpMemoryAdapter(), new Mem0Adapter(), new GraphitiAdapter(), new LettaAdapter(), new JsonLdAdapter(),
        new GraphMlAdapter(), new CypherAdapter(), new NTriplesAdapter()
    };
}
