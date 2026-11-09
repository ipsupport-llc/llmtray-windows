// Fetches one file the way the app's download window does:
//   dotnet run --project tools/LLMTray.Fetch -- <hf|kaggle> <repository> <file> <destination> [sha256]
// The size comes from the source's file list; the SHA-256 from the list
// (Hugging Face LFS) or the argument.
using LLMTray.Core.Downloads;

if (args.Length is < 4 or > 5)
{
    Console.Error.WriteLine("usage: LLMTray.Fetch <hf|kaggle> <repository> <file> <destination> [sha256]");
    return 2;
}
using var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("LLMTray-Windows-CI");
IModelSource source = args[0] switch
{
    "hf" => new HuggingFaceSource(http),
    "kaggle" => new KaggleSource(http),
    _ => throw new ArgumentException($"unknown source {args[0]}"),
};
var files = await source.ListAsync(args[1], CancellationToken.None);
var file = files.FirstOrDefault(f => f.Name == args[2]);
if (file is null)
{
    Console.Error.WriteLine($"{args[2]} is not in {args[1]}: {string.Join(", ", files.Select(f => f.Name))}");
    return 1;
}
var sha = args.Length == 5 ? args[4] : file.Sha256;
var url = await source.FileUrlAsync(args[1], file.Name, CancellationToken.None);
var started = DateTime.UtcNow;
await new ModelDownloader(http).DownloadAsync(new DownloadItem(url, Path.GetFullPath(args[3]), file.Size, sha), null, CancellationToken.None);
Console.WriteLine($"{args[0]} {args[1]}/{file.Name}: {file.Size:N0} bytes in {(DateTime.UtcNow - started).TotalSeconds:0.0} s{(sha is null ? "" : ", SHA-256 checked")}");
return 0;
