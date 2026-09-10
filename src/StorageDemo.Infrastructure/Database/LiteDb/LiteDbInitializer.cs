using LiteDB;
using Microsoft.Extensions.Logging;
using StorageDemo.Core.Documents;

namespace StorageDemo.Infrastructure.Database.LiteDb;

public sealed class LiteDbInitializer(ILiteDatabase database, ILogger<LiteDbInitializer> logger)
    : IDatabaseInitializer
{
    public Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        var documents = database.GetCollection<DocumentRecord>(LiteDbDocumentRepository.CollectionName);
        documents.EnsureIndex(d => d.Id, unique: true);

        logger.LogInformation("LiteDB initialized {DatabaseProvider}", "LiteDb");
        return Task.CompletedTask;
    }
}
