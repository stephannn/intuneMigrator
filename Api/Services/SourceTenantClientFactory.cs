using Azure.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Graph;
using System.Collections.Concurrent;
using System.Security.Claims;
using System.Linq;

namespace intuneMigratorApi.Services;

public class SourceTenantContext
{
    public GraphServiceClient GraphClient { get; }
    public IConfigurationSection TenantConfig { get; }

    public SourceTenantContext(GraphServiceClient graphClient, IConfigurationSection tenantConfig)
    {
        GraphClient = graphClient;
        TenantConfig = tenantConfig;
    }
}

public class SourceTenantClientFactory
{
    private readonly IConfiguration _configuration;
    private readonly ConcurrentDictionary<string, SourceTenantContext> _clients = new();

    public SourceTenantClientFactory(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public SourceTenantContext? GetTenantContext(ClaimsPrincipal user)
    {
        IConfigurationSection? config = null;
        string? tenantId = null;
        string? cacheKey = null;

        // 1. Check if multi-tenant configuration is actually present
        var multiTenantSection = _configuration.GetSection("IntuneSourceTenants");
        if (multiTenantSection.Exists())
        {
            // 2. Extract App Roles from the authenticated user's token
            var roles = user.FindAll(ClaimTypes.Role).Select(c => c.Value)
                            .Concat(user.FindAll("roles").Select(c => c.Value))
                            .Distinct();

            // 3. Map the assigned App Role to your Configuration Keys
            foreach (var role in roles)
            {
                config = multiTenantSection.GetSection(role);
                // Checks for the nested AzureAd config or falls back to flat config
                tenantId = config.GetSection("AzureAd")["TenantId"] ?? config["TenantId"];
                
                if (!string.IsNullOrEmpty(tenantId))
                {
                    cacheKey = role;
                    break; // Found a matching role configuration
                }
            }
        }
        
        // Fallback to legacy single-tenant configuration if no specific role config exists
        if (string.IsNullOrEmpty(tenantId))
        {
            config = _configuration.GetSection("IntuneSourceTenantConfig");
            tenantId = config["TenantId"];
            cacheKey = "DefaultFallback";
        }

        // If still no configuration is found, we deny access
        if (string.IsNullOrEmpty(tenantId) || config == null || string.IsNullOrEmpty(cacheKey)) return null;

        // 3. Cache and return the Graph Client for that specific tenant
        return _clients.GetOrAdd(cacheKey, _ =>
        {
            var clientId = config.GetSection("AzureAd")["ClientId"] ?? config["ClientId"];
            var clientSecret = config.GetSection("AzureAd")["ClientSecret"] ?? config["ClientSecret"];
            var credential = new ClientSecretCredential(tenantId, clientId, clientSecret);
            var client = new GraphServiceClient(credential, new[] { "https://graph.microsoft.com/.default" });
            return new SourceTenantContext(client, config);
        });
    }
}
