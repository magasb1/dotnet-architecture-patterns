using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Streaming;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// Configured sources in a JSON file, which is what standalone runs on.
///
/// Deliberately not the in-memory shape <see cref="InMemoryLiveStreamRegistry"/> takes. That
/// registry may live in a dictionary because it holds a reading, and a reading that is lost on
/// restart was going to be replaced by the next one anyway. A source is a setting somebody typed,
/// and losing it because a pod moved would be a bug rather than a stale view, so the single-process
/// shape still has to reach a disk.
///
/// The file is read once and then served from memory, because a thousand heartbeats a minute must
/// not each open a file, and this process is the only writer. Every save rewrites the whole file.
///
/// ponytail: whole-file rewrite, which costs one serialisation of every row per save. The list is
/// tens of rows written by hand, so the rewrite is microseconds and the alternative - a per-row file
/// or an append log with compaction - buys nothing and adds a recovery path to get wrong. If sources
/// ever become machine-generated in the thousands, move to Redis rather than making this cleverer:
/// that scale has replicas, and replicas need the shared store anyway.
/// </summary>
public sealed class FileLiveSourceStore : ILiveSourceStore
{
    private readonly string _path;
    private readonly ILogger<FileLiveSourceStore> _logger;

    // One lock over both the load and every save. A reader-writer split would be free concurrency
    // in theory, but the contended case here is two operators clicking save at the same second.
    private readonly SemaphoreSlim _gate = new(1, 1);

    private Dictionary<string, LiveSource>? _sources;

    public FileLiveSourceStore(IOptions<LiveOptions> options, ILogger<FileLiveSourceStore> logger)
    {
        var live = options.Value;

        // Resolved here rather than as a property initialiser because it depends on another
        // property, and an initialiser would capture the default recording directory even when the
        // deployment configured one.
        _path = live.SourceFile is { Length: > 0 } configured
            ? configured
            : Path.Combine(
                live.RecordingDirectory is { Length: > 0 } directory
                    ? directory
                    : Path.Combine(Path.GetTempPath(), "storagedemo-live"),
                "sources.json");

        _logger = logger;
    }

    public async Task<IReadOnlyList<LiveSource>> ListAsync(CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return [.. Load().Values.OrderBy(source => source.Name, StringComparer.Ordinal)];
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<LiveSource?> GetAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            return Load().GetValueOrDefault(name);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task SaveAsync(LiveSource source, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var sources = Load();
            sources[source.Name] = source;

            await WriteAsync(sources, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task RemoveAsync(string name, CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);

        try
        {
            var sources = Load();

            if (sources.Remove(name))
            {
                await WriteAsync(sources, cancellationToken);
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The in-memory copy, read from disk the first time anything asks. Lazy rather than in the
    /// constructor so a bad path fails the operation that touches it rather than the container's
    /// startup, where it would read as "the service is broken" instead of "this store is".
    /// </summary>
    private Dictionary<string, LiveSource> Load()
    {
        if (_sources is not null)
        {
            return _sources;
        }

        return _sources = Read().ToDictionary(source => source.Name, StringComparer.Ordinal);
    }

    private List<LiveSource> Read()
    {
        if (!File.Exists(_path))
        {
            // Nobody has configured a source yet, which is the ordinary state of a fresh install.
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<LiveSource>>(File.ReadAllText(_path)) ?? [];
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            // Starting empty rather than throwing, because refusing to start over an unreadable
            // configuration file takes the whole service down - including every stream an encoder
            // is pushing, which needs no source at all. The cost is that the next save overwrites
            // whatever was in there, so the file is moved aside first and the operator can put its
            // rows back by hand.
            var quarantine = _path + ".corrupt";

            try
            {
                File.Move(_path, quarantine, overwrite: true);
                _logger.LogError(ex, "Unreadable source file, moved to '{Path}' and starting empty", quarantine);
            }
            catch (Exception moveFailed) when (moveFailed is IOException or UnauthorizedAccessException)
            {
                _logger.LogError(ex, "Unreadable source file '{Path}', starting empty", _path);
                _logger.LogError(moveFailed, "Could not move the unreadable source file aside");
            }

            return [];
        }
    }

    /// <summary>
    /// Writes through a temporary file in the same directory, so a crash or a full disk halfway
    /// through leaves the previous list intact rather than a half-written one that the next start
    /// would quarantine. Same directory because <see cref="File.Move(string, string, bool)"/> is
    /// only atomic within a volume.
    /// </summary>
    private async Task WriteAsync(
        Dictionary<string, LiveSource> sources,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);

        var temporary = _path + ".tmp";
        var ordered = sources.Values.OrderBy(source => source.Name, StringComparer.Ordinal);

        // Indented: this file is meant to be read, and edited in anger, by a person.
        await File.WriteAllTextAsync(
            temporary,
            JsonSerializer.Serialize(ordered, IndentedJson),
            cancellationToken);

        File.Move(temporary, _path, overwrite: true);
    }

    private static readonly JsonSerializerOptions IndentedJson = new() { WriteIndented = true };
}
