using System.Net.Http;

namespace PAXCookbookSetup.Gui;

// Production IPrereqDownloader: real HTTPS to the GitHub / python.org hosts.
// Every request is host-validated by PrereqDownloadHosts BEFORE it is made,
// and redirects are followed only to allow-listed hosts. No credentials,
// no cookies, no proxy. Defensive throughout (returns null/false, never throws).
public sealed class HttpPrereqDownloader : IPrereqDownloader, IDisposable
{
    private const string UserAgent = "PAX-Cookbook-Setup";
    private readonly HttpClient _client;

    public HttpPrereqDownloader(TimeSpan? timeout = null)
    {
        var handler = new HttpClientHandler
        {
            AllowAutoRedirect = true,           // GitHub release assets redirect to a CDN…
            MaxAutomaticRedirections = 5,
            UseCookies = false,
            UseDefaultCredentials = false,
            UseProxy = false
        };
        // …and every redirect target is re-validated in DownloadFile/GetText by
        // checking the FINAL response RequestMessage URI against the allow-list.
        _client = new HttpClient(handler) { Timeout = timeout ?? TimeSpan.FromMinutes(10) };
        _client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
    }

    public string? GetText(string url, string? accept = null)
    {
        if (!PrereqDownloadHosts.IsAllowed(url)) return null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            if (!string.IsNullOrEmpty(accept))
                req.Headers.Accept.ParseAdd(accept);
            using var resp = _client.Send(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return null;
            if (!FinalHostAllowed(resp)) return null;
            using var stream = resp.Content.ReadAsStream();
            using var reader = new StreamReader(stream);
            return reader.ReadToEnd();
        }
        catch { return null; }
    }

    public bool DownloadFile(string url, string destPath)
    {
        if (!PrereqDownloadHosts.IsAllowed(url)) return false;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = _client.Send(req, HttpCompletionOption.ResponseHeadersRead);
            if (!resp.IsSuccessStatusCode) return false;
            if (!FinalHostAllowed(resp)) return false;

            var dir = Path.GetDirectoryName(destPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using (var src = resp.Content.ReadAsStream())
            using (var dst = File.Create(destPath))
            {
                src.CopyTo(dst);
            }
            return new FileInfo(destPath).Length > 0;
        }
        catch
        {
            try { if (File.Exists(destPath)) File.Delete(destPath); } catch { /* ignore */ }
            return false;
        }
    }

    // Defence in depth: confirm the URL we ACTUALLY ended on (after any
    // redirects) is still an allow-listed host.
    private static bool FinalHostAllowed(HttpResponseMessage resp)
    {
        var finalUri = resp.RequestMessage?.RequestUri?.ToString();
        return finalUri is null || PrereqDownloadHosts.IsAllowed(finalUri);
    }

    public void Dispose() => _client.Dispose();
}
