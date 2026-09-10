using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using StorageDemo.Core.Streaming;
using StorageDemo.Infrastructure.Streaming;

namespace StorageDemo.Api.Controllers;

public sealed record StartIngestRequest(string Name, string Url);

public sealed record StartEgressRequest(Guid DocumentId, string Url);

public sealed record LiveStatusResponse(IReadOnlyList<string> Transports, IReadOnlyList<LiveSession> Sessions);

/// <summary>
/// Control plane for live streaming. REST rather than gRPC on purpose: these are a handful of
/// administrative calls, and the stream itself never travels over either. It is MPEG-TS on a UDP
/// or SRT socket, which is the point.
///
/// Any replica answers any call. Listing and stopping go through the shared registry, and the two
/// calls that need the actual bytes, the preview and the stream, are proxied to the replica that
/// holds them. A caller therefore never has to know which pod took the stream.
/// </summary>
[ApiController]
[Route("api/live")]
public sealed class LiveStreamsController(
    ILiveStreamService live,
    LivePeerProxy peers,
    IOptions<LiveOptions> options,
    ILogger<LiveStreamsController> logger) : ControllerBase
{
    private readonly LiveOptions _options = options.Value;

    [HttpGet]
    public async Task<ActionResult<LiveStatusResponse>> Status(
        [FromHeader(Name = "X-Storage-Token")] string? token,
        CancellationToken cancellationToken)
    {
        if (Guard(token) is { } refused)
        {
            return refused;
        }

        return new LiveStatusResponse(live.Transports, await live.SessionsAsync(cancellationToken));
    }

    /// <summary>Starts listening. The recording becomes a document when the stream ends.</summary>
    [HttpPost("ingest")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<LiveSession>> Ingest(
        StartIngestRequest request,
        [FromHeader(Name = "X-Storage-Token")] string? token,
        CancellationToken cancellationToken)
    {
        if (Guard(token) is { } refused)
        {
            return refused;
        }

        try
        {
            return Accepted(await live.StartIngestAsync(request.Name, request.Url, cancellationToken));
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(ex.Message);
        }
    }

    /// <summary>Multiplexes a stored document out to a destination.</summary>
    [HttpPost("egress")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    public async Task<ActionResult<LiveSession>> Egress(
        StartEgressRequest request,
        [FromHeader(Name = "X-Storage-Token")] string? token,
        CancellationToken cancellationToken)
    {
        if (Guard(token) is { } refused)
        {
            return refused;
        }

        try
        {
            return Accepted(await live.StartEgressAsync(request.DocumentId, request.Url, cancellationToken));
        }
        catch (NotSupportedException ex)
        {
            return BadRequest(ex.Message);
        }
        catch (InvalidOperationException ex)
        {
            return NotFound(ex.Message);
        }
    }

    /// <summary>The latest preview frame of a running stream.</summary>
    [HttpGet("{id:guid}/thumbnail")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Thumbnail(Guid id, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        // Deliberately unauthenticated, like a document thumbnail: it is a picture, and the client
        // renders it in an image tag rather than through an API call it can add headers to.
        if (live.Thumbnail(id) is { } local)
        {
            Response.Headers.XContentTypeOptions = "nosniff";
            return File(local, "image/jpeg");
        }

        var session = await live.GetAsync(id, cancellationToken);
        if (session is null || !session.HasThumbnail || session.IsFinished)
        {
            return NotFound();
        }

        return await ProxyAsync(session, $"api/live/{id}/thumbnail", "image/jpeg", cancellationToken);
    }

    /// <summary>
    /// The running stream, as MPEG-TS, for a player to pull. Ends when the stream does.
    /// </summary>
    [HttpGet("{id:guid}/stream")]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stream(Guid id, CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            return NotFound();
        }

        var session = await live.GetAsync(id, cancellationToken);
        if (session is null || session.IsFinished)
        {
            return NotFound();
        }

        if (!string.Equals(session.Owner, peers.Owner, StringComparison.Ordinal))
        {
            return await ProxyAsync(session, $"api/live/{id}/stream", "video/mp2t", cancellationToken);
        }

        Response.ContentType = "video/mp2t";
        Response.Headers.CacheControl = "no-store";

        try
        {
            await live.StreamAsync(id, Response.Body, cancellationToken);
        }
        catch (OperationCanceledException)
        {
            // The viewer closed the player. Normal.
        }
        catch (InvalidOperationException ex)
        {
            logger.LogDebug(ex, "Live stream {SessionId} ended while a viewer was attached", id);
        }

        return new EmptyResult();
    }

    [HttpDelete("{id:guid}")]
    [ProducesResponseType(StatusCodes.Status202Accepted)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> Stop(
        Guid id,
        [FromHeader(Name = "X-Storage-Token")] string? token,
        CancellationToken cancellationToken)
    {
        if (Guard(token) is { } refused)
        {
            return refused;
        }

        // Accepted rather than NoContent: the owner may be another replica, which acts shortly.
        return await live.StopAsync(id, cancellationToken) ? Accepted() : NotFound();
    }

    private async Task<IActionResult> ProxyAsync(
        LiveSession session,
        string path,
        string contentType,
        CancellationToken cancellationToken)
    {
        var upstream = await peers.OpenAsync(session.Owner, path, cancellationToken);

        if (upstream is null)
        {
            return NotFound();
        }

        Response.ContentType = contentType;
        Response.Headers.CacheControl = "no-store";

        try
        {
            await using var content = upstream;
            await content.CopyToAsync(Response.Body, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }

        return new EmptyResult();
    }

    /// <summary>Null when the caller may proceed.</summary>
    private ObjectResult? Guard(string? token)
    {
        if (!_options.Enabled)
        {
            // Not enabled means not here; do not advertise what is switched off.
            return NotFound(new ProblemDetails { Status = 404, Title = "Live streaming is disabled." });
        }

        if (string.IsNullOrEmpty(_options.Token))
        {
            return null;
        }

        var provided = Encoding.UTF8.GetBytes(token ?? string.Empty);
        var expected = Encoding.UTF8.GetBytes(_options.Token);

        return CryptographicOperations.FixedTimeEquals(provided, expected)
            ? null
            : new ObjectResult(new ProblemDetails { Status = 401, Title = "Bad token." }) { StatusCode = 401 };
    }
}
