namespace StorageDemo.Core.Streaming;

/// <summary>
/// Decides when a running session has actually died with its owner.
///
/// A pod killed mid-stream leaves a record saying "active" that nothing will ever update. Rather
/// than have the API report a stream that stopped minutes ago, a session whose owner has gone
/// quiet is reported as failed. The registry never rewrites the stored record for this: the owner
/// might come back, and it is the reader that needs the truth.
/// </summary>
public static class LiveSessionStaleness
{
    /// <summary>Generous enough to survive a slow pass or a pause in the stream.</summary>
    public static TimeSpan Timeout { get; } = TimeSpan.FromSeconds(30);

    public static LiveSession Apply(LiveSession session)
    {
        if (session.IsFinished || session.Heartbeat is not { } heartbeat)
        {
            return session;
        }

        return DateTimeOffset.UtcNow - heartbeat <= Timeout
            ? session
            : session with
            {
                State = LiveSessionState.Failed,
                EndedAt = heartbeat,
                Error = $"The replica running this session ({session.Owner}) stopped responding.",
            };
    }
}
