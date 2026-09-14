namespace StorageDemo.Core.Documents;

public enum ChangeKind
{
    Added,
    Updated,
    Removed,
}

public sealed record DocumentChange(ChangeKind Kind, Guid DocumentId, string StorageKey, string FileName);
