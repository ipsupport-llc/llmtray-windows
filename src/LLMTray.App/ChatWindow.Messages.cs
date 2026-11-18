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

    /// <summary>For the Store listing: a temporary chat with a question and a formatted answer. CI only.</summary>
    internal async Task StoreScreenshotAsync(string folder, Func<UIElement, string, Task> save)
    {
        chat.New(temporary: true);
        chat.Messages.Add(new LLMTray.Core.Chat.ChatMessage { Role = "user", Content = "Write a PowerShell command that lists the five largest files in my Downloads folder." });
        chat.Messages.Add(new LLMTray.Core.Chat.ChatMessage
        {
            Role = "assistant",
            Model = "gemma-4-E4B-it-Q4_K_M",
            Content = "Here's one that sorts by size and shows each file in megabytes:\n\n```powershell\nGet-ChildItem \"$HOME\\Downloads\" -File -Recurse |\n  Sort-Object Length -Descending |\n  Select-Object -First 5 Name, @{n='MB'; e={[math]::Round($_.Length / 1MB, 1)}}\n```\n\n- `-Recurse` looks into subfolders too; drop it to check only the top level.\n- Change `-First 5` to see more files.\n- Nothing is deleted: the command only lists.",
        });
        RenderSoon();
        try
        {
            await Task.Delay(1500);
            if (Content is UIElement content) await save(content, Path.Combine(folder, "chat.png"));
        }
        finally
        {
            chat.New();
        }
        await Task.Delay(500);
    }
}
