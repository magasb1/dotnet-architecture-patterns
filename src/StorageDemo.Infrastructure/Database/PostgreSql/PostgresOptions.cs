using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Infrastructure.Database.PostgreSql;

public sealed class PostgresOptions
{
    public const string SectionName = "Database:Postgres";

    [Required(AllowEmptyStrings = false)]
    public string ConnectionString { get; init; } = string.Empty;
}
