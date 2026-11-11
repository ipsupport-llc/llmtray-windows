using System.Diagnostics;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Text.Json.Nodes;
using LLMTray.Core.ProjectFiles;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;

namespace LLMTray.App.Interop;

/// <summary>
/// Project pictures are decoded by the app's own exe in <c>--decode-picture</c> mode (adr/0024, Mac
/// adr/0020): a child confined to a job (1 GB, no children, killed with the app) that only starts once
/// it's in the job (it waits for "go"). It reads the header's size first and refuses more than 100
/// megapixels before decoding, takes frame 0 only, scales straight to the side asked for, and writes the
/// picture again with nothing of the original's metadata (no GPS, no camera data). One JSON line out.
/// </summary>
static class PictureDecoding
{
    public const string ArgumentName = "--decode-picture";
    const long MemoryLimit = 1L << 30;
    static readonly TimeSpan WallTime = TimeSpan.FromSeconds(10);
    const int MaxOutputBytes = 16 << 20;

    // ---- The child ----

    /// <summary>The child's side: <c>--decode-picture &lt;in&gt; &lt;out folder&gt; &lt;side&gt;</c>.</summary>
    public static int RunChild(string input, string folder, string side)
    {
        if (Console.In.ReadLine()?.Trim() != "go") return 2;
        JsonObject line;
        try
        {
            line = DecodeAsync(input, folder, int.Parse(side, System.Globalization.CultureInfo.InvariantCulture)).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            // A decoder's own failure on hostile input: the picture is refused, the app goes on.
            line = new JsonObject { ["error"] = "not a picture Windows can read (" + e.GetType().Name + ")" };
        }
        Console.Out.WriteLine(line.ToJsonString());
        Console.Out.Flush();
        return 0;
    }

    static async Task<JsonObject> DecodeAsync(string input, string folder, int side)
    {
        var file = await StorageFile.GetFileFromPathAsync(input);
        using var stream = await file.OpenReadAsync();
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(stream);
        }
        catch (Exception)
        {
            return new JsonObject { ["error"] = "not a picture" };
        }
        // The header's size, before any pixel is decoded.
        var (width, height) = (decoder.OrientedPixelWidth, decoder.OrientedPixelHeight);
        if (width == 0 || height == 0) return new JsonObject { ["error"] = "not a picture" };
        var pixels = (long)decoder.PixelWidth * decoder.PixelHeight;
        if (pixels > ProjectPictures.MaxMegapixels * 1_000_000L)
        {
            return new JsonObject { ["error"] = $"too many pixels ({pixels / 1_000_000} megapixels; at most {ProjectPictures.MaxMegapixels})" };
        }
        var scale = Math.Min(1.0, (double)side / Math.Max(decoder.PixelWidth, decoder.PixelHeight));
        var transform = new BitmapTransform { InterpolationMode = BitmapInterpolationMode.Fant };
        if (scale < 1)
        {
            transform.ScaledWidth = (uint)Math.Max(1, Math.Round(decoder.PixelWidth * scale));
            transform.ScaledHeight = (uint)Math.Max(1, Math.Round(decoder.PixelHeight * scale));
        }
        // Frame 0 only (a GIF's others never); upright; in sRGB.
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb);
        // A photo (a JPEG source) stays a JPEG; anything else, which may have transparency, a PNG.
        var jpeg = decoder.DecoderInformation?.CodecId == BitmapDecoder.JpegDecoderId;
        using var output = new InMemoryRandomAccessStream();
        // A new encoder: nothing of the original's metadata is copied.
        var encoder = await BitmapEncoder.CreateAsync(jpeg ? BitmapEncoder.JpegEncoderId : BitmapEncoder.PngEncoderId, output);
        encoder.SetSoftwareBitmap(bitmap);
        await encoder.FlushAsync();
        var bytes = new byte[output.Size];
        output.Seek(0);
        await output.ReadAsync(bytes.AsBuffer(), (uint)output.Size, InputStreamOptions.None);
        await File.WriteAllBytesAsync(Path.Combine(folder, "picture"), bytes);
        return new JsonObject { ["width"] = width, ["height"] = height, ["jpeg"] = jpeg };
    }

    // ---- The parent ----

    /// <summary>The app's <see cref="IPictureDecoder"/>: one child a picture, at most 10 seconds and 1 GB.</summary>
    public sealed class ChildDecoder : IPictureDecoder
    {
        public async Task<DecodedPicture> DecodeAsync(string path, int side, CancellationToken cancellationToken)
        {
            var folder = Path.Combine(Path.GetTempPath(), "llmtray-decode-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(folder);
            // The child's exit, once it's started: the folder goes after it (later, when it's slow to end).
            Task exited = Task.CompletedTask;
            try
            {
                var exe = Environment.ProcessPath ?? throw new PictureDecodeException("the app's path is unknown");
                var info = new ProcessStartInfo(exe)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardInput = true,
                    RedirectStandardOutput = true,
                    WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
                };
                foreach (var a in new[] { ArgumentName, path, folder, side.ToString(System.Globalization.CultureInfo.InvariantCulture) }) info.ArgumentList.Add(a);
                using var process = Process.Start(info) ?? throw new PictureDecodeException("the decoder didn't start");
                exited = process.WaitForExitAsync();
                IDisposable job;
                try
                {
                    job = JobObjectLauncher.Confine(process, MemoryLimit);
                }
                catch (Exception e) when (e is not OutOfMemoryException)
                {
                    Kill(process);
                    throw new PictureDecodeException("the decoder couldn't be confined");
                }
                using (job)
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                    timeout.CancelAfter(WallTime);
                    try
                    {
                        await process.StandardInput.WriteLineAsync("go".AsMemory(), timeout.Token);
                        await process.StandardInput.FlushAsync(timeout.Token);
                        var line = await ReadLineAsync(process.StandardOutput.BaseStream, timeout.Token)
                            ?? throw new PictureDecodeException("the decoder stopped");
                        // The child is gone before its file is read: nothing can swap it between the size check and the read.
                        Kill(process);
                        await process.WaitForExitAsync(timeout.Token);
                        var answer = JsonNode.Parse(line) as JsonObject ?? throw new PictureDecodeException("the decoder stopped");
                        if (answer["error"]?.GetValue<string>() is { } error) throw new PictureDecodeException(error);
                        var (width, height) = (answer["width"]!.GetValue<int>(), answer["height"]!.GetValue<int>());
                        if (width is <= 0 or > 1_000_000 || height is <= 0 or > 1_000_000 || (long)width * height > ProjectPictures.MaxMegapixels * 1_000_000L)
                        {
                            throw new PictureDecodeException("the decoder's answer is wrong");
                        }
                        return new DecodedPicture(width, height, await ReadCappedAsync(Path.Combine(folder, "picture"), timeout.Token), answer["jpeg"]?.GetValue<bool>() == true);
                    }
                    catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                    {
                        throw new PictureDecodeException("decoding took longer than 10 seconds");
                    }
                    catch (PictureDecodeException)
                    {
                        throw;
                    }
                    catch (Exception e) when (e is not (OperationCanceledException or OutOfMemoryException))
                    {
                        // A damaged answer (a duplicate key, a wrong type) or a decoder that died.
                        throw new PictureDecodeException("the decoder failed (too much memory, or a damaged picture)");
                    }
                    finally
                    {
                        Kill(process);
                    }
                }
            }
            finally
            {
                // Never waited for here (a cancel stays prompt): the folder goes as soon as the child has ended.
                if (exited.IsCompleted) DeleteFolder(folder);
                else _ = exited.ContinueWith(_ => DeleteFolder(folder), TaskScheduler.Default);
            }
        }

        static void DeleteFolder(string folder)
        {
            try
            {
                Directory.Delete(folder, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
            }
        }

        /// <summary>One whole line of at most 4 KB; null for one cut short or too long.</summary>
        static async Task<string?> ReadLineAsync(Stream output, CancellationToken token)
        {
            var buffer = new byte[4096];
            var length = 0;
            while (length < buffer.Length)
            {
                var n = await output.ReadAsync(buffer.AsMemory(length, 1), token);
                if (n == 0) return null;
                if (buffer[length] == (byte)'\n') return System.Text.Encoding.UTF8.GetString(buffer, 0, length).Trim();
                length++;
            }
            return null;
        }

        /// <summary>The picture, read through one handle and never past <see cref="MaxOutputBytes"/> whatever its size said.</summary>
        static async Task<byte[]> ReadCappedAsync(string path, CancellationToken token)
        {
            await using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 1 << 16, useAsync: true);
            using var memory = new MemoryStream();
            var buffer = new byte[1 << 16];
            int n;
            while ((n = await file.ReadAsync(buffer, token)) > 0)
            {
                if (memory.Length + n > MaxOutputBytes) throw new PictureDecodeException("the picture came out too large");
                memory.Write(buffer, 0, n);
            }
            if (memory.Length == 0) throw new PictureDecodeException("the decoder wrote nothing");
            return memory.ToArray();
        }

        static void Kill(Process process)
        {
            try
            {
                if (!process.HasExited) process.Kill();
            }
            catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception)
            {
            }
        }
    }
}
