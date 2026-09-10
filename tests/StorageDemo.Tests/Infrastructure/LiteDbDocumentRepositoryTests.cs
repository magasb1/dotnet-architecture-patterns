using LiteDB;
using Microsoft.Extensions.Logging.Abstractions;
using StorageDemo.Core.Documents;
using StorageDemo.Infrastructure.Database.LiteDb;

namespace StorageDemo.Tests.Infrastructure;

public sealed class LiteDbDocumentRepositoryTests : DocumentRepositoryContract, IDisposable
{
    // In-memory LiteDB: same engine and mapping, no temp file to clean up.
    private readonly LiteDatabase _database = new(":memory:");

    protected override IDocumentRepository CreateRepository()
    {
        new LiteDbInitializer(_database, NullLogger<LiteDbInitializer>.Instance)
            .InitializeAsync()
            .GetAwaiter()
            .GetResult();

        return new LiteDbDocumentRepository(_database);
    }

    public void Dispose() => _database.Dispose();
}
