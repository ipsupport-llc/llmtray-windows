using System.Globalization;
using System.Text.RegularExpressions;

namespace LLMTray.Core.Runtime;

/// <summary>
/// Settings › Models › "Run models on" (adr/0028): Automatic, one card, all
/// cards, or the CPU only. A card is saved by its name and its place among
/// cards of that name, not by its Vulkan index, which moves when a card is
/// added or unplugged.
/// </summary>
public static class DeviceChoice
{
    public const string Automatic = "auto";
    public const string AllCards = "all";
    public const string Cpu = "cpu";
    const string CardPrefix = "card:";

    /// <summary>The saved choice for one card of <paramref name="all"/>.</summary>
    public static string Card(GpuDevice card, IReadOnlyList<GpuDevice> all)
    {
        var place = all.Where(d => d.Description == card.Description).ToList().FindIndex(d => d.Id == card.Id);
        return CardPrefix + card.Description + (place > 0 ? "#" + place.ToString(CultureInfo.InvariantCulture) : "");
    }

    /// <summary>The card a saved choice names: its name and its place among cards of that name; null for the other choices.</summary>
    public static (string Description, int Place)? ParseCard(string? choice)
    {
        if (choice is null || !choice.StartsWith(CardPrefix, StringComparison.Ordinal)) return null;
        var rest = choice[CardPrefix.Length..];
        var hash = rest.LastIndexOf('#');
        if (hash > 0 && int.TryParse(rest[(hash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var place)) return (rest[..hash], place);
        return (rest, 0);
    }

    /// <summary>The card a saved choice names among the cards found now, or null when it isn't there.</summary>
    public static GpuDevice? Find(string? choice, IReadOnlyList<GpuDevice> devices) =>
        ParseCard(choice) is { } card ? devices.Where(d => d.Description == card.Description).ElementAtOrDefault(card.Place) : null;
}

/// <summary>One try at loading a model: a backend and, on a GPU backend, the cards it gets (none: llama.cpp's own pick).</summary>
/// <param name="Key">The same try across a fresh listing: the cards as <see cref="DeviceChoice.Card"/> names them (name and place among
/// same-named cards), never their indexes, which move.</param>
public sealed record LoadAttempt(LlamaBackend Backend, IReadOnlyList<GpuDevice> Devices, string Key)
{
    public LoadAttempt(LlamaBackend backend, IReadOnlyList<GpuDevice> devices, IReadOnlyList<GpuDevice> all)
        : this(backend, devices, backend.Name + ":" + string.Join(",", devices.Select(d => DeviceChoice.Card(d, all))))
    {
    }

    /// <summary>The cards by name ("A + B"); null on the CPU or with llama.cpp's own pick.</summary>
    public string? Label => Devices.Count == 0 ? null : string.Join(" + ", Devices.Select(d => d.Description));

    /// <summary>What the status line calls it.</summary>
    public string Where => Backend.Name == "cpu" ? "the CPU" : Label ?? "the GPU";
}

/// <param name="Attempts">In the order they're tried; the CPU last.</param>
/// <param name="Note">Why the first try isn't the one chosen (a card not connected, short of memory), or null.</param>
/// <param name="ChosenMissing">The chosen card isn't among those found: worth looking again before giving up on it.</param>
public sealed record LoadChain(IReadOnlyList<LoadAttempt> Attempts, string? Note, bool ChosenMissing = false);

/// <summary>
/// The fallback chain (adr/0028): the chosen device first, then each other
/// card (dedicated before integrated, most free memory first), then the CPU.
/// </summary>
public static partial class DevicePlan
{
    /// <summary>A card with less free memory than this can't hold even part of a model: skipped without a try.</summary>
    public const long TooLittleMiB = 512;

    /// <param name="devices">The GPU backend's cards; null when the probe failed (llama.cpp then picks itself).</param>
    public static LoadChain Build(string? choice, IReadOnlyList<LlamaBackend> backends, IReadOnlyList<GpuDevice>? devices)
    {
        var gpu = backends.FirstOrDefault(b => b.Name != "cpu");
        var cpu = backends.FirstOrDefault(b => b.Name == "cpu");
        var attempts = new List<LoadAttempt>();
        string? note = null;
        var missing = false;
        choice ??= DeviceChoice.Automatic;

        if (choice == DeviceChoice.Cpu)
        {
            // The CPU only, as chosen: never a GPU instead.
            return cpu is not null ? new([new LoadAttempt(cpu, [], [])], null) : new([], "the CPU build is missing");
        }
        if (gpu is not null)
        {
            if (devices is null)
            {
                // The cards couldn't be listed: the GPU build picks its own, then the CPU.
                attempts.Add(new LoadAttempt(gpu, [], []));
                if (choice != DeviceChoice.Automatic) note = "the graphics cards couldn't be listed";
            }
            else if (devices.Count == 0)
            {
                if (DeviceChoice.ParseCard(choice) is { } gone)
                {
                    note = $"{gone.Description} isn't connected";
                    missing = true;
                }
                else if (choice != DeviceChoice.Automatic) note = "no graphics card was found";
            }
            else
            {
                var usable = devices.Where(d => d.FreeMiB >= TooLittleMiB).ToList();
                var singles = usable.OrderBy(d => IsIntegrated(d.Description) ? 1 : 0).ThenByDescending(d => d.FreeMiB).ToList();
                IReadOnlyList<GpuDevice>? first = null;
                if (DeviceChoice.ParseCard(choice) is { } wanted)
                {
                    var card = DeviceChoice.Find(choice, devices);
                    if (card is null)
                    {
                        note = $"{wanted.Description} isn't connected";
                        missing = true;
                    }
                    else if (!usable.Contains(card)) note = $"{card.Description} hasn't enough free memory";
                    else first = [card];
                }
                else if (choice == DeviceChoice.AllCards && usable.Count >= 2)
                {
                    first = usable;
                }
                else if (choice == DeviceChoice.AllCards)
                {
                    note = "only one graphics card can take it";
                }
                // Automatic's pick also for "all" with one card; a chosen card that can't run goes
                // straight to the other cards one by one, never to a split.
                if (first is null && singles.Count > 0 && DeviceChoice.ParseCard(choice) is null)
                {
                    // Automatic: every dedicated card (never split onto an integrated one), or the best integrated one.
                    var dedicated = usable.Where(d => !IsIntegrated(d.Description)).ToList();
                    first = dedicated.Count > 0 ? dedicated : [singles[0]];
                }
                if (first is not null) attempts.Add(new LoadAttempt(gpu, first, devices));
                attempts.AddRange(singles.Select(d => new LoadAttempt(gpu, [d], devices)));
                if (usable.Count == 0 && note is null && choice != DeviceChoice.Automatic) note = "no graphics card has enough free memory";
            }
        }
        if (cpu is not null) attempts.Add(new LoadAttempt(cpu, [], []));
        var distinct = attempts.DistinctBy(a => a.Key).ToList();
        return new(distinct, note, missing);
    }

    /// <summary>
    /// An integrated GPU, by its name: llama.cpp's device list doesn't say.
    /// Intel's UHD / HD / Iris and the Arc built into Core Ultra ("Intel(R)
    /// Arc(TM) Graphics", no model number), AMD's "Radeon(TM) Graphics" and
    /// "Radeon 780M"-style names, Qualcomm's Adreno.
    /// </summary>
    public static bool IsIntegrated(string description) => Integrated().IsMatch(description);

    [GeneratedRegex(@"Intel.*\b(UHD|HD|Iris)\b|Intel\(R\) Arc\(TM\) (\d+V )?Graphics$|Radeon(\(TM\))? (\d{3,4}M )?Graphics$|Radeon \d{3,4}M\b|Adreno|Microsoft Basic", RegexOptions.IgnoreCase)]
    private static partial Regex Integrated();
}
