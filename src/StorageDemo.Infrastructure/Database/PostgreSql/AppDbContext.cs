using Microsoft.EntityFrameworkCore;
using StorageDemo.Core.Documents;

namespace StorageDemo.Infrastructure.Database.PostgreSql;

public sealed class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Document> Documents => Set<Document>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
        => modelBuilder.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);
}
