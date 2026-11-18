// Submits a release to the Microsoft Store (adr/0025), through the Store
// submission API: a new submission from the last published one, with the
// release's package, the listing texts of store/listings and, when given,
// new screenshots; then committed for certification.
//
//   LLMTray.Store submit --app <Store ID> --package <file.msixupload> --listings <folder> [--screenshots <folder>]
//   LLMTray.Store check --listings <folder>
//
// Credentials come from the environment: PARTNER_CENTER_TENANT_ID,
// PARTNER_CENTER_CLIENT_ID and PARTNER_CENTER_CLIENT_SECRET (an Entra app
// added to Partner Center with the Manager role).

using System.IO.Compression;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using LLMTray.Core.Store;

var options = new Dictionary<string, string>(StringComparer.Ordinal);
for (var i = 1; i + 1 < args.Length; i += 2) options[args[i]] = args[i + 1];
string Need(string name) => options.TryGetValue(name, out var v) ? v : throw new ArgumentException($"{name} is required");

try
{
    switch (args.FirstOrDefault())
    {
        case "check":
            var checkedTexts = StoreListings.Load(Need("--listings"));
            Console.WriteLine($"listings: {string.Join(", ", checkedTexts.Keys)}");
            return 0;
        case "submit":
            await SubmitAsync(Need("--app"), Need("--package"), StoreListings.Load(Need("--listings")), options.GetValueOrDefault("--screenshots"));
            return 0;
        default:
            Console.Error.WriteLine("usage: LLMTray.Store submit --app <id> --package <file> --listings <folder> [--screenshots <folder>] | check --listings <folder>");
            return 2;
    }
}
catch (Exception e) when (e is ArgumentException or InvalidDataException or IOException or HttpRequestException or InvalidOperationException or System.Text.Json.JsonException or TaskCanceledException)
{
    Console.Error.WriteLine($"error: {e.Message}");
    return 1;
}

static async Task SubmitAsync(string app, string package, IReadOnlyDictionary<string, StoreText> texts, string? screenshotFolder)
{
    string Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } v ? v : throw new ArgumentException($"{name} is not set");
    if (!File.Exists(package)) throw new ArgumentException($"no package at {package}");
    var screenshots = screenshotFolder is not null && Directory.Exists(screenshotFolder)
        ? StoreListings.Order(Directory.EnumerateFiles(screenshotFolder))
        : [];
    Console.WriteLine(screenshots.Count > 0 ? $"screenshots: {string.Join(", ", screenshots)}" : "screenshots: none given, the published ones stay");

    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    http.DefaultRequestHeaders.UserAgent.ParseAdd("LLMTray-Store-Submit");
    var token = await TokenAsync(http, Env("PARTNER_CENTER_TENANT_ID"), Env("PARTNER_CENTER_CLIENT_ID"), Env("PARTNER_CENTER_CLIENT_SECRET"));
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
    var root = $"https://manage.devcenter.microsoft.com/v1.0/my/applications/{Uri.EscapeDataString(app)}";

    // A submission someone left open in Partner Center is theirs to finish or delete: never thrown away from here.
    var application = await JsonAsync(http, HttpMethod.Get, root);
    if (application["pendingApplicationSubmission"]?["id"] is { } pending)
    {
        throw new InvalidOperationException($"submission {pending} is still open in Partner Center: submit or delete it there, then run this again");
    }

    var submission = await JsonAsync(http, HttpMethod.Post, $"{root}/submissions");
    var id = (string?)submission["id"] ?? throw new InvalidOperationException("the new submission has no id");
    // Its signature can hold a "+", which the storage service would read as a space.
    var upload = ((string?)submission["fileUploadUrl"] ?? throw new InvalidOperationException("the new submission has no upload address")).Replace("+", "%2B", StringComparison.Ordinal);
    Console.WriteLine($"submission {id} created from the last published one");

    var packageName = Path.GetFileName(package);
    var shotNames = screenshots.Select(s => "screenshots/" + s).ToList();
    foreach (var skipped in StoreListings.Apply(submission, packageName, texts, shotNames))
    {
        Console.WriteLine($"warning: {skipped} isn't in the published listing and no screenshots were given: left out this time");
    }
    // Fields the API returns but doesn't take back.
    submission.Remove("fileUploadUrl");
    submission.Remove("id");
    submission.Remove("status");
    submission.Remove("statusDetails");
    await JsonAsync(http, HttpMethod.Put, $"{root}/submissions/{id}", submission);
    Console.WriteLine($"submission {id}: package {packageName}, {texts.Count} languages, {screenshots.Count} screenshots");

    // One zip: the package and the screenshots, at the names the submission gives them.
    var zip = Path.Combine(Path.GetTempPath(), $"llmtray-store-{id}.zip");
    File.Delete(zip);
    using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
    {
        archive.CreateEntryFromFile(package, packageName, CompressionLevel.NoCompression);
        for (var i = 0; i < screenshots.Count; i++) archive.CreateEntryFromFile(Path.Combine(screenshotFolder!, screenshots[i]), shotNames[i], CompressionLevel.Optimal);
    }
    await UploadAsync(upload, zip);
    File.Delete(zip);
    Console.WriteLine("uploaded");

    // A token lasts an hour; the upload may have taken much of it.
    http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", await TokenAsync(http, Env("PARTNER_CENTER_TENANT_ID"), Env("PARTNER_CENTER_CLIENT_ID"), Env("PARTNER_CENTER_CLIENT_SECRET")));
    await JsonAsync(http, HttpMethod.Post, $"{root}/submissions/{id}/commit");
    // Committing checks the submission; certification itself takes hours to days and isn't waited for.
    for (var wait = 0; ; wait++)
    {
        var state = await JsonAsync(http, HttpMethod.Get, $"{root}/submissions/{id}/status");
        var status = (string?)state["status"] ?? "";
        // Before the commit is taken up, and while it's checked: wait.
        if (status is "CommitStarted" or "PendingCommit" or "None" && wait < 60)
        {
            await Task.Delay(TimeSpan.FromSeconds(30));
            continue;
        }
        foreach (var warning in state["statusDetails"]?["warnings"] as JsonArray ?? []) Console.WriteLine($"warning: {warning?["code"]}: {warning?["details"]}");
        if (status is "CommitStarted" or "PendingCommit" or "None") throw new InvalidOperationException($"submission {id} was still {status} after 30 minutes: see Partner Center");
        // Taken up: checked and on its way through certification.
        string[] onItsWay = ["PreProcessing", "Certification", "Release", "Publishing", "Published", "PendingPublication"];
        if (!onItsWay.Contains(status))
        {
            foreach (var error in state["statusDetails"]?["errors"] as JsonArray ?? []) Console.Error.WriteLine($"error: {error?["code"]}: {error?["details"]}");
            throw new InvalidOperationException($"submission {id}: {(status.Length == 0 ? "no status" : status)} (it stays in Partner Center to look at)");
        }
        Console.WriteLine($"submission {id}: {status}");
        return;
    }
}

/// <summary>
/// The zip to the submission's storage address in 4 MiB blocks, then the
/// block list: the address's own storage version decides the limits, and
/// an old one takes at most 64 MiB in one request but 4 MiB blocks always.
/// The address carries its own key: no bearer token goes to it, and its
/// error bodies (which describe the signed request) aren't printed.
/// </summary>
static async Task UploadAsync(string upload, string file)
{
    using var plain = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
    var ids = new List<string>();
    var buffer = new byte[4 << 20];
    await using (var stream = File.OpenRead(file))
    {
        for (var n = 0; ; n++)
        {
            var read = await stream.ReadAtLeastAsync(buffer, buffer.Length, throwOnEndOfStream: false);
            if (read == 0) break;
            var id = Convert.ToBase64String(Encoding.ASCII.GetBytes(n.ToString("D6", System.Globalization.CultureInfo.InvariantCulture)));
            ids.Add(id);
            for (var attempt = 1; ; attempt++)
            {
                using var put = new HttpRequestMessage(HttpMethod.Put, $"{upload}&comp=block&blockid={Uri.EscapeDataString(id)}") { Content = new ByteArrayContent(buffer, 0, read) };
                using var response = await plain.SendAsync(put);
                if (response.IsSuccessStatusCode) break;
                if (attempt >= 3) throw new HttpRequestException($"upload: block {n}: {(int)response.StatusCode}");
                await Task.Delay(TimeSpan.FromSeconds(5 * attempt));
            }
        }
    }
    var list = new StringBuilder("<?xml version=\"1.0\" encoding=\"utf-8\"?><BlockList>");
    foreach (var id in ids) list.Append("<Latest>").Append(id).Append("</Latest>");
    list.Append("</BlockList>");
    using var commit = new HttpRequestMessage(HttpMethod.Put, $"{upload}&comp=blocklist") { Content = new StringContent(list.ToString(), Encoding.UTF8, "application/xml") };
    using var done = await plain.SendAsync(commit);
    if (!done.IsSuccessStatusCode) throw new HttpRequestException($"upload: block list: {(int)done.StatusCode}");
    Console.WriteLine($"uploaded {ids.Count} blocks");
}

static async Task<string> TokenAsync(HttpClient http, string tenant, string client, string secret)
{
    using var form = new FormUrlEncodedContent(new Dictionary<string, string>
    {
        ["grant_type"] = "client_credentials",
        ["client_id"] = client,
        ["client_secret"] = secret,
        ["resource"] = "https://manage.devcenter.microsoft.com",
    });
    using var response = await http.PostAsync($"https://login.microsoftonline.com/{Uri.EscapeDataString(tenant)}/oauth2/token", form);
    var body = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"sign-in: {(int)response.StatusCode}: Partner Center refused the credentials");
    return (string?)JsonNode.Parse(body)?["access_token"] ?? throw new HttpRequestException("sign-in: no token");
}

static async Task<JsonObject> JsonAsync(HttpClient http, HttpMethod method, string url, JsonObject? body = null)
{
    using var request = new HttpRequestMessage(method, url);
    if (body is not null) request.Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
    else if (method == HttpMethod.Post) request.Content = new StringContent("", Encoding.UTF8, "application/json");
    using var response = await http.SendAsync(request);
    var text = await response.Content.ReadAsStringAsync();
    if (!response.IsSuccessStatusCode) throw new HttpRequestException($"{method} {new Uri(url).AbsolutePath}: {(int)response.StatusCode} {text}");
    return text.Length == 0 ? [] : JsonNode.Parse(text) as JsonObject ?? [];
}
