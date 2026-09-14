namespace StorageDemo.Infrastructure.Streaming;

/// <summary>
/// One demultiplexed packet, copied out of libav into managed memory.
///
/// Copied rather than reference-counted on purpose. A packet in the rolling buffer outlives the
/// read that produced it by up to thirty seconds and is handed to any number of subscribers, so
/// tying its lifetime to libav's reference counting would mean every consumer understanding that
/// contract. A byte array is also what makes the buffer's byte ceiling a real number rather than
/// an estimate.
/// </summary>
/// <param name="StreamIndex">Which stream inside the transport this belongs to.</param>
/// <param name="IsKeyframe">
/// Whether a decoder can start here. This is what turns a flow of packets into segments, and it
/// is the sender's keyframe interval that decides how coarse those segments are.
/// </param>
public sealed record MediaPacket(
    int StreamIndex,
    byte[] Data,
    long Pts,
    long Dts,
    long Duration,
    bool IsKeyframe)
{
    public int Bytes => Data.Length;
}
