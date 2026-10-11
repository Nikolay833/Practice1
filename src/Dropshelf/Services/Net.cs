using System;
using System.Net.Http;
using System.Net.Http.Headers;

namespace Dropshelf.Services;

/// <summary>
/// The one <see cref="HttpClient"/> for every network call (weather, geocoding, ICS feeds). A single instance is reused so
/// connections are pooled; every request is cut off after 10 seconds. Callers always await it off the UI thread's critical
/// path and treat any failure as "offline" (logged, never thrown at the user).
/// </summary>
internal static class Net
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(10);

    /// <summary>Largest response body the client buffers (big ICS feeds can run to tens of MB).</summary>
    public const long MaxResponseBytes = 64L * 1024 * 1024;

    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var client = new HttpClient(new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            AllowAutoRedirect = true,
        })
        {
            Timeout = Timeout,
            MaxResponseContentBufferSize = MaxResponseBytes,
        };
        client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("Dropshelf", "1.0"));
        return client;
    }
}
