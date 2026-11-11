using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace LLMTray.App.Interop;

/// <summary>A window or display the user picked, captured once.</summary>
sealed record Capture(string Name, byte[] Png, string? Text);

/// <summary>
/// Showing the model a window or the screen (adr/0022, Mac adr/0030): the
/// system's capture picker, where the user's click is the consent; one
/// frame, no cursor; nothing captured again later. The text in it is
/// recognized on this PC (Windows.Media.Ocr) for a model that doesn't see.
/// </summary>
static class ScreenCapture
{
    public static bool IsSupported => GraphicsCaptureSession.IsSupported();

    /// <summary>The picker, then one frame as PNG (long side at most <paramref name="maxSide"/>); null when the user cancelled.</summary>
    public static async Task<Capture?> PickAsync(IntPtr window, uint maxSide)
    {
        var picker = new GraphicsCapturePicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
        var item = await picker.PickSingleItemAsync();
        if (item is null) return null;
        using var bitmap = await FrameAsync(item);
        var png = await PngAsync(bitmap, maxSide);
        // Read from the full frame (up to what the engine takes), not the scaled picture: small text survives.
        string? text;
        try
        {
            text = await RecognizeAsync(bitmap);
        }
        catch (Exception)
        {
            // No text, but the picture is good.
            text = null;
        }
        return new Capture(item.DisplayName ?? "", png, text);
    }

    static async Task<SoftwareBitmap> FrameAsync(GraphicsCaptureItem item)
    {
        using var device = Device();
        using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
        using var session = pool.CreateCaptureSession(item);
        try
        {
            session.IsCursorCaptureEnabled = false;
        }
        catch (Exception e) when (e is COMException or InvalidCastException or NotImplementedException)
        {
            // Older Windows: the cursor may show.
        }
        var first = new TaskCompletionSource<SoftwareBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
        var taken = 0;
        async void OnFrame(Direct3D11CaptureFramePool sender, object _)
        {
            // On the pool's own thread: nothing may escape (an async void would end the app), and only the first frame counts.
            try
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is null || Interlocked.Exchange(ref taken, 1) == 1) return;
                var copy = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied);
                // Too late (timed out): nobody will dispose it but us.
                if (!first.TrySetResult(copy)) copy.Dispose();
            }
            catch (Exception e)
            {
                first.TrySetException(e);
            }
        }
        pool.FrameArrived += OnFrame;
        try
        {
            session.StartCapture();
            return await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
            // A copy still under way finds the task done and disposes its bitmap.
            first.TrySetCanceled();
            throw new InvalidOperationException("no picture came from the window in 5 seconds");
        }
        finally
        {
            pool.FrameArrived -= OnFrame;
        }
    }

    static async Task<byte[]> PngAsync(SoftwareBitmap bitmap, uint maxSide)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        using var bgra = SoftwareBitmap.Convert(bitmap, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        encoder.SetSoftwareBitmap(bgra);
        var scale = Math.Min(1.0, (double)maxSide / Math.Max(bgra.PixelWidth, bgra.PixelHeight));
        if (scale < 1)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)Math.Max(1, Math.Round(bgra.PixelWidth * scale));
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, Math.Round(bgra.PixelHeight * scale));
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }
        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        stream.Seek(0);
        await stream.ReadAsync(bytes.AsBuffer(), (uint)stream.Size, InputStreamOptions.None);
        return bytes;
    }

    /// <summary>The text in the picture, line by line in the engine's reading order; null when no OCR language is installed or nothing reads.</summary>
    static async Task<string?> RecognizeAsync(SoftwareBitmap frame)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;
        // The full frame when the engine takes it; else scaled down to its limit.
        var limit = OcrEngine.MaxImageDimension;
        using var source = SoftwareBitmap.Convert(frame, BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        SoftwareBitmap bitmap;
        if (source.PixelWidth <= limit && source.PixelHeight <= limit)
        {
            bitmap = SoftwareBitmap.Copy(source);
        }
        else
        {
            var scaled = await PngAsync(source, limit);
            using var stream = new InMemoryRandomAccessStream();
            await stream.WriteAsync(scaled.AsBuffer());
            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        }
        using var _ = bitmap;
        var result = await engine.RecognizeAsync(bitmap);
        var text = string.Join("\n", result.Lines.Select(l => l.Text));
        return text.Trim().Length > 0 ? text : null;
    }

    // ---- A Direct3D device for the frame pool ----

    static IDirect3DDevice Device()
    {
        // Hardware first; WARP (software) where there's no GPU (a VM, a CI runner).
        Exception? last = null;
        foreach (var driver in new[] { DriverTypeHardware, DriverTypeWarp })
        {
            if (D3D11CreateDevice(IntPtr.Zero, driver, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion, out var d3d, out _, out var context) != 0) continue;
            try
            {
                var iid = IdxgiDevice;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, in iid, out var dxgi));
                try
                {
                    Marshal.ThrowExceptionForHR(CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var inspectable));
                    try
                    {
                        return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(inspectable);
                    }
                    finally
                    {
                        Marshal.Release(inspectable);
                    }
                }
                finally
                {
                    Marshal.Release(dxgi);
                }
            }
            catch (Exception e) when (e is COMException or InvalidCastException)
            {
                // This driver's device won't do (a flaky GPU, a VM): the next one is tried.
                last = e;
            }
            finally
            {
                Marshal.Release(d3d);
                if (context != IntPtr.Zero) Marshal.Release(context);
            }
        }
        throw new InvalidOperationException("no Direct3D device for the capture", last);
    }

    const int DriverTypeHardware = 1;
    const int DriverTypeWarp = 5;
    const uint CreateDeviceBgraSupport = 0x20;
    const uint SdkVersion = 7;
    static readonly Guid IdxgiDevice = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c");

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags, IntPtr featureLevels, uint featureLevelCount,
        uint sdkVersion, out IntPtr device, out int featureLevel, out IntPtr immediateContext);

    [DllImport("d3d11.dll", ExactSpelling = true)]
    static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
}
