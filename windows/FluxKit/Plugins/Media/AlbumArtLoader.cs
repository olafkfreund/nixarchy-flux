using System.Net;
using System.Net.Http.Headers;

namespace FluxKit;

// Loads the album art that a computer names. The computer chooses the URL,
// so the load goes only to http and https, sends no cookies, keeps no cache,
// and stops at MaxBytes. AlbumArtLoader.swift of the Mac app has the same
// rules.
public static class AlbumArtLoader
{
    // The largest image that Flux loads, in bytes.
    public const int MaxBytes = 4 << 20;

    // The most redirects that a load follows.
    public const int MaxRedirects = 5;

    // The longest wait for the server, for the headers and for each read.
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    // HttpClient keeps no cache, and the handler sends no cookies. The load
    // follows each redirect itself, so that it checks each address.
    static readonly HttpClient client = new(new SocketsHttpHandler
    {
        UseCookies = false,
        AllowAutoRedirect = false,
        AutomaticDecompression = DecompressionMethods.None,
    })
    {
        Timeout = System.Threading.Timeout.InfiniteTimeSpan,
    };

    // Reports whether Flux loads art from the URL: http or https with a host.
    public static bool Loads(Uri url) =>
        url.IsAbsoluteUri && (url.Scheme == Uri.UriSchemeHttp || url.Scheme == Uri.UriSchemeHttps) && url.Host.Length > 0;

    // Returns the image data, or null for another scheme, an error, an HTTP
    // status other than 200, or more than MaxBytes.
    public static async Task<byte[]?> LoadAsync(Uri url, CancellationToken ct = default)
    {
        if (!Loads(url))
        {
            return null;
        }
        // The timer starts again before each read, so that only a stalled
        // server times out.
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var current = url;
            for (var redirects = 0; ; redirects++)
            {
                idle.CancelAfter(Timeout);
                using var request = new HttpRequestMessage(HttpMethod.Get, current);
                request.Headers.CacheControl = new CacheControlHeaderValue { NoStore = true };
                using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, idle.Token).ConfigureAwait(false);
                if ((int)response.StatusCode is 301 or 302 or 303 or 307 or 308)
                {
                    if (response.Headers.Location is not { } location || redirects >= MaxRedirects)
                    {
                        return null;
                    }
                    var next = location.IsAbsoluteUri ? location : new Uri(current, location);
                    // A redirect can lead to another scheme, or from https to http.
                    if (!Loads(next) || (current.Scheme == Uri.UriSchemeHttps && next.Scheme != Uri.UriSchemeHttps))
                    {
                        return null;
                    }
                    current = next;
                    continue;
                }
                if (response.StatusCode != HttpStatusCode.OK || response.Content.Headers.ContentLength > MaxBytes)
                {
                    return null;
                }
                return await ReadAsync(response.Content, idle).ConfigureAwait(false);
            }
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or InvalidOperationException or UriFormatException)
        {
            return null;
        }
    }

    // Reads the body, or returns null above MaxBytes.
    static async Task<byte[]?> ReadAsync(HttpContent content, CancellationTokenSource idle)
    {
        await using var stream = await content.ReadAsStreamAsync(idle.Token).ConfigureAwait(false);
        var data = new MemoryStream();
        var buffer = new byte[81920];
        while (true)
        {
            idle.CancelAfter(Timeout);
            var n = await stream.ReadAsync(buffer, idle.Token).ConfigureAwait(false);
            if (n == 0)
            {
                return data.ToArray();
            }
            if (data.Length + n > MaxBytes)
            {
                return null;
            }
            data.Write(buffer, 0, n);
        }
    }
}
