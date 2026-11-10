namespace LLMTray.Core.Chat;

/// <summary>How much a thinking model may think (Mac adr/0022): one setting for every chat.</summary>
public enum ThinkingEffort { Off, Low, Medium, High }

/// <summary>What a model's chat template offers.</summary>
public enum ThinkingStyle
{
    /// <summary>Doesn't think, or nothing to steer: no picker.</summary>
    None,
    /// <summary>Thinks in a reasoning block (Qwen 3.5, Gemma 4, Nemotron, LFM2.5 Thinking): Off by the template's switch, Low and Medium by a budget.</summary>
    Budget,
    /// <summary>Its own effort levels (gpt-oss's reasoning_effort).</summary>
    Levels,
}

/// <summary>
/// The effort as request fields (Mac Thinking). The budget is llama-server's
/// own per-request <c>thinking_budget_tokens</c>: at the budget it closes
/// the reasoning with a message and the model answers.
/// </summary>
public static class Thinking
{
    /// <summary>Reasoning tokens at most for Low and Medium.</summary>
    public static int? Budget(ThinkingEffort effort) => effort switch
    {
        ThinkingEffort.Low => 512,
        ThinkingEffort.Medium => 2048,
        _ => null,
    };

    /// <summary>What the model reads where its thinking is cut off.</summary>
    public const string CutOffMessage = "I have to answer now.";

    /// <summary>The line before the latest user message at Low and Medium, so the model plans a short thought; never saved.</summary>
    public static string Note(int budget) =>
        $"[App note, not the user's words: the user chose a short reasoning effort -- think at most about {budget} tokens, then answer. Your thinking is cut off there.]";

    /// <summary>The template's style; none without one.</summary>
    public static ThinkingStyle Style(string? chatTemplate)
    {
        if (string.IsNullOrEmpty(chatTemplate)) return ThinkingStyle.None;
        if (chatTemplate.Contains("reasoning_effort", StringComparison.Ordinal)) return ThinkingStyle.Levels;
        foreach (var marker in new[] { "enable_thinking", "<think>", "<|channel>", "<|think" })
        {
            if (chatTemplate.Contains(marker, StringComparison.Ordinal)) return ThinkingStyle.Budget;
        }
        return ThinkingStyle.None;
    }

    /// <summary>The budget to tell the model, for a budget model at Low or Medium.</summary>
    public static int? BudgetToTell(ThinkingStyle style, ThinkingEffort effort) => style == ThinkingStyle.Budget ? Budget(effort) : null;

    /// <summary>
    /// The request's fields: Off is the template's switch plus a budget of
    /// nothing (for a template that thinks regardless); High sends nothing.
    /// </summary>
    public static System.Text.Json.Nodes.JsonObject Fields(ThinkingStyle style, ThinkingEffort effort)
    {
        var o = new System.Text.Json.Nodes.JsonObject();
        switch (style, effort)
        {
            case (ThinkingStyle.Budget, ThinkingEffort.Off):
                o["chat_template_kwargs"] = new System.Text.Json.Nodes.JsonObject { ["enable_thinking"] = false };
                o["thinking_budget_tokens"] = 0;
                break;
            case (ThinkingStyle.Budget, ThinkingEffort.Low or ThinkingEffort.Medium):
                o["thinking_budget_tokens"] = Budget(effort);
                o["reasoning_budget_message"] = CutOffMessage;
                break;
            case (ThinkingStyle.Levels, _):
                o["chat_template_kwargs"] = new System.Text.Json.Nodes.JsonObject
                {
                    ["reasoning_effort"] = effort switch { ThinkingEffort.Medium => "medium", ThinkingEffort.High => "high", _ => "low" },
                };
                break;
        }
        return o;
    }
}
