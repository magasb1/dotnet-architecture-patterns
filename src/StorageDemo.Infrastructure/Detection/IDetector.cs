using StorageDemo.Core.Streaming;

namespace StorageDemo.Infrastructure.Detection;

/// <summary>
/// The seam from detection-plan.md: a decoded picture in, boxes in original-frame pixels out,
/// whatever model sits behind it.
///
/// A frame is a pointer to libav's own <c>AVFrame</c>, passed as <see cref="IntPtr"/> the way
/// <c>FrameHandler</c> already does. That is what the worker holds after a decode and what a
/// test holds after decoding a JPEG through libav, so one shape serves both; the pixel format is
/// whatever the decoder produced, and the resize converts it in the same pass.
/// </summary>
public interface IDetector
{
    /// <summary>
    /// Several frames as one model batch, one result array per frame in the same order. The
    /// caller assembles the batch; the runtime does not (research/onnxruntime-dotnet.md section 3).
    /// </summary>
    VmtiDetection[][] Detect(ReadOnlySpan<IntPtr> frames);

    VmtiDetection[] Detect(IntPtr frame) => Detect([frame])[0];
}
