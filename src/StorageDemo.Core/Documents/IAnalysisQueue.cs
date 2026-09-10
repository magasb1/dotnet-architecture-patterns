namespace StorageDemo.Core.Documents;

/// <param name="Id">The document whose row gets the thumbnail and metadata.</param>
/// <param name="StorageKey">Where the bytes already are.</param>
public sealed record AnalysisRequest(Guid Id, string StorageKey, string FileName, string? ContentType);

/// <summary>
/// Work handed off so an upload can return as soon as the bytes and the row are safe. Probing a
/// video and decoding a frame takes seconds, and a caller should not wait for a preview image.
///
/// In one process this is a channel. Across replicas it has to be shared, or a pod that accepted
/// an upload is the only pod that can produce its thumbnail, and the work dies with that pod.
/// </summary>
public interface IAnalysisQueue
{
    Task EnqueueAsync(AnalysisRequest request, CancellationToken cancellationToken = default);

    /// <summary>Runs until cancelled. Several readers may share one queue; each item goes to one.</summary>
    IAsyncEnumerable<AnalysisRequest> DequeueAllAsync(CancellationToken cancellationToken);
}
