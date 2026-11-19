using LLMTray.Core.Runtime;

namespace LLMTray.Core.Tests;

public class DevicePlanTests
{
    static readonly LlamaBackend Vulkan = new("vulkan", "vk");
    static readonly LlamaBackend Cpu = new("cpu", "cpu");
    static readonly LlamaBackend[] Both = [Vulkan, Cpu];
    static readonly GpuDevice Uhd = new("Vulkan0", "Intel(R) UHD Graphics 770", 16000, 15000);
    static readonly GpuDevice Rtx = new("Vulkan1", "NVIDIA GeForce RTX 4070", 12282, 11000);
    static readonly GpuDevice Rx = new("Vulkan2", "AMD Radeon RX 7800 XT", 16368, 16000);

    static string[] Order(LoadChain chain) => [.. chain.Attempts.Select(a => a.Backend.Name == "cpu" ? "cpu" : string.Join(",", a.Devices.Select(d => d.Id)))];

    [Fact]
    public void AutomaticLeavesTheIntegratedGpuOutOfASplit()
    {
        // llama.cpp alone would split across both; the dedicated card runs it, the integrated one is a fallback.
        var chain = DevicePlan.Build(DeviceChoice.Automatic, Both, [Uhd, Rtx]);
        Assert.Equal(["Vulkan1", "Vulkan0", "cpu"], Order(chain));
        Assert.Null(chain.Note);
    }

    [Fact]
    public void AutomaticSplitsAcrossDedicatedCards()
    {
        var chain = DevicePlan.Build(DeviceChoice.Automatic, Both, [Uhd, Rtx, Rx]);
        Assert.Equal(["Vulkan1,Vulkan2", "Vulkan2", "Vulkan1", "Vulkan0", "cpu"], Order(chain));
    }

    [Fact]
    public void AutomaticWithOnlyAnIntegratedGpuUsesIt()
    {
        Assert.Equal(["Vulkan0", "cpu"], Order(DevicePlan.Build(DeviceChoice.Automatic, Both, [Uhd])));
    }

    [Fact]
    public void AChosenCardGoesFirst()
    {
        var chain = DevicePlan.Build(DeviceChoice.Card(Uhd, [Uhd, Rtx]), Both, [Uhd, Rtx]);
        Assert.Equal(["Vulkan0", "Vulkan1", "cpu"], Order(chain));
    }

    [Fact]
    public void AChosenCardThatsGoneFallsThroughWithANote()
    {
        var chain = DevicePlan.Build(DeviceChoice.Card(Rtx, [Uhd, Rtx]), Both, [Uhd with { Id = "Vulkan0" }]);
        Assert.Equal(["Vulkan0", "cpu"], Order(chain));
        Assert.Equal("NVIDIA GeForce RTX 4070 isn't connected", chain.Note);
        Assert.True(chain.ChosenMissing);
    }

    [Fact]
    public void AChosenCardIsFoundByNameWhenItsIndexMoved()
    {
        var choice = DeviceChoice.Card(Rtx, [Uhd, Rtx]);
        var moved = Rtx with { Id = "Vulkan0" };
        Assert.Equal(["Vulkan0", "Vulkan1", "cpu"], Order(DevicePlan.Build(choice, Both, [moved, Uhd with { Id = "Vulkan1" }])));
    }

    [Fact]
    public void TwoIdenticalCardsAreToldApartByTheirPlace()
    {
        GpuDevice a = new("Vulkan0", "NVIDIA GeForce RTX 3090", 24000, 23000), b = new("Vulkan1", "NVIDIA GeForce RTX 3090", 24000, 20000);
        var second = DeviceChoice.Card(b, [a, b]);
        Assert.Equal("card:NVIDIA GeForce RTX 3090#1", second);
        Assert.Equal("Vulkan1", DevicePlan.Build(second, Both, [a, b]).Attempts[0].Devices.Single().Id);
        Assert.Equal("card:NVIDIA GeForce RTX 3090", DeviceChoice.Card(a, [a, b]));
    }

    [Fact]
    public void ACardShortOfMemoryIsSkipped()
    {
        var full = Rtx with { FreeMiB = 100 };
        var chain = DevicePlan.Build(DeviceChoice.Card(full, [Uhd, full]), Both, [Uhd, full]);
        Assert.Equal(["Vulkan0", "cpu"], Order(chain));
        Assert.Equal("NVIDIA GeForce RTX 4070 hasn't enough free memory", chain.Note);
    }

    [Fact]
    public void AllCardsSplitsAcrossEveryUsableOne()
    {
        Assert.Equal(["Vulkan0,Vulkan1", "Vulkan1", "Vulkan0", "cpu"], Order(DevicePlan.Build(DeviceChoice.AllCards, Both, [Uhd, Rtx])));
        var one = DevicePlan.Build(DeviceChoice.AllCards, Both, [Rtx]);
        Assert.Equal(["Vulkan1", "cpu"], Order(one));
        Assert.NotNull(one.Note);
    }

    [Fact]
    public void ProcessorOnlyIsTheCpuBuildAlone()
    {
        Assert.Equal(["cpu"], Order(DevicePlan.Build(DeviceChoice.Cpu, Both, [Rtx])));
    }

    [Fact]
    public void UnknownCardsLeaveThePickToLlamaCpp()
    {
        var chain = DevicePlan.Build(DeviceChoice.Automatic, Both, null);
        Assert.Equal(["", "cpu"], Order(chain));
        Assert.Equal(["cpu"], Order(DevicePlan.Build(DeviceChoice.Automatic, Both, [])));
    }

    [Fact]
    public void AChosenCardThatCantRunGoesToTheOthersOneByOne()
    {
        // Not to a split of the rest: each other card, most free memory first.
        var gone = DeviceChoice.Card(new GpuDevice("Vulkan3", "NVIDIA GeForce RTX 5090", 32000, 31000), []);
        Assert.Equal(["Vulkan2", "Vulkan1", "Vulkan0", "cpu"], Order(DevicePlan.Build(gone, Both, [Uhd, Rtx, Rx])));
    }

    [Fact]
    public void AChosenCardWithNoCardsListedIsLookedForAgain()
    {
        var chain = DevicePlan.Build(DeviceChoice.Card(Rtx, [Rtx]), Both, []);
        Assert.Equal(["cpu"], Order(chain));
        Assert.True(chain.ChosenMissing);
    }

    [Fact]
    public void ProcessorOnlyWithoutTheCpuBuildNeverFallsToAGpu()
    {
        var chain = DevicePlan.Build(DeviceChoice.Cpu, [Vulkan], [Rtx]);
        Assert.Empty(chain.Attempts);
        Assert.NotNull(chain.Note);
    }

    [Fact]
    public void AnAttemptIsTheSameAcrossMovedIndexes()
    {
        var before = DevicePlan.Build(DeviceChoice.Automatic, Both, [Uhd, Rtx]).Attempts[0].Key;
        var moved = DevicePlan.Build(DeviceChoice.Automatic, Both, [Rtx with { Id = "Vulkan0" }, Uhd with { Id = "Vulkan1" }]).Attempts[0].Key;
        Assert.Equal(before, moved);
    }

    [Fact]
    public void TwoIdenticalCardsAreBothInTheChain()
    {
        GpuDevice a = new("Vulkan0", "NVIDIA GeForce RTX 3090", 24000, 23000), b = new("Vulkan1", "NVIDIA GeForce RTX 3090", 24000, 20000);
        Assert.Equal(["Vulkan1", "Vulkan0", "cpu"], Order(DevicePlan.Build(DeviceChoice.Card(b, [a, b]), Both, [a, b])));
    }

    [Theory]
    [InlineData("Intel(R) UHD Graphics 770", true)]
    [InlineData("Intel(R) Iris(R) Xe Graphics", true)]
    [InlineData("Intel(R) Arc(TM) Graphics", true)]
    [InlineData("Intel(R) Arc(TM) 140V Graphics", true)]
    [InlineData("Intel(R) Arc(TM) A770 Graphics", false)]
    [InlineData("AMD Radeon(TM) Graphics", true)]
    [InlineData("AMD Radeon 780M Graphics", true)]
    [InlineData("AMD Radeon RX 7800 XT", false)]
    [InlineData("NVIDIA GeForce RTX 4070", false)]
    [InlineData("Qualcomm(R) Adreno(TM) X1-85 GPU", true)]
    public void TellsIntegratedGpusByName(string description, bool integrated)
    {
        Assert.Equal(integrated, DevicePlan.IsIntegrated(description));
    }
}
