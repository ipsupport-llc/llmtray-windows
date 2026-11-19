using System.Net;
using LLMTray.Core.Proxy;

namespace LLMTray.Core.Tests;

public sealed class ApiKeyCheckTests
{
    const string Key = "llmtray-abc";

    [Fact]
    public void ReadsTheKeyFromEitherHeader()
    {
        Assert.Equal(Key, ApiKeyCheck.Presented($"Bearer {Key}", null));
        Assert.Equal(Key, ApiKeyCheck.Presented($"bearer  {Key} ", null));
        Assert.Equal(Key, ApiKeyCheck.Presented(null, Key));
        Assert.Null(ApiKeyCheck.Presented($"Basic {Key}", null));
        Assert.Null(ApiKeyCheck.Presented("", ""));
    }

    [Fact]
    public void AllowsByTheRules()
    {
        var on = new ApiAccess(true, false, Key);
        Assert.True(ApiKeyCheck.Allows(ApiAccess.Open, null, null, fromThisPc: true, fromApp: false));
        // Without a key required, another device never gets in (the network needs the key).
        Assert.False(ApiKeyCheck.Allows(ApiAccess.Open, null, null, fromThisPc: false, fromApp: false));
        Assert.False(ApiKeyCheck.Allows(ApiAccess.Open, null, null, fromThisPc: false, fromApp: true));
        Assert.False(ApiKeyCheck.Allows(on, null, null, false, false));
        Assert.False(ApiKeyCheck.Allows(on, "Bearer llmtray-abd", null, false, false));
        Assert.False(ApiKeyCheck.Allows(on, "Bearer llmtray-ab", null, false, false));
        Assert.True(ApiKeyCheck.Allows(on, $"Bearer {Key}", null, false, false));
        Assert.True(ApiKeyCheck.Allows(on, null, Key, false, false));
        Assert.True(ApiKeyCheck.Allows(on, null, null, false, fromApp: true));
        // This PC needs it unless only other devices are asked.
        Assert.False(ApiKeyCheck.Allows(on, null, null, fromThisPc: true, false));
        Assert.True(ApiKeyCheck.Allows(on with { OnlyFromOtherDevices = true }, null, null, fromThisPc: true, false));
        Assert.False(ApiKeyCheck.Allows(on with { OnlyFromOtherDevices = true }, null, null, fromThisPc: false, false));
        // No key to compare with: nobody the rule asks gets in, an empty one given included.
        Assert.False(ApiKeyCheck.Allows(on with { Key = null }, "Bearer ", "", false, false));
        Assert.False(ApiKeyCheck.Allows(on with { Key = "" }, null, "", false, false));
    }

    [Fact]
    public void PreflightNeedsOriginAndMethod()
    {
        Assert.True(ApiKeyCheck.IsPreflight("OPTIONS", "http://a.test", "POST"));
        Assert.False(ApiKeyCheck.IsPreflight("OPTIONS", null, "POST"));
        Assert.False(ApiKeyCheck.IsPreflight("OPTIONS", "http://a.test", ""));
        Assert.False(ApiKeyCheck.IsPreflight("POST", "http://a.test", "POST"));
    }

    [Fact]
    public void NewKeysAreRandomLettersAndDigits()
    {
        var a = ApiKeyCheck.Generate();
        Assert.StartsWith("llmtray-", a);
        Assert.Equal(40, a.Length);
        Assert.All(a[8..], c => Assert.True(char.IsAsciiLetterOrDigit(c)));
        Assert.NotEqual(a, ApiKeyCheck.Generate());
    }

    [Fact]
    public void RefusalTakesTheRoutesShape()
    {
        Assert.Equal("authentication_error", ApiKeyCheck.Refusal("/v1/messages")["error"]!["type"]!.GetValue<string>());
        Assert.Equal("invalid_api_key", ApiKeyCheck.Refusal("/v1/chat/completions")["error"]!["code"]!.GetValue<string>());
    }

    [Fact]
    public void LoopbackIsThisPc()
    {
        Assert.True(ThisPc.Is(IPAddress.Loopback));
        Assert.True(ThisPc.Is(IPAddress.IPv6Loopback));
        Assert.True(ThisPc.Is(IPAddress.Loopback.MapToIPv6()));
        Assert.False(ThisPc.Is(IPAddress.Parse("203.0.113.7")));
        Assert.False(ThisPc.Is(null));
        // Link-local: the same address may be another device on another link.
        Assert.False(ThisPc.Is(IPAddress.Parse("fe80::1%3")));
        Assert.False(ThisPc.Is(IPAddress.Parse("169.254.10.20")));
    }
}
