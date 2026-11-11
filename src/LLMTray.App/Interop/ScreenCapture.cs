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
        return new Capture(item.DisplayName ?? "", png, await RecognizeAsync(png));
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
        pool.FrameArrived += async (sender, _) =>
        {
            using var frame = sender.TryGetNextFrame();
            if (frame is null || first.Task.IsCompleted) return;
            try
            {
                first.TrySetResult(await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied));
            }
            catch (Exception e)
            {
                first.TrySetException(e);
            }
        };
        session.StartCapture();
        return await first.Task.WaitAsync(TimeSpan.FromSeconds(5));
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
    static async Task<string?> RecognizeAsync(byte[] png)
    {
        var engine = OcrEngine.TryCreateFromUserProfileLanguages();
        if (engine is null) return null;
        using var stream = new InMemoryRandomAccessStream();
        await stream.WriteAsync(png.AsBuffer());
        stream.Seek(0);
        var decoder = await BitmapDecoder.CreateAsync(stream);
        using var bitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);
        if (bitmap.PixelWidth > OcrEngine.MaxImageDimension || bitmap.PixelHeight > OcrEngine.MaxImageDimension) return null;
        var result = await engine.RecognizeAsync(bitmap);
        var text = string.Join("\n", result.Lines.Select(l => l.Text));
        return text.Trim().Length > 0 ? text : null;
    }

    // ---- A Direct3D device for the frame pool ----

    static IDirect3DDevice Device()
    {
        // Hardware first; WARP (software) where there's no GPU (a VM, a CI runner).
        foreach (var driver in new[] { DriverTypeHardware, DriverTypeWarp })
        {
            if (D3D11CreateDevice(IntPtr.Zero, driver, IntPtr.Zero, CreateDeviceBgraSupport, IntPtr.Zero, 0, SdkVersion, out var d3d, out _, out var context) != 0) continue;
            try
            {
                var iid = IdxgiDevice;
                Marshal.ThrowExceptionForHR(Marshal.QueryInterface(d3d, ref iid, out var dxgi));
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
            finally
            {
                Marshal.Release(d3d);
                if (context != IntPtr.Zero) Marshal.Release(context);
            }
        }
        throw new InvalidOperationException("no Direct3D device for the capture");
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
