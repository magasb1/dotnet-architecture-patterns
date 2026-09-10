using Microsoft.Extensions.Options;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Api.Controllers;

/// <summary>
/// Fetches live bytes from the replica that holds them.
///
/// This is what lets a viewer keep talking to one address while the stream itself only exists on
/// one pod. The caller hits whichever replica the load balancer picked; that replica looks up the
/// owner in the shared registry and reads through to it over the cluster network.
///
/// Only inbound media needs per-pod addressing. Playback does not, because of this.
/// </summary>
public sealed class LivePeerProxy(
    IHttpClientFactory clients,
    LiveStreamManager manager,
    IOptions<LiveOptions> options,
    ILogger<LivePeerProxy> logger)
{
    private readonly LiveOptions _options = options.Value;

    public string Owner => manager.Owner;

    /// <summary>Null when the peer cannot be addressed or has nothing to give.</summary>
    public async Task<Stream?> OpenAsync(string owner, string path, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_options.PeerAddressTemplate))
        {
            logger.LogWarning(
                "Live session is owned by {Owner}, but Live:PeerAddressTemplate is not set, so it "
                + "cannot be reached from here",
                owner);

            return null;
        }

        var address = $"{_options.PeerAddressTemplate.Replace("{node}", owner).TrimEnd('/')}/{path}";

        try
        {
            var client = clients.CreateClient(nameof(LivePeerProxy));

            var response = await client.GetAsync(
                address,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            return await response.Content.ReadAsStreamAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not read a live stream from {Address}", address);
            return null;
        }
    }
}
