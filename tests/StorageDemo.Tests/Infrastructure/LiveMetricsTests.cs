using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>
/// What this pod says about itself, as whatever is reading the meter would see it.
///
/// The per-stream half of Phase 9 is tested against a real socket in <see cref="SrtListenerTests"/>
/// and through the API in the integration suite; this is the aggregate half, which has no network
/// in it at all.
/// </summary>
public sealed class LiveMetricsTests
{
    [Fact]
    public void The_owned_gauge_reports_what_the_heartbeat_last_told_it()
    {
        using var metrics = new LiveMetrics { StreamsOwned = 7 };
        using var meters = new Meters(metrics);

        Assert.Equal(7, meters.Value("live.streams.owned"));

        metrics.StreamsOwned = 0;

        Assert.Equal(0, meters.Value("live.streams.owned"));
    }

    /// <summary>
    /// Both counters carry the port, and a reject carries why. Neither carries a stream name, which
    /// is the difference between four time series and four thousand.
    /// </summary>
    [Fact]
    public void Accepts_and_rejects_are_tagged_by_port_and_by_reason_and_by_nothing_else()
    {
        using var metrics = new LiveMetrics();
        using var meters = new Meters(metrics);

        metrics.Accepted(9000);
        metrics.Rejected(9000, "conflict");

        var recorded = meters.Read();

        var accept = Assert.Single(recorded, measurement => measurement.Instrument == "live.accepts");
        var reject = Assert.Single(recorded, measurement => measurement.Instrument == "live.rejects");

        Assert.Equal(1, accept.Value);
        Assert.Equal([new KeyValuePair<string, object?>("port", 9000)], accept.Tags);

        Assert.Equal(1, reject.Value);
        Assert.Equal(
            [
                new KeyValuePair<string, object?>("port", 9000),
                new KeyValuePair<string, object?>("reason", "conflict"),
            ],
            reject.Tags);
    }

    /// <summary>
    /// The counter that catches what never reached libsrt, on a machine that has no such counter.
    ///
    /// A developer's Windows box has no /proc, and this is the path where a health signal is most
    /// tempting to fake: reporting zero would read as "no packets were lost" on a machine that
    /// cannot know. It has to be absent instead, and above all it must not throw, because it is
    /// read from a metrics callback that nothing is catching exceptions for.
    /// </summary>
    [Fact]
    public void The_kernel_udp_counter_is_absent_rather_than_fatal_where_proc_is_not()
    {
        var errors = LiveMetrics.KernelUdpReceiveErrors();

        if (OperatingSystem.IsLinux())
        {
            Assert.NotNull(errors);
            Assert.True(errors >= 0);
        }
        else
        {
            Assert.Null(errors);
        }
    }

    /// <summary>
    /// The same absence, seen from the meter: an instrument with nothing to report publishes no
    /// measurement rather than a zero.
    /// </summary>
    [Fact]
    public void The_kernel_udp_counter_publishes_a_measurement_only_where_there_is_one()
    {
        using var metrics = new LiveMetrics();
        using var meters = new Meters(metrics);

        Assert.Equal(OperatingSystem.IsLinux(), meters.Value("live.udp.receive.errors") is not null);
    }
}

/// <summary>
/// Collects one <see cref="LiveMetrics"/> instance's measurements, and only that instance's, for as
/// long as it is not disposed.
///
/// The filter is the meter's scope rather than its name: several hosts run in one test process and
/// each publishes a meter of the same name.
///
/// It has to be started before whatever it is measuring, because a counter is an event and not a
/// value: a listener that starts afterwards sees nothing at all, however many times the counter was
/// added to. Observable instruments are the other way round and report only when asked.
/// </summary>
internal sealed class Meters : IDisposable
{
    internal readonly record struct Recording(
        string Instrument,
        long Value,
        IReadOnlyList<KeyValuePair<string, object?>> Tags);

    private readonly ConcurrentQueue<Recording> _seen = new();

    private readonly MeterListener _listener;

    public Meters(LiveMetrics metrics)
    {
        _listener = new MeterListener
        {
            InstrumentPublished = (instrument, listening) =>
            {
                if (ReferenceEquals(instrument.Meter.Scope, metrics))
                {
                    listening.EnableMeasurementEvents(instrument);
                }
            },
        };

        _listener.SetMeasurementEventCallback<long>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.SetMeasurementEventCallback<int>((instrument, value, tags, _) => Add(instrument, value, tags));
        _listener.Start();
    }

    public IReadOnlyList<Recording> Read()
    {
        _listener.RecordObservableInstruments();

        return [.. _seen];
    }

    /// <summary>The newest measurement of one instrument, or null if it reported none.</summary>
    public long? Value(string instrument)
        => Read()
            .Where(recording => recording.Instrument == instrument)
            .Select(recording => (long?)recording.Value)
            .LastOrDefault();

    public void Dispose() => _listener.Dispose();

    private void Add(Instrument instrument, long value, ReadOnlySpan<KeyValuePair<string, object?>> tags)
        => _seen.Enqueue(new Recording(instrument.Name, value, tags.ToArray()));
}
