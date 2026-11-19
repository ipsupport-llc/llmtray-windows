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
        var window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        try
        {
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
        catch (COMException)
        {
            // No share sheet here (a stripped-down Windows): the text goes to the clipboard instead.
            Copy(text);
        }
    }
}
