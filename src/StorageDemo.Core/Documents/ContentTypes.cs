using HeyRed.Mime;

namespace StorageDemo.Core.Documents;

/// <summary>Maps a file name to a media type, and a media type to how the UI should show it.</summary>
public static class ContentTypes
{
    public const string Unknown = "application/octet-stream";

    /// <summary>
    /// MimeTypesMap carries the whole IANA-ish table, so only the handful it answers unhelpfully
    /// for a document store live here.
    /// </summary>
    private static readonly Dictionary<string, string> Overrides = new(StringComparer.OrdinalIgnoreCase)
    {
        // Shared with TypeScript, and a .ts in a file store is a transport stream.
        [".ts"] = "video/mp2t",
        [".m2ts"] = "video/mp2t",
        [".mts"] = "video/mp2t",
    };

    public static string Guess(string fileName)
    {
        var extension = Path.GetExtension(fileName);

        if (Overrides.TryGetValue(extension, out var overridden))
        {
            return overridden;
        }

        return string.IsNullOrEmpty(extension) ? Unknown : MimeTypesMap.GetMimeType(fileName);
    }

    public static DocumentKind KindOf(string? contentType, string fileName)
    {
        var type = string.IsNullOrWhiteSpace(contentType) || contentType == Unknown
            ? Guess(fileName)
            : contentType;

        if (type.StartsWith("image/", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentKind.Image;
        }

        if (type.StartsWith("video/", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentKind.Video;
        }

        if (type.StartsWith("audio/", StringComparison.OrdinalIgnoreCase))
        {
            return DocumentKind.Audio;
        }

        if (type == "application/pdf")
        {
            return DocumentKind.Pdf;
        }

        return type.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
            || type is "application/json" or "application/xml"
            ? DocumentKind.Text
            : DocumentKind.Other;
    }
}

public enum DocumentKind
{
    Other,
    Text,
    Pdf,
    Image,
    Video,
    Audio,
}
