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
///
/// A plain bool per port, because <c>srt_bind</c> and <c>srt_listen</c> either succeed or return an
/// error: a port is listening or it is not, and nothing has to be inferred from how long an attempt
/// took.
/// </summary>
public sealed class LiveListeners
{
    private readonly ConcurrentDictionary<StreamIntent, bool> _bound = new();

    /// <summary>False when live streaming is switched off, and then nothing here is a fault.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// Why this replica cannot serve media at all: no libsrt, so neither port can be opened. It is
    /// permanent for the life of the process and means every encoder the Service sends here would
    /// fail, so the pod should not be in the Service.
    /// </summary>
    public string? Fault { get; set; }

    /// <summary>Called by the listener once <c>srt_listen</c> has succeeded.</summary>
    public void Bound(StreamIntent port) => _bound[port] = true;

    public void Stopped(StreamIntent port) => _bound.TryRemove(port, out _);

    public bool IsListening(StreamIntent port) => _bound.ContainsKey(port);

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
