using Microsoft.Extensions.Logging.Abstractions;
using StorageDemo.Core.Documents;

namespace StorageDemo.Tests.Application;

public sealed class StorageReconcilerTests
{
    private readonly FakeFileStorage _storage = new();
    private readonly FakeDocumentRepository _repository = new();

    private readonly FakeMediaAnalyzer _analyzer = new();

    private readonly InMemoryAnalysisQueue _queue = new();

    private StorageReconciler CreateReconciler()
        => new(
            _storage,
            _repository,
            _queue,
            new InMemoryChangeFeed(),
            NullLogger<StorageReconciler>.Instance);

    private DocumentService CreateService()
        => new(
            _storage,
            _repository,
            _analyzer,
            _queue,
            new InMemoryChangeFeed(),
            NullLogger<DocumentService>.Instance);

    private void PutObject(string key, string content = "x")
        => _storage.Objects[key] = System.Text.Encoding.UTF8.GetBytes(content);

    [Fact]
    public async Task An_object_written_outside_the_application_is_imported()
    {
        PutObject("documents/holiday.jpg", "bytes");

        var result = await CreateReconciler().ReconcileAsync("documents/");

        Assert.Equal(1, result.Added);
        var imported = Assert.Single(_repository.Documents.Values);
        Assert.Equal("holiday.jpg", imported.FileName);
        Assert.Equal("image/jpeg", imported.ContentType);
        Assert.Equal(5, imported.Size);
    }

    [Fact]
    public async Task Importing_the_same_object_twice_does_not_duplicate_it()
    {
        PutObject("documents/holiday.jpg");
        var reconciler = CreateReconciler();

        await reconciler.ReconcileAsync("documents/");
        var second = await reconciler.ReconcileAsync("documents/");

        Assert.Single(_repository.Documents);
        Assert.False(second.AnyChanges);
    }

    [Fact]
    public async Task An_object_replaced_outside_the_application_updates_the_size()
    {
        PutObject("documents/report.txt", "short");
        var reconciler = CreateReconciler();
        await reconciler.ReconcileAsync("documents/");

        PutObject("documents/report.txt", "much longer content");
        var result = await reconciler.ReconcileAsync("documents/");

        Assert.Equal(1, result.Updated);
        Assert.Equal(19, _repository.Documents.Values.Single().Size);
    }

    [Fact]
    public async Task An_object_deleted_outside_the_application_removes_the_metadata()
    {
        PutObject("documents/report.txt");
        var reconciler = CreateReconciler();
        await reconciler.ReconcileAsync("documents/");

        _storage.Objects.Clear();
        var result = await reconciler.ReconcileAsync("documents/");

        Assert.Equal(1, result.Removed);
        Assert.Empty(_repository.Documents);
    }

    [Fact]
    public async Task An_uploaded_document_survives_reconciliation_unchanged()
    {
        var service = CreateService();
        var uploaded = await service.UploadAsync(
            "invoice.pdf",
            new MemoryStream([1, 2, 3]),
            "application/pdf");

        var result = await CreateReconciler().ReconcileAsync("documents/");

        Assert.False(result.AnyChanges);
        Assert.Equal(uploaded.Id, _repository.Documents.Values.Single().Id);
    }

    [Fact]
    public async Task Objects_outside_the_prefix_are_ignored()
    {
        PutObject("logs/app.log");

        var result = await CreateReconciler().ReconcileAsync("documents/");

        Assert.False(result.AnyChanges);
        Assert.Empty(_repository.Documents);
    }
}
