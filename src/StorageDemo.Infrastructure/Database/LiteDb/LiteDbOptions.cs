using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Infrastructure.Database.LiteDb;

public sealed class LiteDbOptions
{
    public const string SectionName = "Database:LiteDb";

    [Required(AllowEmptyStrings = false)]
    public string Path { get; init; } = "./data/database/app.db";
}
