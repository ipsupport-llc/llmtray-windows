using Microsoft.UI.Xaml;

namespace LLMTray.App;

sealed partial class ChatWindow
{
    /// <summary>
    /// A short temporary chat: the answers across the window's width as on
    /// the Mac, Regenerate above the composer. CI only (it leaves a new
    /// empty chat).
    /// </summary>
    internal async Task MessagesScreenshotAsync(string folder, Func<UIElement, string, Task> save)
    {
        chat.New(temporary: true);
        chat.Messages.Add(new LLMTray.Core.Chat.ChatMessage { Role = "user", Content = "Hi" });
        chat.Messages.Add(new LLMTray.Core.Chat.ChatMessage { Role = "assistant", Content = "Hi! How can I help?", Model = "demo-model" });
        RenderSoon();
        try
        {
            await Task.Delay(1200);
            if (Content is UIElement content) await save(content, Path.Combine(folder, "chat-messages.png"));
        }
        finally
        {
            chat.New();
        }
        await Task.Delay(500);
    }
}
