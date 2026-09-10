using Microsoft.Extensions.Logging;

namespace StorageDemo.Infrastructure.Seeding;

/// <summary>
/// Runs in every environment, production included. This demo has no reference data yet,
/// so it exists to mark where lookup tables and configuration rows would go.
/// </summary>
public sealed class ReferenceDataSeeder(ILogger<ReferenceDataSeeder> logger) : ISeeder
{
    public Task SeedAsync(CancellationToken cancellationToken = default)
    {
        logger.LogInformation("Reference data seed complete (nothing to insert)");
        return Task.CompletedTask;
    }
}
