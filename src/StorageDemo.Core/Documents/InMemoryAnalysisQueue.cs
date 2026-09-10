using System.Threading.Channels;

namespace StorageDemo.Core.Documents;

/// <summary>
/// A queue inside one process. Unbounded because dropping a request would leave a document
/// permanently without its thumbnail, and each entry is four small fields rather than the file.
///
/// ponytail: work in flight is lost if the process dies, here and in the Redis queue alike. The
/// cost is a missing preview on one document, and re-uploading it fixes that. Claim-and-acknowledge
/// is the upgrade if a thumbnail ever has to be guaranteed.
/// </summary>
public sealed class InMemoryAnalysisQueue : IAnalysisQueue
{
    private readonly Channel<AnalysisRequest> _channel =
        Channel.CreateUnbounded<AnalysisRequest>(new UnboundedChannelOptions { SingleReader = true });

    public Task EnqueueAsync(AnalysisRequest request, CancellationToken cancellationToken = default)
    {
        _channel.Writer.TryWrite(request);
        return Task.CompletedTask;
    }

    public IAsyncEnumerable<AnalysisRequest> DequeueAllAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAllAsync(cancellationToken);
}
