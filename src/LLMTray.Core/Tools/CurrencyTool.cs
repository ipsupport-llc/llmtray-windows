using System.Globalization;
using System.Text.Json.Nodes;

namespace LLMTray.Core.Tools;

/// <summary>convert_currency: today's rate from ExchangeRate-API's open access.</summary>
public sealed class CurrencyTool() : SelectableTool("convert_currency")
{
    /// <summary>The service asks for this credit wherever its rates are shown.</summary>
    public const string Attribution = "Rates By Exchange Rate API (https://www.exchangerate-api.com)";

    /// <summary>Rates per base until the service's next update (daily): in memory only, off its rate limit.</summary>
    static readonly Dictionary<string, (JsonNode Rates, DateTimeOffset Until)> Cache = [];
    static readonly object CacheLock = new();

    public override ToolSchema Schema(IReadOnlyList<string> offering) =>
        new(Name, "Convert money at today's exchange rate. Rates change daily: always call this for a conversion, "
            + "never assume a rate or use calculate.",
        [
            new("amount", ParamKind.Number, Required: true, Aliases: ["value", "sum", "quantity"]),
            new("from", ParamKind.String, "ISO 4217 code.", Required: true, Aliases: ["from_currency", "base", "source", "currency_from", "src"]),
            new("to", ParamKind.String, Required: true, Aliases: ["to_currency", "target", "currency_to", "dest", "destination"]),
        ]);

    static bool IsCode(string? s) => s is { Length: 3 } && s.All(char.IsAsciiLetter);

    async Task<JsonNode> RatesAsync(WebFetch web, string @base, DateTimeOffset now, CancellationToken cancellationToken)
    {
        lock (CacheLock)
        {
            if (Cache.TryGetValue(@base, out var hit) && hit.Until > now) return hit.Rates;
        }
        var rates = await web.JsonAsync($"https://open.er-api.com/v6/latest/{@base}", cancellationToken).ConfigureAwait(false);
        if (rates.Str("result") == "success")
        {
            var next = rates.Num("time_next_update_unix") is double unix ? DateTimeOffset.FromUnixTimeSeconds((long)unix) : DateTimeOffset.MinValue;
            var until = next < now.AddHours(24) ? next : now.AddHours(24);
            lock (CacheLock) Cache[@base] = (rates, until);
        }
        return rates;
    }

    public override async Task<string> RunAsync(ToolArguments a, ToolContext context, CancellationToken cancellationToken)
    {
        if (a.Number("amount") is not double amount || !double.IsFinite(amount) || Math.Abs(amount) >= 1e15) return Error("amount must be a number");
        var from = a.String("from")?.ToUpperInvariant();
        var to = a.String("to")?.ToUpperInvariant();
        if (!IsCode(from) || !IsCode(to)) return Error("from and to must be 3-letter ISO 4217 codes, like USD");
        try
        {
            var rates = await RatesAsync(context.Web, from!, context.Now, cancellationToken).ConfigureAwait(false);
            if (rates.Str("result") != "success") return Error($"unknown currency {from}");
            if (rates["rates"].Num(to!) is not double rate) return Error($"unknown currency {to}");
            var value = amount * rate;
            // Significant digits for the rate, so tiny ones (IRR → USD) don't round to 0.
            var exactRate = decimal.Parse(rate.ToString("G6", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
            var converted = Math.Abs(value) >= 1
                ? Math.Round((decimal)value, 2, MidpointRounding.AwayFromZero)
                : decimal.Parse(value.ToString("G4", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture);
            return Json(new JsonObject
            {
                ["amount"] = amount,
                ["from"] = from,
                ["to"] = to,
                ["rate"] = exactRate,
                ["result"] = converted,
                ["rate_date"] = rates.Str("time_last_update_utc") ?? "",
                ["source"] = Attribution,
            });
        }
        catch (Exception e) when (e is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            return Error($"rate lookup failed: {WebFetch.Describe(e)}");
        }
    }
}
