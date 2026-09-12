using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Api.Controllers;

/// <summary>
/// Reaches the replica that owns a stream.
///
/// Only the owner has the bytes and only the owner can act on a stream, so a call landing anywhere
/// else is routed rather than refused. This is what lets a caller keep talking to one address
/// while the stream itself exists on exactly one pod.
///
/// The address is the one the owner recorded when it claimed the name, not one derived from a
/// predictable pod name. That is what allows a Deployment instead of a StatefulSet, and it deletes
/// the headless Service and the per-pod ingest Services with it.
///
/// Media never travels over the API port to a viewer. It travels over it between two pods, which is
/// what this was built for: a viewer that lands on the wrong replica is served from the owner
/// through here, and only the last hop to the player is SRT.
/// </summary>
public sealed class LivePeerProxy(
    IHttpClientFactory clients,
    ILiveStreamService live,
    ILogger<LivePeerProxy> logger)
{
    public string Owner => live.Owner;

    /// <summary>Reads a stream of bytes from the owner. Null when it cannot be reached.</summary>
    public async Task<Stream?> OpenAsync(LiveStream stream, string path, CancellationToken cancellationToken)
    {
        if (Address(stream, path) is not { } address)
        {
            return null;
        }

        try
        {
            var response = await clients.CreateClient(LiveOptions.PeerClient).GetAsync(
                address,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStreamAsync(cancellationToken)
                : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not read from {Address}", address);
            return null;
        }
    }

    /// <summary>
    /// Repeats a control call against the owner and hands back its answer. Null when the owner
    /// cannot be reached, which the caller reports as the stream not being found: an owner nobody
    /// can talk to is indistinguishable from a stream that is gone.
    /// </summary>
    public async Task<IActionResult?> RelayAsync(
        LiveStream stream,
        HttpMethod method,
        string path,
        string? token,
        object? body,
        CancellationToken cancellationToken)
    {
        if (Address(stream, path) is not { } address)
        {
            return null;
        }

        using var request = new HttpRequestMessage(method, address);

        if (token is { Length: > 0 })
        {
            request.Headers.Add("X-Storage-Token", token);
        }

        if (body is not null)
        {
            request.Content = JsonContent.Create(body);
        }

        try
        {
            using var response = await clients.CreateClient(LiveOptions.PeerClient)
                .SendAsync(request, cancellationToken);

            var content = await response.Content.ReadAsStringAsync(cancellationToken);

            return new ContentResult
            {
                StatusCode = (int)response.StatusCode,
                Content = content,
                ContentType = response.Content.Headers.ContentType?.ToString() ?? "application/json",
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not reach {Address}", address);
            return null;
        }
    }

    private string? Address(LiveStream stream, string path)
    {
        if (string.IsNullOrWhiteSpace(stream.OwnerAddress))
        {
            logger.LogWarning(
                "'{Name}' is owned by {Owner}, which recorded no address, so it cannot be reached "
                + "from here. Set Live:PeerBaseUrl on every replica.",
                stream.Name,
                stream.Owner);

            return null;
        }

        return $"{stream.OwnerAddress.TrimEnd('/')}/{path}";
    }
}
