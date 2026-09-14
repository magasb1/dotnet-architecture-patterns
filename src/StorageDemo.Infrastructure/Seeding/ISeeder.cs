namespace StorageDemo.Infrastructure.Seeding;

/// <summary>Seeders must be idempotent: startup runs them on every boot.</summary>
public interface ISeeder
{
    Task SeedAsync(CancellationToken cancellationToken = default);
}
