using System.ComponentModel.DataAnnotations;

namespace StorageDemo.Infrastructure.FileStorage.FileSystem;

public sealed class FileSystemStorageOptions
{
    public const string SectionName = "Storage:FileSystem";

    [Required(AllowEmptyStrings = false)]
    public string RootPath { get; init; } = "./data/files";
}
