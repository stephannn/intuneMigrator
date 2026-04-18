using System;
using System.Net.Http;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Controls;
using Microsoft.Identity.Client;

namespace intuneMigratorClient.Services;

public class AuthService
{
    private readonly IPublicClientApplication _pca;
    private readonly string[] _scopes;

    public AuthService(string clientId, string tenantId, string scopes, string redirectUri, string? spaOrigin = null, Microsoft.Identity.Client.LogLevel msalLogLevel = Microsoft.Identity.Client.LogLevel.Warning)
    {
        _scopes = scopes.Split(new[] { ' ', ',' }, StringSplitOptions.RemoveEmptyEntries);
        
        // Extract origin from RedirectUri for SPA header requirement
        // Or use the configured SpaOrigin if provided
        var origin = !string.IsNullOrWhiteSpace(spaOrigin) 
            ? spaOrigin 
            : $"{new Uri(redirectUri).Scheme}://{new Uri(redirectUri).Authority}";

        _pca = PublicClientApplicationBuilder.Create(clientId)
            .WithAuthority(AzureCloudInstance.AzurePublic, tenantId)
            .WithRedirectUri(redirectUri)
            .WithHttpClientFactory(new SpaHttpClientFactory(origin))
                .WithLogging((level, message, containsPii) =>
                {
                    // MSAL messages have a very long prefix (Version, OS, Correlation ID). Strip it for readability.
                    var cleanMessage = message;
                    var lastBracketIndex = message.IndexOf("] ");
                    if (lastBracketIndex > -1 && message.Length > lastBracketIndex + 2)
                    {
                        cleanMessage = message.Substring(lastBracketIndex + 2);
                    }

                    // Filter out known noisy Info messages
                    if (level == Microsoft.Identity.Client.LogLevel.Info && cleanMessage.Contains("[Region discovery]")) return;

                    if (level == Microsoft.Identity.Client.LogLevel.Error) LogService.Error($"MSAL: {cleanMessage}");
                    else if (level == Microsoft.Identity.Client.LogLevel.Warning) LogService.Warning($"MSAL: {cleanMessage}");
                    else LogService.Info($"MSAL: {cleanMessage}");
                }, msalLogLevel, enablePiiLogging: false)
            .Build();
    }

    public async Task<AuthenticationResult?> LoginAsync(IntPtr parentWindowHandle, System.Threading.CancellationToken cancellationToken = default)
    {
        var accounts = await _pca.GetAccountsAsync();
        try
        {
            return await _pca.AcquireTokenSilent(_scopes, accounts.FirstOrDefault())
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalUiRequiredException)
        {
            return await _pca.AcquireTokenInteractive(_scopes)
                .WithParentActivityOrWindow(parentWindowHandle)
                .WithUseEmbeddedWebView(false)
                .ExecuteAsync(cancellationToken);
        }
    }

    public async Task<AuthenticationResult?> LoginDeviceCodeAsync(Func<DeviceCodeResult, Task> deviceCodeResultCallback, CancellationToken cancellationToken = default)
    {
        return await _pca.AcquireTokenWithDeviceCode(_scopes, deviceCodeResultCallback)
            .ExecuteAsync(cancellationToken);
    }

    public async Task LogoutAsync()
    {
        var accounts = await _pca.GetAccountsAsync();
        foreach (var account in accounts)
        {
            await _pca.RemoveAsync(account);
        }
    }

    private class SpaHttpClientFactory : IMsalHttpClientFactory
    {
        private readonly HttpClient _httpClient;

        public SpaHttpClientFactory(string origin)
        {
            _httpClient = new HttpClient();
            _httpClient.DefaultRequestHeaders.Add("Origin", origin);
        }

        public HttpClient GetHttpClient() => _httpClient;
    }
}