using StorageDemo.Core.Streaming;

namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// What the owner knows about detection on one of its streams: the toggle, which worker holds it,
/// and a short ring of the VMTI frames that worker has posted, beside the KLV ring the
/// <see cref="KlvExtractor"/> keeps. The owner never decodes and never detects; it carries.
///
/// The worker's hold is a lease. A worker renews it by claiming again every beat, and one that
/// stops renewing - because it died holding the stream - loses it after <see cref="WorkerLease"/>,
/// which is what lets another worker pick the stream up. That is the pull-lease shape from
/// detection-plan.md: nothing pushes work to a worker, and nothing has to notice a worker dying.
/// </summary>
public sealed class StreamDetection
{
    /// <summary>
    /// Bounded by count like the KLV ring, and much smaller: a VMTI frame arrives once per
    /// detection rather than per metadata packet, so sixteen is three seconds at the five a
    /// second the plan sizes for and sixteen seconds at the one a second a processor manages.
    /// Only the newest is served today; the ring exists so a capture can be matched to the frame
    /// that caused it a moment ago, which is the same reason the KLV ring is more than one deep.
    /// </summary>
    private const int RingSize = 16;

    /// <summary>
    /// Five beats of the worker's poll. Longer than the three the owner heartbeat allows itself,
    /// because a worker's beat also waits on an HTTP listing and losing a stream costs a reconnect
    /// and a tracker reset, which is worse than a few seconds without a detector.
    /// </summary>
    public static readonly TimeSpan WorkerLease = TimeSpan.FromSeconds(10);

    private readonly Lock _gate = new();
    private readonly VmtiSample?[] _ring = new VmtiSample?[RingSize];
    private int _next;
    private int _count;
    private string? _worker;
    private DateTimeOffset _workerSeenAt;

    public bool Enabled { get; private set; }

    public int Rate { get; private set; }

    /// <summary>The worker holding the stream, or null when none does or the holder's lease has lapsed.</summary>
    public string? Worker
    {
        get
        {
            lock (_gate)
            {
                return DateTimeOffset.UtcNow - _workerSeenAt <= WorkerLease ? _worker : null;
            }
        }
    }

    public VmtiSample? Latest
    {
        get { lock (_gate) { return _count == 0 ? null : _ring[(_next - 1 + RingSize) % RingSize]; } }
    }

    public void Set(bool enabled, int rate)
    {
        lock (_gate)
        {
            Enabled = enabled;
            Rate = rate;

            if (!enabled)
            {
                _worker = null;
            }
        }
    }

    /// <summary>False when another worker's lease is still live; the same worker always renews.</summary>
    public bool TryClaim(string worker)
    {
        lock (_gate)
        {
            if (Worker is { } holder && holder != worker)
            {
                return false;
            }

            _worker = worker;
            _workerSeenAt = DateTimeOffset.UtcNow;

            return true;
        }
    }

    public bool Release(string worker)
    {
        lock (_gate)
        {
            if (_worker != worker)
            {
                return false;
            }

            _worker = null;

            return true;
        }
    }

    public void Post(VmtiSample sample)
    {
        lock (_gate)
        {
            _ring[_next] = sample;
            _next = (_next + 1) % RingSize;
            _count = Math.Min(_count + 1, RingSize);
        }
    }

    /// <summary>
    /// Takes over the state a previous owner published, when this replica resumes a stream that
    /// moved. The toggle is what the operator set and must survive the move; the worker keeps its
    /// hold for one lease so it can re-subscribe here rather than being displaced by the move.
    /// </summary>
    public void Adopt(LiveStream shared)
    {
        lock (_gate)
        {
            Enabled = shared.DetectionEnabled;
            Rate = shared.DetectionRate;
            _worker = shared.DetectionWorker;
            _workerSeenAt = DateTimeOffset.UtcNow;
        }
    }
}
