using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace LLMTray.Core.Downloads;

/// <summary>One file to fetch into the models folder.</summary>
/// <param name="Size">Checked when known.</param>
/// <param name="Sha256">Checked when known (lowercase hex).</param>
/// <param name="Identity">
/// What exactly is being fetched (pinned repository + file). A partial file
/// left by another identity (a newer version with the same name) is not
/// resumed but started over.
/// </param>
public sealed record DownloadItem(Uri Url, string Destination, long? Size, string? Sha256, string? Identity = null);

public readonly record struct DownloadProgress(long Received, long? Total);

/// <summary>
/// Fetches a file to <c>file.part</c> and moves it into place when it's
/// complete and checks out. An interrupted download resumes from the
/// partial file (HTTP Range) when the server confirms the range; otherwise
/// it starts over. A file Kaggle sends zipped is unpacked.
/// </summary>
public sealed class ModelDownloader(HttpClient http)
{
    /// <summary>No byte for this long ends the download (a proxy holding the connection).</summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(90);

    public async Task DownloadAsync(DownloadItem item, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        var name = Path.GetFileName(item.Destination);
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        if (File.Exists(item.Destination) && item.Sha256 is not null && new FileInfo(item.Destination).Length == item.Size
            && await HashFileAsync(item.Destination, cancellationToken).ConfigureAwait(false) == item.Sha256)
        {
            // Already here and verified (a retry, or a projector two models share).
            progress?.Report(new DownloadProgress(item.Size ?? 0, item.Size));
            return;
        }

        var part = item.Destination + ".part";
        var source = part + ".source";
        long have = File.Exists(part) ? new FileInfo(part).Length : 0;
        var sameSource = item.Identity is null || (File.Exists(source) && File.ReadAllText(source) == item.Identity);
        if (have > 0 && (!sameSource || (item.Size is long expected && have > expected)))
        {
            File.Delete(part);
            have = 0;
        }
        if (item.Identity is not null) File.WriteAllText(source, item.Identity);

        using var request = new HttpRequestMessage(HttpMethod.Get, item.Url);
        if (have > 0) request.Headers.Range = new RangeHeaderValue(have, null);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
        Errors.ThrowIfProxyAuth(response);

        var archive = response.Content.Headers.ContentType?.MediaType is "application/zip" or "application/x-zip-compressed"
            && !item.Destination.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);
        if (response.StatusCode == HttpStatusCode.RequestedRangeNotSatisfiable && have > 0 && have == item.Size)
        {
            // The partial file is already whole.
        }
        else
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new DownloadException($"{name}: the server answered {(int)response.StatusCode} {response.ReasonPhrase}");
            }
            long? total;
            if (response.StatusCode == HttpStatusCode.PartialContent && have > 0)
            {
                // Resumed only when the server confirms it starts where the file ends.
                var range = response.Content.Headers.ContentRange;
                if (range?.From != have || archive)
                {
                    response.Dispose();
                    File.Delete(part);
                    throw new DownloadException($"{name}: the server resumed at the wrong place. Download it again to start over.");
                }
                total = range.Length ?? item.Size;
            }
            else
            {
                have = 0;
                total = archive ? null : response.Content.Headers.ContentLength ?? item.Size;
            }
            if (!archive && item.Size is long size && total is long t && t != size)
            {
                throw new DownloadException($"{name}: the server offers {t:N0} bytes, expected {size:N0}");
            }
            await CopyAsync(response, part, have, total, progress, cancellationToken).ConfigureAwait(false);
            if (!archive && total is long whole && new FileInfo(part).Length != whole)
            {
                throw new DownloadException($"{name}: the connection ended early. Download it again to resume.");
            }
        }

        if (archive) Unzip(part, name);

        var got = new FileInfo(part).Length;
        if (item.Size is long want && got != want)
        {
            File.Delete(part);
            throw new DownloadException($"{name}: got {got:N0} bytes, expected {want:N0}. It was removed; download it again.");
        }
        if (item.Sha256 is not null && await HashFileAsync(part, cancellationToken).ConfigureAwait(false) != item.Sha256)
        {
            File.Delete(part);
            throw new DownloadException($"{name}: the file is damaged (SHA-256 differs). It was removed; download it again.");
        }
        File.Move(part, item.Destination, overwrite: true);
        File.Delete(source);
    }

    async Task CopyAsync(HttpResponseMessage response, string part, long have, long? total, IProgress<DownloadProgress>? progress, CancellationToken cancellationToken)
    {
        await using var file = new FileStream(part, have > 0 ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        var buffer = new byte[1 << 20];
        var received = have;
        var lastReport = DateTime.MinValue;
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        while (true)
        {
            stall.CancelAfter(StallTimeout);
            int read;
            try
            {
                read = await stream.ReadAsync(buffer, stall.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new DownloadException($"{Path.GetFileName(part)[..^5]}: no data for {StallTimeout.TotalSeconds:0} s. Download it again to resume.");
            }
            if (read == 0) break;
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

    /// <summary>Kaggle sometimes sends a single file zipped: the file is taken out.</summary>
    static void Unzip(string part, string name)
    {
        var unpacked = part + ".unzip";
        using (var zip = ZipFile.OpenRead(part))
        {
            var entry = zip.Entries.FirstOrDefault(e => e.Name == name) ?? (zip.Entries.Count == 1 ? zip.Entries[0] : null)
                ?? throw new DownloadException($"{name}: the archive the server sent doesn't hold it");
            entry.ExtractToFile(unpacked, overwrite: true);
        }
        File.Move(unpacked, part, overwrite: true);
    }

    static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }
}
