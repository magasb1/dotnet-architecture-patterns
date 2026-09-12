using System.Diagnostics.Metrics;
using System.Globalization;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// What this pod publishes about itself, for an autoscaler and for anyone holding
/// <c>dotnet-counters monitor --counters StorageDemo.Live</c>.
///
/// Aggregates only. The per-stream question - which of my thousand streams is broken - is answered
/// by <c>GET /api/live</c>, which already lists every stream and now carries each one's loss and
/// drop figures. Nothing here is tagged with a stream name, and nothing here should ever be: a
/// thousand streams times several instruments is thousands of time series, every one of them
/// retained by whatever scrapes it long after the stream ended, to answer a question the API
/// answers for free. Every tag on this class has a small, fixed set of values.
///
/// No exporter. There is no autoscaler yet, and the Prometheus exporter has never shipped a stable
/// release; when there is one it is two calls in Program.cs, and until then dotnet-counters reads
/// all of this with no package at all.
/// </summary>
public sealed class LiveMetrics : IDisposable
{
    public const string MeterName = "StorageDemo.Live";

    /// <summary>
    /// Where Linux keeps the UDP counters. Nothing else in this service reads /proc, so the path
    /// lives here rather than in a constants file with one member.
    /// </summary>
    private const string Snmp = "/proc/net/snmp";

    private readonly Meter _meter;

    private readonly Counter<long> _accepts;

    private readonly Counter<long> _rejects;

    private int _streamsOwned;

    public LiveMetrics()
    {
        // Scoped to this instance so that two hosts in one process - which is how the replica tests
        // run - publish distinguishable meters rather than one name twice.
        _meter = new Meter(new MeterOptions(MeterName) { Scope = this });

        // An up-down counter rather than a gauge, which is what the documentation prescribes for
        // the size of a set. Observed rather than incremented, because the set it reports is a
        // dictionary that is added to and removed from on several threads and the count is the only
        // thing that has to be right.
        _meter.CreateObservableUpDownCounter(
            "live.streams.owned",
            () => Volatile.Read(ref _streamsOwned),
            description: "Live streams this replica currently holds the connection for.");

        _accepts = _meter.CreateCounter<long>(
            "live.accepts",
            description: "Callers accepted, by the port they arrived on.");

        _rejects = _meter.CreateCounter<long>(
            "live.rejects",
            description: "Callers refused at the handshake, by port and by why.");

        // The counter that sees what never reached libsrt. Every per-socket figure is blind to it:
        // a packet dropped by the kernel because the receive buffer was full was never delivered to
        // any socket, so the only place it is counted is here. It is what the 250-stream baseline
        // actually hit - 319,000 of these in fifteen seconds while every stream reported itself
        // healthy - and it is the reason a count of streams owned cannot be trusted alone.
        _meter.CreateObservableCounter(
            "live.udp.receive.errors",
            ObserveReceiveErrors,
            description: "Kernel UDP receive errors for this pod's whole network namespace.");
    }

    /// <summary>
    /// How many streams this replica holds, republished by the heartbeat. Read on the collection
    /// thread, which must never block, so it is a field and not a walk of a dictionary.
    /// </summary>
    public int StreamsOwned
    {
        get => Volatile.Read(ref _streamsOwned);
        set => Volatile.Write(ref _streamsOwned, value);
    }

    public void Accepted(int port)
        => _accepts.Add(1, new KeyValuePair<string, object?>("port", port));

    /// <param name="reason">
    /// One of a handful of words, never anything derived from what the caller sent. A reason taken
    /// from a stream identifier would let whoever is connecting choose this metric's cardinality.
    /// </param>
    public void Rejected(int port, string reason)
        => _rejects.Add(
            1,
            new KeyValuePair<string, object?>("port", port),
            new KeyValuePair<string, object?>("reason", reason));

    /// <summary>
    /// UDP datagrams the kernel counted as receive errors since boot, or null where there is no
    /// such number to read.
    ///
    /// Linux only, by nature: this is /proc/net/snmp's InErrors, the figure netstat prints as
    /// "packet receive errors". Windows and macOS have no equivalent that is worth faking, and a
    /// developer machine must not throw or report a comforting zero for a counter it cannot see, so
    /// the honest answer there is nothing at all.
    /// </summary>
    public static long? KernelUdpReceiveErrors()
    {
        try
        {
            string? header = null;

            foreach (var line in File.ReadLines(Snmp))
            {
                if (!line.StartsWith("Udp:", StringComparison.Ordinal))
                {
                    continue;
                }

                // The file gives each protocol twice: the column names, then the values. Read by
                // name rather than by position, because which columns exist depends on the kernel.
                if (header is null)
                {
                    header = line;

                    continue;
                }

                var columns = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var values = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                var at = Array.IndexOf(columns, "InErrors");

                return at > 0 && at < values.Length
                    && long.TryParse(values[at], CultureInfo.InvariantCulture, out var errors)
                        ? errors
                        : null;
            }

            return null;
        }
        catch (Exception)
        {
            // A health signal that throws is worse than one that is absent, and every way this can
            // fail - no such file, no permission, a kernel that words the file differently - has
            // the same answer.
            return null;
        }
    }

    public void Dispose() => _meter.Dispose();

    private static IEnumerable<Measurement<long>> ObserveReceiveErrors()
        => KernelUdpReceiveErrors() is { } errors ? [new Measurement<long>(errors)] : [];
}
