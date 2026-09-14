using Microsoft.EntityFrameworkCore;
using Npgsql;
using StorageDemo.Core.Documents;

namespace StorageDemo.Infrastructure.Database.PostgreSql;

public sealed class PostgresDocumentRepository(AppDbContext db) : IDocumentRepository
{
    public async Task<Document?> GetAsync(Guid id, CancellationToken cancellationToken = default)
        => await Run(() => db.Documents.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id, cancellationToken));

    public async Task<IReadOnlyList<Document>> GetAllAsync(CancellationToken cancellationToken = default)
        => await Run(async () => (IReadOnlyList<Document>)await db.Documents
            .AsNoTracking()
            .OrderByDescending(d => d.CreatedAt)
            .ToListAsync(cancellationToken));

    public async Task AddAsync(Document document, CancellationToken cancellationToken = default)
        => await Run(async () =>
        {
            db.Documents.Add(document);
            await db.SaveChangesAsync(cancellationToken);
            db.Entry(document).State = EntityState.Detached;
            return true;
        });

    public async Task UpsertAsync(Document document, CancellationToken cancellationToken = default)
        => await Run(async () =>
        {
            // No ON CONFLICT here: the demo's write volume does not justify raw SQL.
            var existing = await db.Documents.FirstOrDefaultAsync(d => d.Id == document.Id, cancellationToken);

            if (existing is null)
            {
                db.Documents.Add(document);
            }
            else
            {
                db.Entry(existing).CurrentValues.SetValues(document);
            }

            await db.SaveChangesAsync(cancellationToken);
            db.ChangeTracker.Clear();
            return true;
        });

    public async Task DeleteAsync(Guid id, CancellationToken cancellationToken = default)
        => await Run(async () =>
        {
            // ExecuteDelete is a no-op when the row is gone, which is the idempotency we want.
            await db.Documents.Where(d => d.Id == id).ExecuteDeleteAsync(cancellationToken);
            return true;
        });

    private static async Task<T> Run<T>(Func<Task<T>> action)
    {
        try
        {
            return await action();
        }
        catch (DbUpdateException ex)
        {
            throw new PersistenceException("PostgreSQL write failed.", ex);
        }
        catch (NpgsqlException ex)
        {
            throw new PersistenceException("PostgreSQL operation failed.", ex);
        }
    }
}
