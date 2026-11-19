using System.Runtime.InteropServices;
using Windows.ApplicationModel.DataTransfer;

namespace LLMTray.App;

/// <summary>Share… of a message's text through Windows' share sheet (Mac: ShareLink beside Copy).</summary>
public sealed partial class ChatWindow
{
    DataTransferManager? shareManager;
    string? shareText;

    void ShareText(string text)
    {
        try
        {
            var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
            if (shareManager is null)
            {
                shareManager = DataTransferManagerInterop.GetForWindow(window);
                shareManager.DataRequested += (_, e) =>
                {
                    if (shareText is not { } shared) return;
                    e.Request.Data.Properties.Title = "LLMTray";
                    e.Request.Data.SetText(shared);
                };
            }
            shareText = text;
            DataTransferManagerInterop.ShowShareUIForWindow(window);
        }
        catch (Exception e) when (e is COMException or TypeInitializationException or NotImplementedException or UnauthorizedAccessException or InvalidOperationException)
        {
            // No share sheet here (a stripped-down Windows; CsWinRT maps its HRESULTs to these): the text goes to the clipboard instead.
            try
            {
                Copy(text);
            }
            catch (COMException)
            {
                // The clipboard is busy: nothing to do but leave it.
            }
        }
    }
}
