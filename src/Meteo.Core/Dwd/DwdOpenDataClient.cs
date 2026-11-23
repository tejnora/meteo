using System.Net;
using System.Text.RegularExpressions;
using Meteo.Core.Grib;

namespace Meteo.Core.Dwd;

/// <summary>Downloads and decodes GRIB2 files from DWD open data.</summary>
public sealed partial class DwdOpenDataClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly SemaphoreSlim _throttle;

    public DwdOpenDataClient(int maxParallelDownloads = 8, HttpClient? http = null)
    {
        _ownsHttp = http is null;
        _http = http ?? new HttpClient { Timeout = TimeSpan.FromMinutes(3) };
        if (_ownsHttp) _http.DefaultRequestHeaders.UserAgent.ParseAdd("Meteo/1.0");
        _throttle = new SemaphoreSlim(maxParallelDownloads);
    }

    /// <summary>Total number of bytes downloaded (compressed).</summary>
    public long BytesDownloaded => Interlocked.Read(ref _bytes);
    private long _bytes;

    public async Task<bool> ExistsAsync(string url, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Head, url);
            using var resp = await _http.SendAsync(req, ct);
            return resp.IsSuccessStatusCode;
        }
        finally { _throttle.Release(); }
    }

    /// <summary>Downloads, decompresses (bz2) and decodes one GRIB2 file; returns its first field.</summary>
    public async Task<Grib2Field> GetFieldAsync(string url, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        byte[] compressed;
        try
        {
            compressed = await GetWithRetryAsync(url, ct);
        }
        finally { _throttle.Release(); }
        Interlocked.Add(ref _bytes, compressed.Length);

        return await Task.Run(() =>
        {
            using var ms = new MemoryStream(compressed);
            var raw = Bz2.Decompress(ms);
            var fields = Grib2Reader.Read(raw);
            if (fields.Count == 0) throw new InvalidDataException($"No GRIB2 field in {url}");
            return fields[0];
        }, ct);
    }

    /// <summary>Lists file names in a DWD directory (Apache-style index).</summary>
    public async Task<IReadOnlyList<string>> ListAsync(string directoryUrl, CancellationToken ct)
    {
        await _throttle.WaitAsync(ct);
        try
        {
            var html = await _http.GetStringAsync(directoryUrl, ct);
            return HrefRegex().Matches(html).Select(m => m.Groups[1].Value).Where(n => !n.StartsWith("..")).ToList();
        }
        finally { _throttle.Release(); }
    }

    private async Task<byte[]> GetWithRetryAsync(string url, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound)
                    throw new FileNotFoundException($"DWD file not found: {url}");
                resp.EnsureSuccessStatusCode();
                return await resp.Content.ReadAsByteArrayAsync(ct);
            }
            catch (Exception ex) when (attempt < 3 && ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(2 * attempt), ct);
            }
        }
    }

    [GeneratedRegex("href=\"([^\"]+)\"")]
    private static partial Regex HrefRegex();

    public void Dispose()
    {
        if (_ownsHttp) _http.Dispose();
        _throttle.Dispose();
    }
}
