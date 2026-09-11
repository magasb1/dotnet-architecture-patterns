using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using StorageDemo.Core.Documents;
using StorageDemo.Grpc;

namespace StorageDemo.Client;

/// <summary>One row of the metadata panel.</summary>
public sealed record MetadataRow(string Key, string Value);

/// <summary>
/// One tile in the explorer.
///
/// A tile can exist before the server knows about it: an upload puts a pending tile on screen
/// immediately and fills in the real document when the call returns. That, plus the thumbnail
/// arriving later over the change feed, is what makes an upload feel instant.
/// </summary>
public sealed class DocumentItem : INotifyPropertyChanged
{
    private DocumentMessage? _document;
    private LiveStreamMessage? _live;
    private BitmapImage? _thumbnail;
    private string? _pendingName;
    private long _pendingSize;

    private DocumentItem()
    {
    }

    public DocumentItem(DocumentMessage document) => _document = document;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Null until the upload completes, and for a live session.</summary>
    public DocumentMessage? Document => _document;

    /// <summary>Set when this tile is a stream rather than a stored file.</summary>
    public LiveStreamMessage? Live => _live;

    public bool IsLive => _live is not null;

    public bool IsPending => _document is null && _live is null;

    /// <summary>
    /// A document's identifier, or a stream's name. The name is a stream's identity: a feed that
    /// drops and reconnects under it is the same stream resuming, so there is no other key.
    /// </summary>
    public string Id => _document?.Id ?? _live?.Name ?? string.Empty;

    public string FileName => _document?.FileName ?? _live?.Name ?? _pendingName ?? string.Empty;

    public long Size => _document?.Size ?? _live?.Bytes ?? _pendingSize;

    public bool HasThumbnail => _document?.HasThumbnail ?? _live?.HasPreview ?? false;

    /// <summary>An interrupted stream is dimmed rather than removed, so the tile stays put.</summary>
    public bool IsInterrupted => _live?.State == "Interrupted";

    public bool IsRecording => _live?.Recording is not null;

    /// <summary>A tile that is still uploading is as new as it gets.</summary>
    public DateTimeOffset CreatedAt => _document?.CreatedAt?.ToDateTimeOffset() ?? DateTimeOffset.UtcNow;

    public DocumentKind Kind => IsLive
        ? DocumentKind.Video
        : ContentTypes.KindOf(_document?.ContentType, FileName);

    public string Details => IsLive
        ? $"{_live!.State}{(_live.Recording is null ? string.Empty : "  REC")}  {HumanSize(Size)}"
        : IsPending
            ? "Uploading..."
            : $"{Icon}  {HumanSize(Size)}";

    public double Dimming => IsPending || IsInterrupted ? 0.45 : 1.0;

    /// <summary>A running stream is marked, because it is the one tile that changes on its own.</summary>
    public Visibility LiveBadgeVisibility => IsLive ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Everything the server's probe reported, in the order it reported it.</summary>
    public IReadOnlyList<MetadataRow> Metadata
    {
        get
        {
            if (_live is not null)
            {
                var rows = new List<MetadataRow>
                {
                    new("State", _live.State),
                    new("Source", _live.Manual ? "Created by request" : "Named itself on connect"),
                    new("Layout", _live.Layout),
                    new("Replica", _live.Owner),
                    new("Packets", _live.Packets.ToString("N0")),
                    new("Carried", HumanSize(_live.Bytes)),
                    new("Buffered", $"{_live.BufferedSeconds:0.#} s"),
                };

                if (_live.Recording is { } recording)
                {
                    // On the stream rather than in the document list, because a document appears
                    // only when there is a file.
                    rows.Add(new MetadataRow(
                        "Recording",
                        $"since {recording.StartedAt.ToDateTimeOffset().LocalDateTime:HH:mm:ss}, "
                        + HumanSize(recording.Bytes)));
                }

                if (!_live.Startable)
                {
                    // The operator's business: while this is true every pre-roll is empty and
                    // every snapshot is second-hand, and the fix is at the encoder.
                    rows.Add(new MetadataRow("Warning", "No keyframe recently enough to start from."));
                }

                if (_live.CeilingBinding)
                {
                    rows.Add(new MetadataRow("Warning", "The buffer's byte ceiling is binding, so "
                        + "pre-rolls are shorter than the window promises."));
                }

                return rows;
            }

            return _document is null
                ? []
                : [.. _document.Metadata.Select(entry => new MetadataRow(entry.Key, entry.Value))];
        }
    }

    public static DocumentItem ForLive(LiveStreamMessage live) => new() { _live = live };

    /// <summary>Refreshes a live tile in place, so its stats move without the tile being rebuilt.</summary>
    public void Apply(LiveStreamMessage live)
    {
        _live = live;

        Raise(
            nameof(Live),
            nameof(FileName),
            nameof(Size),
            nameof(HasThumbnail),
            nameof(Details),
            nameof(Dimming),
            nameof(IsInterrupted),
            nameof(IsRecording),
            nameof(Metadata));
    }

    public string Icon => Kind switch
    {
        DocumentKind.Text => "TXT",
        DocumentKind.Pdf => "PDF",
        DocumentKind.Image => "IMG",
        DocumentKind.Video => "VID",
        DocumentKind.Audio => "AUD",
        _ => "BIN",
    };

    /// <summary>
    /// Rendered by the server with ffmpeg, for images and video alike, so a tile costs one small
    /// JPEG rather than downloading and decoding the original.
    /// </summary>
    public BitmapImage? Thumbnail
    {
        get => _thumbnail;
        set
        {
            _thumbnail = value;
            Raise(nameof(Thumbnail), nameof(ThumbnailVisibility), nameof(IconVisibility), nameof(PlayBadgeVisibility));
        }
    }

    public Visibility ThumbnailVisibility => _thumbnail is null ? Visibility.Collapsed : Visibility.Visible;

    public Visibility IconVisibility => _thumbnail is null ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>A play badge over the tile, so a video reads as a video and not as a photo.</summary>
    public Visibility PlayBadgeVisibility =>
        Kind == DocumentKind.Video && _thumbnail is not null && !IsLive
            ? Visibility.Visible
            : Visibility.Collapsed;

    /// <summary>
    /// A tile for a file that is still uploading. Local images get a thumbnail straight away,
    /// because the bytes are right there on disk.
    /// </summary>
    public static DocumentItem Pending(string path)
    {
        var item = new DocumentItem
        {
            _pendingName = Path.GetFileName(path),
            _pendingSize = new FileInfo(path).Length,
        };

        if (ContentTypes.KindOf(null, item.FileName) == DocumentKind.Image)
        {
            item.Thumbnail = TryLoadLocalThumbnail(path);
        }

        return item;
    }

    /// <summary>Swaps in the real document once the server has answered, keeping the same tile.</summary>
    public void Apply(DocumentMessage document)
    {
        _document = document;

        Raise(
            nameof(Document),
            nameof(IsPending),
            nameof(Id),
            nameof(FileName),
            nameof(Size),
            nameof(HasThumbnail),
            nameof(Kind),
            nameof(Details),
            nameof(Dimming),
            nameof(Metadata),
            nameof(Icon),
            nameof(IconVisibility),
            nameof(PlayBadgeVisibility));
    }

    public static string HumanSize(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:0.#} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):0.#} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):0.#} GB",
    };

    private static BitmapImage? TryLoadLocalThumbnail(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.DecodePixelWidth = 160;
            bitmap.StreamSource = new MemoryStream(File.ReadAllBytes(path));
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch (Exception)
        {
            // A format WPF cannot decode simply shows the type icon until the server's one lands.
            return null;
        }
    }

    private void Raise(params string[] properties)
    {
        foreach (var property in properties)
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
        }
    }
}
