using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using MediaBrowser.Controller;
using MediaBrowser.Controller.Plugins;
using MediaBrowser.Controller.Subtitles;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.OpenSubtitles;

/// <summary>
/// Register subtitle provider.
/// </summary>
public class PluginServiceRegistrator : IPluginServiceRegistrator
{
    public void RegisterServices(IServiceCollection serviceCollection, IServerApplicationHost applicationHost)
    {
        serviceCollection.AddHttpClient(nameof(OpenSubtitles), c =>
        {
            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                applicationHost.Name.Replace(' ', '_'),
                applicationHost.ApplicationVersionString));

            c.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue(
                "Jellyfin-Plugin-OpenSubtitles",
                System.Reflection.Assembly.GetExecutingAssembly().GetName().Version!.ToString()));

            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*"));
        })
        .ConfigurePrimaryHttpMessageHandler(sp =>
        {
            // Thread-safe rate limit handler
            var rateLimitHandler = new ClientSideRateLimitedHandler(
                sp.GetRequiredService<ILogger<ClientSideRateLimitedHandler>>());

            // Keep gzip/deflate decompression
            rateLimitHandler.InnerHandler = new HttpClientHandler
            {
                AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate
            };

            return rateLimitHandler;
        });

        serviceCollection.AddSingleton<ISubtitleProvider, OpenSubtitleDownloader>();
    }
}

/// <summary>
/// Thread-safe rate-limited handler for parallel requests.
/// </summary>
public class ClientSideRateLimitedHandler : DelegatingHandler
{
    private static readonly SemaphoreSlim _semaphore = new(2); // max 2 parallel requests
    private readonly ILogger<ClientSideRateLimitedHandler> _logger;

    public ClientSideRateLimitedHandler(ILogger<ClientSideRateLimitedHandler> logger)
    {
        _logger = logger;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await _semaphore.WaitAsync(cancellationToken);
        try
        {
            return await base.SendAsync(request, cancellationToken);
        }
        finally
        {
            _semaphore.Release();
        }
    }
}
