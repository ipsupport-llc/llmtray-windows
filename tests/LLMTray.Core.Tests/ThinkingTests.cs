using LLMTray.Core.Chat;
using Xunit;

namespace LLMTray.Core.Tests;

public sealed class ThinkingTests
{
    [Fact]
    public void StylesComeFromTheTemplate()
    {
        Assert.Equal(ThinkingStyle.None, Thinking.Style(null));
        Assert.Equal(ThinkingStyle.None, Thinking.Style("{{ messages }}"));
        Assert.Equal(ThinkingStyle.Budget, Thinking.Style("{% if enable_thinking %}<think>{% endif %}"));
        Assert.Equal(ThinkingStyle.Budget, Thinking.Style("<|channel>thought"));
        Assert.Equal(ThinkingStyle.Levels, Thinking.Style("Reasoning: {{ reasoning_effort }}"));
    }

    [Fact]
    public void EffortsBecomeRequestFields()
    {
        Assert.Empty(Thinking.Fields(ThinkingStyle.Budget, ThinkingEffort.High));
        Assert.Empty(Thinking.Fields(ThinkingStyle.None, ThinkingEffort.Off));
        var off = Thinking.Fields(ThinkingStyle.Budget, ThinkingEffort.Off);
        Assert.False(off["chat_template_kwargs"]!["enable_thinking"]!.GetValue<bool>());
        Assert.Equal(0, off["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal(512, Thinking.Fields(ThinkingStyle.Budget, ThinkingEffort.Low)["thinking_budget_tokens"]!.GetValue<int>());
        Assert.Equal("low", Thinking.Fields(ThinkingStyle.Levels, ThinkingEffort.Off)["chat_template_kwargs"]!["reasoning_effort"]!.GetValue<string>());
        Assert.Equal(2048, Thinking.BudgetToTell(ThinkingStyle.Budget, ThinkingEffort.Medium));
        Assert.Null(Thinking.BudgetToTell(ThinkingStyle.Levels, ThinkingEffort.Medium));
    }
}
