namespace LLMTray.Core;

/// <summary>Constants shared with LLMTray for macOS.</summary>
public static class AppInfo
{
    /// <summary>The proxy's port, the same as on the Mac.</summary>
    public const int ProxyPort = 8765;

    /// <summary>The OpenAI-compatible base URL clients are given.</summary>
    public static string ApiBaseUrl => $"http://localhost:{ProxyPort}/v1";
}
