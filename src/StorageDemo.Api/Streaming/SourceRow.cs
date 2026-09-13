using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Api.Streaming;

/// <summary>
/// What an operator sees in the state column, which is three answers rather than two.
///
/// <see cref="NotOnAir"/> is the one that has to exist separately. A configured source with no
/// stream behind it has never arrived, or arrived and finished; an interrupted one is claimed,
/// buffered and expected back within the grace period. Collapsing them would tell an operator that
/// a source nobody has ever pushed is merely having a bad moment.
/// </summary>
public enum SourceState
{
    NotOnAir,
    Live,
    Interrupted,
}

/// <summary>
/// One line of the sources table: the configuration, whatever is on air under its name, and the
/// few figures derived from the pair of them.
///
/// A plain record built by a static method rather than logic inside the component, because this is
/// the only part of the page worth testing and a renderer is a needlessly expensive way to ask
/// whether a source with no stream reads as off air.
/// </summary>
/// <param name="LocalOutput">
/// The SRT address a player pulls this feed from, which is the answer to the question an operator
/// asks most often. Present whether or not the stream is on air: it is where the feed will be, and
/// handing it over before the encoder connects is how a viewer is set up in advance.
/// </param>
/// <param name="BitsPerSecond">
/// Null when no honest figure exists yet - the first refresh has nothing to subtract from, and a
/// counter that went backwards means the stream restarted rather than that throughput was
/// negative. The page shows total bytes in that case instead of inventing a rate.
/// </param>
public sealed record SourceRow(
    LiveSource Source,
    LiveStream? Stream,
    SourceState State,
    string LocalOutput,
    double? BitsPerSecond)
{
    private IReadOnlyList<ForwardStatus> Statuses => Stream?.Forwards ?? [];

    public int ForwardsConfigured => Source.Forwards.Count;

    public int ForwardsConnected => Statuses.Count(status => status.Connected);

    /// <summary>
    /// Every configured forward beside what it is actually doing, status null when the owning
    /// replica is reporting nothing for it.
    ///
    /// Driven from the configuration rather than from the statuses on purpose. A forward that was
    /// asked for and never started has no status at all, and listing only the statuses would hide
    /// exactly the forward the operator is looking for.
    /// </summary>
    public IEnumerable<(ForwardTarget Target, ForwardStatus? Status)> Detail
        => Source.Forwards.Select(target =>
            (target, Statuses.FirstOrDefault(status => status.Id == target.Id)));
}

/// <summary>
/// The page's arithmetic, kept out of the component so it can be exercised without a renderer.
/// </summary>
public static class StreamingRows
{
    /// <summary>
    /// Where a player pulls this source from this service.
    ///
    /// <see cref="LiveOptions.PublicConsumptionUrl"/> wins when set, because only the deployment
    /// knows the address a player can actually reach: the host the browser is talking to is the API
    /// port's, and behind an ingress or a port mapping it is frequently not where media is exposed.
    /// Falling back to that host and the configured consumption port is still the right guess for
    /// the standalone shape, where the two are the same machine.
    ///
    /// The name rides in the SRT <c>streamid</c>, escaped, in its bare form rather than the
    /// <c>#!::r=name,m=request</c> envelope. Both are accepted on the consumption port and the bare
    /// one is what an operator can retype into a player without counting punctuation.
    /// </summary>
    public static string LocalOutput(LiveOptions options, string? host, string name)
    {
        var address = string.IsNullOrWhiteSpace(options.PublicConsumptionUrl)
            ? $"srt://{(string.IsNullOrWhiteSpace(host) ? "localhost" : host)}:{options.ConsumptionPort}"
            : options.PublicConsumptionUrl.TrimEnd('/');

        return $"{address}?streamid={Uri.EscapeDataString(name)}";
    }

    /// <summary>
    /// The same scheme rule the coordinator applies before it opens anything, run in the browser so
    /// a typo costs a red line under the field rather than a round trip and a stack trace.
    ///
    /// Deliberately only the scheme. Whether the loaded FFmpeg actually carries that transport is
    /// a property of the server's build and cannot be answered here; the coordinator still checks
    /// it, and this is a first pass rather than the authority.
    /// </summary>
    public static bool SchemeAllowed(string? url, IReadOnlyList<string> allowed)
    {
        if (string.IsNullOrWhiteSpace(url))
        {
            return false;
        }

        var separator = url.IndexOf("://", StringComparison.Ordinal);

        // No scheme at all is "file" here for the same reason it is in the coordinator: an
        // unqualified path is one libav would happily read off the disk.
        var scheme = separator > 0 ? url[..separator] : "file";

        return allowed.Contains(scheme, StringComparer.OrdinalIgnoreCase);
    }

    public static SourceRow Build(
        LiveSource source,
        LiveStream? stream,
        LiveOptions options,
        string? host,
        double? bitsPerSecond = null)
        => new(
            source,
            stream,
            stream is null
                ? SourceState.NotOnAir
                : stream.State == LiveStreamState.Interrupted ? SourceState.Interrupted : SourceState.Live,
            LocalOutput(options, host, source.Name),
            bitsPerSecond);
}

/// <summary>
/// Turns the stream's running byte total into a rate, by remembering what it was last time.
///
/// The registry counts bytes since the stream started, which says nothing about whether a feed is
/// still carrying its bitrate now. Two samples a heartbeat apart do, and they are honest in a way
/// dividing the total by the uptime is not: a feed that stalled ten minutes ago still averages
/// nicely.
/// </summary>
public sealed class ThroughputMeter
{
    private readonly Dictionary<string, (long Bytes, DateTimeOffset At)> _last = new(StringComparer.Ordinal);

    /// <summary>Null until there is a previous sample to subtract, and after a counter reset.</summary>
    public double? Sample(string name, long bytes, DateTimeOffset at)
    {
        var known = _last.TryGetValue(name, out var previous);
        _last[name] = (bytes, at);

        if (!known)
        {
            return null;
        }

        var seconds = (at - previous.At).TotalSeconds;

        // A total that went backwards is a stream that ended and came back under the same name,
        // which is the ordinary case for a reconnecting encoder. Reporting the negative rate, or
        // clamping it to zero, would both be readings of something that did not happen.
        return seconds > 0 && bytes >= previous.Bytes
            ? (bytes - previous.Bytes) * 8 / seconds
            : null;
    }
}
