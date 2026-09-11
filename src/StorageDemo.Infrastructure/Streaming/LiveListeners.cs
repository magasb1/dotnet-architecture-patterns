using System.Collections.Concurrent;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// What the two media listeners are actually doing, so readiness can be about the thing that
/// matters.
///
/// For a service whose whole purpose is being live, a replica that is reachable but not accepting
/// is worse than one that is plainly absent: a load balancer keeps sending encoders to it and they
/// keep failing. Reporting the listener rather than only the process is what lets Kubernetes take
/// such a replica out of the Service until it can serve.
/// </summary>
public sealed class LiveListeners
{
    /// <summary>
    /// How stale a listener's last confirmed bind may be before readiness stops believing in it.
    /// Generous next to the accept loop's one second listen window, so an ordinary busy moment
    /// never flaps a pod out of the Service.
    /// </summary>
    public static readonly TimeSpan Freshness = TimeSpan.FromSeconds(15);

    private readonly ConcurrentDictionary<StreamIntent, DateTimeOffset> _bound = new();

    /// <summary>False when live streaming is switched off, and then nothing here is a fault.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Why this replica cannot ingest at all: a failed stream identifier self-test, or an FFmpeg
    /// with no SRT in it. Both are permanent for the life of the process, and both mean every
    /// stream arriving here would be broken, so the pod should not be in the Service.
    /// </summary>
    public string? Fault { get; set; }

    /// <summary>Called by the accept loop whenever it has confirmed the port is bound.</summary>
    public void Bound(StreamIntent port) => _bound[port] = DateTimeOffset.UtcNow;

    public void Stopped(StreamIntent port) => _bound.TryRemove(port, out _);

    public DateTimeOffset? BoundAt(StreamIntent port)
        => _bound.TryGetValue(port, out var at) ? at : null;

    public bool IsListening(StreamIntent port)
        => BoundAt(port) is { } at && DateTimeOffset.UtcNow - at <= Freshness;

    /// <summary>Null when this replica can serve encoders and viewers; otherwise why it cannot.</summary>
    public string? NotServing()
    {
        if (!Enabled)
        {
            return null;
        }

        if (Fault is { Length: > 0 } fault)
        {
            return fault;
        }

        if (!IsListening(StreamIntent.Publish))
        {
            return "the ingest port is not accepting";
        }

        return IsListening(StreamIntent.Subscribe) ? null : "the consumption port is not accepting";
    }
}
