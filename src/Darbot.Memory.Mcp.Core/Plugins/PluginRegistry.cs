using Darbot.Memory.Mcp.Core.Interfaces;
using Darbot.Memory.Mcp.Core.Models;
using Microsoft.Extensions.Logging;

namespace Darbot.Memory.Mcp.Core.Plugins;

/// <summary>
/// Registry for managing and organizing memory plugins
/// </summary>
public class PluginRegistry : IPluginRegistry
{
    private readonly Dictionary<string, IMemoryPlugin> _plugins = new();
    private readonly ILogger<PluginRegistry> _logger;

    public PluginRegistry(ILogger<PluginRegistry> logger)
    {
        _logger = logger;
    }

    public void RegisterPlugin(IMemoryPlugin plugin)
    {
        ArgumentNullException.ThrowIfNull(plugin);
        
        _plugins[plugin.Name] = plugin;
        _logger.LogInformation("Registered plugin: {PluginName} v{PluginVersion}", 
            plugin.Name, plugin.Version);
    }

    public IReadOnlyList<IMemoryPlugin> GetAllPlugins()
    {
        return _plugins.Values.ToList().AsReadOnly();
    }

    public IMemoryPlugin? GetPlugin(string name)
    {
        return _plugins.TryGetValue(name, out var plugin) ? plugin : null;
    }

    public async Task<IReadOnlyList<IMemoryPlugin>> GetAvailablePluginsAsync(CancellationToken cancellationToken = default)
    {
        var availablePlugins = new List<IMemoryPlugin>();

        foreach (var plugin in _plugins.Values)
        {
            try
            {
                if (await plugin.IsAvailableAsync(cancellationToken))
                {
                    availablePlugins.Add(plugin);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Plugin {PluginName} availability check failed", plugin.Name);
            }
        }

        return availablePlugins.AsReadOnly();
    }
}