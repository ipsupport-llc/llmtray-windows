using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace LLMTray.Core.Downloads;

/// <summary>One file to fetch into the models folder.</summary>
/// <param name="Size">Checked when known.</param>
/// <param name="Sha256">Checked when known (lowercase hex).</param>
public sealed record DownloadItem(Uri Url, string Destination, long? Size, string? Sha256);

public readonly record struct DownloadProgress(long Received, long? Total);

/// <summary>
/// Fetches a file to <c>file.part</c> and moves it into place when it's
/// complete and checks out. An interrupted download resumes from the
/// partial file (HTTP Range); a server that ignores the range starts it over.
/// </summary>
public sealed class ModelDownloader(HttpClient http)
{
    public async Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        if (File.Exists(item.Destination) && (item.Size is null || new FileInfo(item.Destination).Length == item.Size))
        {
            // Already here (a model and its projector shared by two entries, or a retry).
            if (item.Sha256 is null || await HashFileAsync(item.Destination, cancellationToken).ConfigureAwait(false) == item.Sha256)
            {
                progress?.Report(new DownloadProgress(item.Size ?? 0, item.Size));
                return;
            }
        }

        var part = item.Destination + ".part";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        if (item.Size is long expected && have > expected)
        {
            File.Delete(part);
            have = 0;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0 && have == item.Size)
        {
            // The partial file is already whole.
        }
        else
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadException($"{Path.GetFileName(item.Destination)}: the server answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            if (have > 0 && response.StatusCode != HttpStatusCode.PartialContent)
            {
                have = 0;
            }
            var total = response.Content.Headers.ContentLength is long length ? length + have : item.Size;
            if (item.Size is long size && total is long t && t != size)
            {
                throw new DownloadException($"{Path.GetFileName(item.Destination)}: the server offers {t:N0} bytes, expected {size:N0}");
            }
            await using (var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true))
            await using (var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false))
            {
                var buffer = new byte[1 << 20];
                var received = have;
                var lastReport = DateTime.MinValue;
                int read;
                while ((read = await stream.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    received += read;
                    if (DateTime.UtcNow - lastReport > TimeSpan.FromMilliseconds(250))
                    {
                        lastReport = DateTime.UtcNow;
                        progress?.Report(new DownloadProgress(received, total));
                    }
                }
                progress?.Report(new DownloadProgress(received, total));
            }
        }

        var got = new FileInfo(part).Length;
        if (item.Size is long want && got != want)
        {
            throw new DownloadException($"{Path.GetFileName(item.Destination)}: got {got:N0} bytes, expected {want:N0}. Try again to resume.");
        }
        if (item.Sha256 is not null)
        {
            var hash = await HashFileAsync(part, cancellationToken).ConfigureAwait(false);
            if (hash != item.Sha256)
            {
                File.Delete(part);
                throw new DownloadException($"{Path.GetFileName(item.Destination)}: the file is damaged (SHA-256 differs). It was removed; download it again.");
            }
        }
        File.Move(part, item.Destination, overwrite: true);
    }

    static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
