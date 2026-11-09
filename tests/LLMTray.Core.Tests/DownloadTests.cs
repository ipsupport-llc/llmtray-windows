using System.Net;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using LLMTray.Core.Downloads;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace LLMTray.Core.Tests;

/// <summary>The downloader and both sources against an in-process stand-in for Hugging Face and Kaggle.</summary>
public sealed class DownloadTests : IAsyncDisposable
{
    static readonly byte[] Payload = RandomNumberGenerator.GetBytes(300_000);
    static readonly string PayloadSha = Convert.ToHexStringLower(SHA256.HashData(Payload));

    readonly string root = Directory.CreateTempSubdirectory("llmtray-dl-").FullName;
    readonly WebApplication app;
    readonly HttpClient http = new();
    readonly Uri baseUri;
    bool honourRanges = true;
    int requests;

    public DownloadTests()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, 0));
        app = builder.Build();
        app.Run(async context =>
        {
            Interlocked.Increment(ref requests);
            var path = context.Request.Path.Value!;
            switch (path)
            {
                case "/hf/api/models/pub/repo/tree/main":
                    await context.Response.WriteAsync(new JsonArray(
                        new JsonObject { ["type"] = "file", ["path"] = "m-Q4.gguf", ["size"] = Payload.Length, ["lfs"] = new JsonObject { ["oid"] = PayloadSha } },
                        new JsonObject { ["type"] = "file", ["path"] = "README.md", ["size"] = 10 },
                        new JsonObject { ["type"] = "directory", ["path"] = "docs" }).ToJsonString());
                    return;
                case "/kaggle/own/mod/gguf/var/get":
                    await context.Response.WriteAsync("""{"versionNumber":3}""");
                    return;
                case "/kaggle/own/mod/gguf/var/3/files":
                    await context.Response.WriteAsync(context.Request.Query.ContainsKey("page_token")
                        ? """{"files":[{"name":"mmproj-f16.gguf","size":5}],"nextPageToken":""}"""
                        : """{"files":[{"name":"m-Q4.gguf","size":300000}],"nextPageToken":"p2"}""");
                    return;
                case "/kaggle/own/mod/gguf/var/3/download/m-Q4.gguf":
                    // Kaggle answers with a redirect to signed storage.
                    context.Response.Redirect("/blob/m-Q4.gguf");
                    return;
                case "/blob/m-Q4.gguf":
                    await ServeAsync(context, Payload);
                    return;
                default:
                    context.Response.StatusCode = 404;
                    return;
            }
        });
        app.StartAsync().GetAwaiter().GetResult();
        var addresses = app.Services.GetRequiredService<IServer>().Features.Get<IServerAddressesFeature>()!.Addresses;
        baseUri = new Uri(addresses.First() + "/");
    }

    async Task ServeAsync(HttpContext context, byte[] data)
    {
        var range = context.Request.Headers.Range.ToString();
        if (honourRanges && range.StartsWith("bytes=", StringComparison.Ordinal))
        {
            var from = long.Parse(range["bytes=".Length..].TrimEnd('-'));
            if (from >= data.Length)
            {
                context.Response.StatusCode = 416;
                return;
            }
            context.Response.StatusCode = 206;
            context.Response.ContentLength = data.Length - from;
            await context.Response.Body.WriteAsync(data.AsMemory((int)from));
            return;
        }
        context.Response.ContentLength = data.Length;
        await context.Response.Body.WriteAsync(data);
    }

    public async ValueTask DisposeAsync()
    {
        http.Dispose();
        await app.StopAsync();
        await app.DisposeAsync();
        Directory.Delete(root, recursive: true);
    }

    DownloadItem Item(string? sha = null, long? size = null) =>
        new(new Uri(baseUri, "blob/m-Q4.gguf"), Path.Combine(root, "pub", "repo", "m-Q4.gguf"), size ?? Payload.Length, sha ?? PayloadSha);

    [Fact]
    public async Task DownloadsAndChecks()
    {
        var item = Item();
        await new ModelDownloader(http).DownloadAsync(item, null, CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.Destination));
        Assert.False(File.Exists(item.Destination + ".part"));
    }

    [Fact]
    public async Task ResumesFromThePartialFile()
    {
        var item = Item();
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        await File.WriteAllBytesAsync(item.Destination + ".part", Payload[..100_000]);
        await new ModelDownloader(http).DownloadAsync(item, null, CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.Destination));
    }

    [Fact]
    public async Task StartsOverWhenTheServerIgnoresTheRange()
    {
        honourRanges = false;
        var item = Item();
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        await File.WriteAllBytesAsync(item.Destination + ".part", Payload[..100_000]);
        await new ModelDownloader(http).DownloadAsync(item, null, CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.Destination));
    }

    [Fact]
    public async Task ADamagedFileIsRemoved()
    {
        var item = Item(sha: new string('0', 64));
        var e = await Assert.ThrowsAsync<DownloadException>(() => new ModelDownloader(http).DownloadAsync(item, null, CancellationToken.None));
        Assert.Contains("damaged", e.Message);
        Assert.False(File.Exists(item.Destination));
        Assert.False(File.Exists(item.Destination + ".part"));
    }

    [Fact]
    public async Task AWrongSizeIsRefusedBeforeDownloading()
    {
        var item = Item(size: 123);
        await Assert.ThrowsAsync<DownloadException>(() => new ModelDownloader(http).DownloadAsync(item, null, CancellationToken.None));
        Assert.False(File.Exists(item.Destination));
    }

    [Fact]
    public async Task AFileAlreadyThereIsKept()
    {
        var item = Item();
        Directory.CreateDirectory(Path.GetDirectoryName(item.Destination)!);
        await File.WriteAllBytesAsync(item.Destination, Payload);
        var before = requests;
        await new ModelDownloader(http).DownloadAsync(item, null, CancellationToken.None);
        Assert.Equal(before, requests);
    }

    [Fact]
    public async Task HuggingFaceListsFilesWithHashes()
    {
        var hf = new HuggingFaceSource(http, new Uri(baseUri, "hf/"));
        var files = await hf.ListAsync("pub/repo", CancellationToken.None);
        Assert.Equal(["m-Q4.gguf", "README.md"], files.Select(f => f.Name));
        Assert.Equal(PayloadSha, files[0].Sha256);
        Assert.Equal(new Uri(baseUri, "hf/pub/repo/resolve/abc123/m-Q4.gguf"), await hf.FileUrlAsync("pub/repo@abc123", "m-Q4.gguf", CancellationToken.None));
        Assert.Equal("pub/repo", hf.LocalFolder("pub/repo@abc123"));
        Assert.Throws<ArgumentException>(() => hf.LocalFolder("../x"));
        Assert.Throws<ArgumentException>(() => hf.LocalFolder("just-a-name"));
    }

    [Fact]
    public async Task KaggleFindsTheLatestVersionPagesAndRedirects()
    {
        var kaggle = new KaggleSource(http, new Uri(baseUri, "kaggle/"));
        var files = await kaggle.ListAsync("own/mod/gguf/var", CancellationToken.None);
        Assert.Equal(["m-Q4.gguf", "mmproj-f16.gguf"], files.Select(f => f.Name));
        Assert.Equal("own/mod-var", kaggle.LocalFolder("own/mod/gguf/var/3"));

        var url = await kaggle.FileUrlAsync("own/mod/gguf/var", "m-Q4.gguf", CancellationToken.None);
        var item = Item() with { Url = url };
        await new ModelDownloader(http).DownloadAsync(item, null, CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(item.Destination));
        Assert.Throws<ArgumentException>(() => kaggle.LocalFolder("own/mod"));
    }

    [Fact]
    public async Task TheQueueRunsJobsAndReports()
    {
        var queue = new DownloadQueue(new ModelDownloader(http));
        var done = new TaskCompletionSource<DownloadJob>();
        queue.Completed += j => done.TrySetResult(j);
        queue.Enqueue(new DownloadJob("m", [Item()]));
        var job = await done.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("m", job.Title);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (queue.State.IsActive && DateTime.UtcNow < deadline) await Task.Delay(20);
        Assert.Equal("m", queue.State.LastDone);
        Assert.Null(queue.State.LastError);
    }

    [Fact]
    public void FeaturedModelsArePinned()
    {
        Assert.NotEmpty(FeaturedModels.All);
        foreach (var m in FeaturedModels.All)
        {
            Assert.Matches("^[0-9a-f]{64}$", m.File.Sha256);
            Assert.True(m.File.Size > 0);
            Assert.True(m.IsOn(DownloadSource.HuggingFace) || m.IsOn(DownloadSource.Kaggle));
            Assert.Matches("^roman220220/", m.Folder);
            if (m.HuggingFace is { } hf) Assert.Matches("^[0-9a-f]{40}$", hf.Revision);
            if (m.Kaggle is { } k) Assert.Equal(5, k.Handle.Split('/').Length);
        }
        Assert.Contains(FeaturedModels.All, m => m.IsOn(DownloadSource.Kaggle));
    }
}
