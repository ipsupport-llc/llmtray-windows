using Microsoft.UI.Xaml;

namespace LLMTray.App;

sealed partial class ChatWindow
{
    /// <summary>
    /// The messages, Regenerate and the composer in one centred column. The
    /// runner's screen is too small for a window wider than the column, so
    /// the column is narrowed for the picture instead.
    /// </summary>
    internal async Task ColumnScreenshotAsync(string folder, Func<UIElement, string, Task> save)
    {
        chat.New(temporary: true);
        chat.Messages.Add(new LLMTray.Core.Chat.ChatMessage { Role = "user", Content = "Hi" });
        chat.Messages.Add(new LLMTray.Core.Chat.ChatMessage { Role = "assistant", Content = "Hi! How can I help?", Model = "demo-model" });
        RenderSoon();
        var width = messages.MaxWidth;
        messages.MaxWidth = 420;
        await Task.Delay(1200);
        if (Content is UIElement content) await save(content, Path.Combine(folder, "chat-column.png"));
        messages.MaxWidth = width;
        chat.New();
        await Task.Delay(500);
    }
}
