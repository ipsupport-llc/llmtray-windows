using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace LLMTray.Core.ProjectFiles;

/// <summary>Reads a document's pages: in a supervised child process in the app, in-process in tests.</summary>
public interface IDocumentReader
{
    /// <summary>The pages; throws <see cref="ExtractException"/> with the reason the user is shown.</summary>
    Task<IReadOnlyList<ExtractedPage>> ReadAsync(string path, DocumentFormat format, CancellationToken cancellationToken);
}

public sealed class InProcessReader : IDocumentReader
{
    public Task<IReadOnlyList<ExtractedPage>> ReadAsync(string path, DocumentFormat format, CancellationToken cancellationToken) =>
        Task.Run<IReadOnlyList<ExtractedPage>>(() => DocumentExtractor.Extract(path, format).ToList(), cancellationToken);
}

/// <summary>A started reader process: its pipes, how to end it and when it has.</summary>
public sealed record ReaderProcess(Stream Input, Stream Output, Action Kill, Task<int> Exited, IDisposable Handle);

/// <summary>
/// The child's side and the parent's (adr/0021): the child waits for "go"
/// on stdin (sent once the parent has it in its job with a memory limit),
/// then writes one JSON line a page and a last <c>{"done":true}</c> or
/// <c>{"error":…}</c>. The parent caps the bytes it reads, the pages and
/// the wall time; a cap hit kills the child and fails the document, never
/// a silently cut one.
/// </summary>
public static class ExtractProtocol
{
    public const string ArgumentName = "--extract";

    /// <summary>The child: <c>LLMTray.exe --extract &lt;path&gt; &lt;format&gt;</c>. Returns its exit code.</summary>
    public static int RunChild(string path, string format, TextReader input, Stream output)
    {
        using var writer = new StreamWriter(output, new UTF8Encoding(false)) { AutoFlush = false, NewLine = "\n" };
        if (input.ReadLine()?.Trim() != "go") return 2;
        try
        {
            if (!Enum.TryParse<DocumentFormat>(format, out var kind)) throw new ExtractException("unknown format");
            foreach (var page in DocumentExtractor.Extract(path, kind))
            {
                var line = new JsonObject { ["p"] = page.Number, ["t"] = page.Text };
                if (page.Label is { } label) line["l"] = label;
                if (page.Table) line["tab"] = true;
                writer.WriteLine(line.ToJsonString());
                writer.Flush();
            }
            writer.WriteLine("{\"done\":true}");
        }
        catch (ExtractException e)
        {
            writer.WriteLine(new JsonObject { ["error"] = e.Message }.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            writer.WriteLine(new JsonObject { ["error"] = "the file couldn't be read: " + e.Message }.ToJsonString());
        }
        catch (Exception e)
        {
            // A parser's own failure on hostile input: the document fails, the app doesn't.
            writer.WriteLine(new JsonObject { ["error"] = "the file couldn't be read (" + e.GetType().Name + ")" }.ToJsonString());
        }
        writer.Flush();
        return 0;
    }

    /// <summary>The parent: reads what <paramref name="process"/> writes, within the caps.</summary>
    public static async Task<IReadOnlyList<ExtractedPage>> ReadAsync(ReaderProcess process, ExtractLimits limits, TimeSpan wallTime, CancellationToken cancellationToken)
    {
        using var handle = process.Handle;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(wallTime);
        // Escaping can take six bytes a character; framing a little more.
        var maxBytes = limits.MaxTextChars * 6 + limits.MaxPages * 64L + 4096;
        var pages = new List<ExtractedPage>();
        long read = 0;
        long chars = 0;
        try
        {
            await process.Input.WriteAsync("go\n"u8.ToArray(), timeout.Token).ConfigureAwait(false);
            await process.Input.FlushAsync(timeout.Token).ConfigureAwait(false);
            var buffer = new byte[64 * 1024];
            var line = new MemoryStream();
            while (true)
            {
                var n = await process.Output.ReadAsync(buffer, timeout.Token).ConfigureAwait(false);
                if (n == 0) break;
                read += n;
                if (read > maxBytes) throw new ExtractException("too large: too much text");
                var start = 0;
                for (var i = 0; i < n; i++)
                {
                    if (buffer[i] != (byte)'\n') continue;
                    line.Write(buffer, start, i - start);
                    start = i + 1;
                    if (Line(line.ToArray(), pages, limits, ref chars) is { } done) return done ? pages : throw new ExtractException("the reader stopped");
                    line.SetLength(0);
                }
                line.Write(buffer, start, n - start);
            }
            var code = await process.Exited.WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None).ConfigureAwait(false);
            throw new ExtractException(code == 0 ? "the reader stopped before the end" : "the reader failed (too much memory, or a damaged file)");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new ExtractException("too slow: it took longer than " + (int)wallTime.TotalSeconds + " seconds");
        }
        catch (TimeoutException)
        {
            throw new ExtractException("the reader stopped before the end");
        }
        catch (IOException)
        {
            throw new ExtractException("the reader failed (too much memory, or a damaged file)");
        }
        finally
        {
            if (!process.Exited.IsCompleted) process.Kill();
        }
    }

    /// <summary>One line: a page (null: go on), done (true), or the child's error (thrown).</summary>
    static bool? Line(byte[] bytes, List<ExtractedPage> pages, ExtractLimits limits, ref long chars)
    {
        JsonNode? node;
        try
        {
            node = JsonNode.Parse(bytes);
        }
        catch (JsonException)
        {
            throw new ExtractException("the reader's output didn't read");
        }
        if (node is not JsonObject o) throw new ExtractException("the reader's output didn't read");
        if (o["error"] is JsonValue error) throw new ExtractException(error.ToString());
        if (o["done"] is not null) return true;
        if (pages.Count >= limits.MaxPages) throw new ExtractException($"too large: more than {limits.MaxPages} pages");
        if (o["p"] is not JsonValue p || !p.TryGetValue<int>(out var number) || o["t"] is not JsonValue t || !t.TryGetValue<string>(out var text))
        {
            throw new ExtractException("the reader's output didn't read");
        }
        // The child keeps the caps too; the parent doesn't count on it.
        chars += text.Length;
        if (chars > limits.MaxTextChars) throw new ExtractException("too large: too much text");
        var label = o["l"] is JsonValue l && l.TryGetValue<string>(out var s) ? s : null;
        pages.Add(new ExtractedPage(number, text, label, o["tab"] is JsonValue tab && tab.TryGetValue<bool>(out var b) && b));
        return null;
    }
}

/// <summary>Runs <see cref="ExtractProtocol"/> against a child started by <paramref name="start"/> (the app's: its own exe in a job).</summary>
public sealed class ChildProcessReader(Func<string, DocumentFormat, ReaderProcess> start) : IDocumentReader
{
    /// <summary>A minute, and a minute more per 10 MB.</summary>
    public static TimeSpan WallTime(long bytes) => TimeSpan.FromSeconds(60 + bytes / (10L << 20) * 60);

    public Task<IReadOnlyList<ExtractedPage>> ReadAsync(string path, DocumentFormat format, CancellationToken cancellationToken)
    {
        var bytes = new FileInfo(path).Length;
        ReaderProcess process;
        try
        {
            process = start(path, format);
        }
        catch (Exception e) when (e is IOException or InvalidOperationException or System.ComponentModel.Win32Exception or UnauthorizedAccessException)
        {
            throw new ExtractException("the reader didn't start: " + e.Message);
        }
        return ExtractProtocol.ReadAsync(process, ExtractLimits.Default, WallTime(bytes), cancellationToken);
    }
}
