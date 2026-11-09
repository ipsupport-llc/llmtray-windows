using LLMTray.Core.Models;

namespace LLMTray.Core.Tests;

public sealed class ModelCatalogTests : IDisposable
{
    readonly string root = Directory.CreateTempSubdirectory("llmtray-models-").FullName;

    public void Dispose() => Directory.Delete(root, recursive: true);

    string Touch(string relative, int bytes = 10)
    {
        var path = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    [Fact]
    public void ListsGgufFilesByName()
    {
        Touch("publisher/SmolLM2-GGUF/SmolLM2-Q4_0.gguf");
        Touch("publisher/gemma-GGUF/gemma-Q4_K_M.gguf");
        Touch("notes.txt");
        var catalog = ModelCatalog.Scan(root);
        Assert.Equal(["gemma-Q4_K_M", "SmolLM2-Q4_0"], catalog.Models.Select(m => m.Name));
        Assert.Equal("publisher/SmolLM2-GGUF/SmolLM2-Q4_0.gguf", catalog.Models[1].RelativePath);
        Assert.Equal("publisher/SmolLM2-GGUF", catalog.Models[1].Folder);
    }

    [Fact]
    public void ProjectorIsAttachedNotListed()
    {
        Touch("org/vl-GGUF/vl-Q4_K_M.gguf");
        var mmproj = Touch("org/vl-GGUF/mmproj-vl-f16.gguf");
        var model = Assert.Single(ModelCatalog.Scan(root).Models);
        Assert.Equal(mmproj, model.ProjectorPath);
    }

    [Fact]
    public void SplitModelIsOneEntryWithAllShards()
    {
        Touch("org/big-GGUF/big-Q4_K_M-00001-of-00002.gguf", 100);
        Touch("org/big-GGUF/big-Q4_K_M-00002-of-00002.gguf", 50);
        var model = Assert.Single(ModelCatalog.Scan(root).Models);
        Assert.Equal("big-Q4_K_M", model.Name);
        Assert.Equal(150, model.SizeBytes);
        Assert.EndsWith("-00001-of-00002.gguf", model.FullPath);
    }

    [Fact]
    public void SameFileNameInTwoFoldersIsNamedByPath()
    {
        Touch("a/repo/model.gguf");
        Touch("b/repo/model.gguf");
        Assert.Equal(["a/repo/model", "b/repo/model"], ModelCatalog.Scan(root).Models.Select(m => m.Name));
    }

    [Fact]
    public void ResolvesByNamePathAndSingleModelFolder()
    {
        Touch("org/one-GGUF/one-Q4.gguf");
        Touch("org/two-GGUF/two-Q4.gguf");
        Touch("org/two-GGUF/two-Q8.gguf");
        var catalog = ModelCatalog.Scan(root);
        Assert.Equal("one-Q4", catalog.Resolve("one-Q4")?.Name);
        Assert.Equal("one-Q4", catalog.Resolve("ONE-q4")?.Name);
        Assert.Equal("two-Q8", catalog.Resolve("org/two-GGUF/two-Q8.gguf")?.Name);
        Assert.Equal("two-Q8", catalog.Resolve(@"org\two-GGUF\two-Q8")?.Name);
        Assert.Equal("one-Q4", catalog.Resolve("org/one-GGUF")?.Name);
        Assert.Equal("one-Q4", catalog.Resolve("one-GGUF")?.Name);
        // Two models in the folder: the folder alone is ambiguous.
        Assert.Null(catalog.Resolve("two-GGUF"));
        Assert.Null(catalog.Resolve("gpt-4o"));
        Assert.Null(catalog.Resolve(""));
    }

    [Fact]
    public void MissingFolderIsEmpty()
    {
        Assert.Empty(ModelCatalog.Scan(Path.Combine(root, "nope")).Models);
    }
}
