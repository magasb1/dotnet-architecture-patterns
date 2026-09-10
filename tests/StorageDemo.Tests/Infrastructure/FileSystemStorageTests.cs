using Microsoft.Extensions.Options;
using StorageDemo.Core.Storage;
using StorageDemo.Infrastructure.FileStorage.FileSystem;

namespace StorageDemo.Tests.Infrastructure;

public sealed class FileSystemStorageTests : FileStorageContract, IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "storage-demo-tests",
        Guid.NewGuid().ToString("N"));

    protected override IFileStorage CreateStorage()
        => new FileSystemStorage(Options.Create(new FileSystemStorageOptions { RootPath = _root }));

    [Theory]
    [InlineData("../escape.txt")]
    [InlineData("documents/../../escape.txt")]
    [InlineData("..\\escape.txt")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public async Task Keys_that_escape_the_root_are_rejected(string key)
    {
        var storage = CreateStorage();

        await Assert.ThrowsAnyAsync<Exception>(
            () => storage.SaveAsync(key, new MemoryStream([1]), null));
    }

    [Fact]
    public async Task A_sibling_directory_sharing_the_root_prefix_is_rejected()
    {
        var storage = CreateStorage();
        var sibling = $"../{Path.GetFileName(_root)}-other/file.txt";

        await Assert.ThrowsAsync<StorageException>(
            () => storage.SaveAsync(sibling, new MemoryStream([1]), null));
    }

    [Fact]
    public async Task Files_land_under_the_configured_root()
    {
        var storage = CreateStorage();
        await storage.SaveAsync("documents/1/report.pdf", new MemoryStream([1, 2, 3]), null);

        Assert.True(File.Exists(Path.Combine(_root, "documents", "1", "report.pdf")));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
        {
            Directory.Delete(_root, recursive: true);
        }
    }
}
