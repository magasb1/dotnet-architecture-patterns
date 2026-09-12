using System.Buffers.Binary;
using System.Text;

namespace StorageDemo.Core.Streaming;

/// <summary>
/// One detection in one frame, in the terms a detector actually produces: an identifier, a
/// bounding box in pixels, and optionally how sure it is and what it thinks the thing is.
///
/// Pixel coordinates are the standard's, not a tensor library's: 1-based, column then row, with
/// (1, 1) the top left pixel (ST 0903.4 section 11.15, Tag 1). A detector counting from zero adds
/// one before it gets here, and doing that conversion at the boundary is why this record says so.
/// The box is inclusive of both corners.
/// </summary>
/// <param name="Id">
/// VTarget Pack Target ID Number, 1 to 2,097,151. ST 0903.4-28 asks that it identify a given
/// target uniquely, which is what makes a track out of a series of detections.
/// </param>
/// <param name="ConfidencePercent">
/// VTarget Pack tag 5, 0 to 100. The standard's unit is a percentage, not a probability, so a
/// model's 0..1 score is scaled by the caller rather than silently here.
/// </param>
/// <param name="OntologyClass">
/// VObject LS tag 2, the class name as it appears in the ontology named by
/// <see cref="VmtiFrame.Ontology"/>. Null when the detector reports a box but no class.
/// </param>
public sealed record VmtiDetection(
    int Id,
    int Left,
    int Top,
    int Right,
    int Bottom,
    int? ConfidencePercent = null,
    string? OntologyClass = null);

/// <summary>
/// One frame's detections, with the frame-level facts a consumer needs to make sense of them. A
/// frame with no detections is a legitimate instance: it says the detector ran and found nothing,
/// which is not the same as a gap in the metadata.
/// </summary>
/// <param name="Timestamp">
/// VMTI LS tag 2, the frame this describes. Same clock as ST 0601 tag 2 (MISB ST 0603 microseconds
/// since the UNIX epoch), which is the whole point: it is what pairs a detection with the platform
/// and sensor state that lets a worker geo-reference it later.
/// </param>
/// <param name="FrameWidth">
/// VMTI LS tag 8. Not decoration: every pixel position in the packet is a single number computed
/// from it, so a consumer cannot turn a detection back into a column and row without it.
/// </param>
/// <param name="SourceSensor">
/// VMTI LS tag 10, which imagery the detector ran on. ST 0903.4-24 wants it repeated periodically,
/// and every packet is the simplest way to satisfy that.
/// </param>
/// <param name="Ontology">
/// VObject LS tag 1, the URI of the OWL ontology the class names come from (ST 0903.4-45). Null
/// when no detection carries a class.
/// </param>
public sealed record VmtiFrame(
    DateTimeOffset Timestamp,
    int FrameWidth,
    int FrameHeight,
    string SourceSensor,
    IReadOnlyList<VmtiDetection> Detections,
    string? Ontology = null);

/// <summary>
/// Encodes a standalone MISB ST 0903 VMTI Local Set: the detections from one frame, as a KLV
/// packet a STANAG 4609 consumer already knows how to read.
///
/// Sources, so every choice below can be checked against a document rather than against this file:
/// - MISP-2019.1, which STANAG 4609 Ed. 5 adopts, normatively references MISB ST 0903.4 Video
///   Moving Target Indicator and Track Metadata, Oct 2014 (reference [57] of that document, read
///   from the MISP-2019.1 PDF itself). So .4 is the revision this encoder writes, and VMTI LS tag
///   4 carries the value 4 to say so.
/// - Every tag, key, format and requirement number cited below was read from the ST 0903.4 PDF
///   dated 23 October 2014, tables 1, 2 and 4 and sections 8.3, 9.3 and 11. NSGREG sits behind a
///   CAPTCHA and gwg.nga.mil now refuses the file, so the copy used was the Internet Archive's
///   capture of the MISB's own distribution at gwg.nga.mil/misb/docs/standards/ST0903.4.pdf. That
///   is the MISB document, not a third party's summary, but it is a mirror rather than the
///   registry, which is worth knowing if a clause number here ever looks wrong.
/// - Later revisions exist (.5 adds the VTrack LS, .6 reworks pixel addressing) and were NOT
///   consulted; nothing here should be assumed to hold for them. A consumer reading tag 4 learns
///   which revision it got, which is what that tag is for.
///
/// ponytail: this encodes what a detection carries and nothing else. Left out deliberately, each a
/// TLV away: VMTI LS tags 3 (system name), 5 (total detected, required only when culling makes it
/// differ from tag 6, per ST 0903.4-18), 7 (frame number), 11/12 (VMTI sensor FOV, needed only
/// when detection ran on different imagery than the video), 13 (MIIS core identifier); VTarget
/// tags 4 (priority), 6 (target history), 7-9 (pixel share, colour, intensity), 10-18 (all the
/// geo-space forms, which are the worker's job once it has ST 0601 platform data), 19/20 (centroid
/// as row and column, a redundant spelling of tag 1), 21 (FPA index); and the VMask, VFeature,
/// VChip and VTracker local sets. VTracker is the one to add first: it is where a track's history
/// lives, and pattern of life is a track question.
/// </summary>
public static class Misb0903
{
    /// <summary>ST 0903.4 Table 1: the VMTI LS 16-byte universal key.</summary>
    public static ReadOnlySpan<byte> Key =>
    [
        0x06, 0x0E, 0x2B, 0x34, 0x02, 0x0B, 0x01, 0x01, 0x0E, 0x01, 0x03, 0x03, 0x06, 0x00, 0x00, 0x00,
    ];

    /// <summary>The revision this encoder writes, sent as VMTI LS tag 4 (ST 0903.4 section 11.4).</summary>
    public const int Version = 4;

    /// <summary>
    /// The packet, ready to hand to a KLV carriage. Throws <see cref="ArgumentOutOfRangeException"/>
    /// rather than emitting a conforming-looking packet with nonsense in it: a box outside the
    /// frame or a zero target id is a bug in the detector wiring, and a consumer has no way to
    /// notice either.
    /// </summary>
    public static byte[] Encode(VmtiFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(frame.FrameWidth, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(frame.FrameHeight, 1);

        var body = new List<byte>();

        // ST 0903.4-14: when a Precision Time Stamp is present it is the first TLV in the set.
        Item(body, 2, Fixed(Microseconds(frame.Timestamp), 8));
        Item(body, 4, Variable(Version));

        // ST 0903.4-19: Number of Reported Targets is always specified, zero included.
        Item(body, 6, Variable((ulong)frame.Detections.Count));
        Item(body, 8, Variable((ulong)frame.FrameWidth));
        Item(body, 9, Variable((ulong)frame.FrameHeight));
        Item(body, 10, Encoding.UTF8.GetBytes(frame.SourceSensor));

        // ST 0903.4-10 requires at least one TLV after a VTarget Pack's id, so an empty frame says
        // "nothing detected" with tag 6 alone; Table 1 tag 5 agrees that no targets is expressed by
        // no value at all. An empty VTargetSeries would be a series of nothing, which is not a thing.
        if (frame.Detections.Count > 0)
        {
            Item(body, 101, Series(frame));
        }

        // ST 0903.4-16/17: the checksum is the last TLV and covers the key, the set's length, every
        // TLV before it and its own tag and length byte, but not its own value.
        var payload = new List<byte>(body) { 1, 2 };
        byte[] packet = [.. Key, .. Length(payload.Count + 2), .. payload, 0, 0];

        BinaryPrimitives.WriteUInt16BigEndian(
            packet.AsSpan(packet.Length - 2),
            // ST 0903.4 section 11.1 defers to ST 0601 for the algorithm, so this is the same sum.
            Misb0601.Checksum(packet.AsSpan(0, packet.Length - 2)));

        return packet;
    }

    /// <summary>
    /// ST 0903.4-06/07: a Series is a variable-length pack of same-typed elements, here VTarget
    /// Packs, each a BER length and a value, with no key and no count in front of them.
    /// </summary>
    private static byte[] Series(VmtiFrame frame)
    {
        var series = new List<byte>();

        foreach (var detection in frame.Detections)
        {
            var pack = Pack(frame, detection);
            series.AddRange(Length(pack.Length));
            series.AddRange(pack);
        }

        return [.. series];
    }

    private static byte[] Pack(VmtiFrame frame, VmtiDetection detection)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(detection.Id, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(detection.Id, 2_097_151);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(detection.Left, detection.Right);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(detection.Top, detection.Bottom);

        // ST 0903.4-09: the target id comes first, BER-OID encoded, with no tag and no length.
        var pack = new List<byte>(Oid(detection.Id));

        // ST 0903.4-29: a centroid must be present. The box's centre is it: ST 0903.4 section
        // 11.15 Tag 1 footnote 13 says the centroid of a simple bounding box may be adequate.
        // ponytail: a segmentation model knows better; add explicit centroid fields when one lands.
        Item(pack, 1, Variable(Pixel(frame, (detection.Left + detection.Right) / 2, (detection.Top + detection.Bottom) / 2)));
        Item(pack, 2, Variable(Pixel(frame, detection.Left, detection.Top)));
        Item(pack, 3, Variable(Pixel(frame, detection.Right, detection.Bottom)));

        if (detection.ConfidencePercent is { } confidence)
        {
            // Table 2 tag 5: one byte, 0 to 100 as a percentage.
            ArgumentOutOfRangeException.ThrowIfNegative(confidence);
            ArgumentOutOfRangeException.ThrowIfGreaterThan(confidence, 100);
            Item(pack, 5, [(byte)confidence]);
        }

        if (detection.OntologyClass is { } target)
        {
            Item(pack, 102, VObject(frame.Ontology, target));
        }

        return [.. pack];
    }

    /// <summary>
    /// Table 4: the VObject LS, which is where ST 0903 puts what a target is. The ontology URI is
    /// repeated in every pack rather than sent once: ST 0903.4-46 requires it to precede any class
    /// that uses it and ST 0903.4-47 makes it periodic, and a consumer that joins a live stream
    /// mid-flight has not seen an earlier packet.
    /// </summary>
    private static byte[] VObject(string? ontology, string target)
    {
        var set = new List<byte>();

        if (ontology is not null)
        {
            Item(set, 1, Encoding.UTF8.GetBytes(ontology));
        }

        Item(set, 2, Encoding.UTF8.GetBytes(target));

        return [.. set];
    }

    /// <summary>
    /// ST 0903.4 section 11.15 Tag 1: pixel number is Column + (Row - 1) x Frame Width, counting
    /// from 1 at the top left, row-major. One number instead of two is the standard's bandwidth
    /// saving, and it is why Frame Width has to be in the packet.
    /// </summary>
    private static ulong Pixel(VmtiFrame frame, int column, int row)
    {
        if (column < 1 || column > frame.FrameWidth || row < 1 || row > frame.FrameHeight)
        {
            throw new ArgumentOutOfRangeException(
                nameof(frame),
                $"pixel ({column}, {row}) is outside a {frame.FrameWidth}x{frame.FrameHeight} frame");
        }

        return (ulong)column + ((ulong)(row - 1) * (ulong)frame.FrameWidth);
    }

    private static ulong Microseconds(DateTimeOffset timestamp)
        => (ulong)(timestamp.UtcDateTime - DateTime.UnixEpoch).Ticks / 10;

    private static void Item(List<byte> into, int tag, ReadOnlySpan<byte> value)
    {
        // Every tag this encoder writes is below 128, which BER-OID encodes as the byte itself.
        into.Add((byte)tag);
        into.AddRange(Length(value.Length));
        into.AddRange(value);
    }

    /// <summary>BER length: one byte below 128, otherwise a count of the length bytes that follow.</summary>
    private static byte[] Length(int length)
    {
        if (length < 128)
        {
            return [(byte)length];
        }

        var bytes = Variable((ulong)length);

        return [(byte)(0x80 | bytes.Length), .. bytes];
    }

    /// <summary>
    /// ST 0903.4 section 8.3, the "Vmax" formats: the fewest bytes that hold the value, leading
    /// zeroes dropped, and one byte for zero (ST 0903.4-05).
    /// </summary>
    private static byte[] Variable(ulong value)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);

        var first = 0;

        while (first < 7 && bytes[first] == 0)
        {
            first++;
        }

        return bytes[first..];
    }

    /// <summary>The "Fn" formats, which unlike "Vn" are always the full width.</summary>
    private static byte[] Fixed(ulong value, int length)
    {
        var bytes = new byte[8];
        BinaryPrimitives.WriteUInt64BigEndian(bytes, value);

        return bytes[(8 - length)..];
    }

    /// <summary>BER-OID: seven bits per byte, high bit set on every byte but the last.</summary>
    private static byte[] Oid(int value)
    {
        var bytes = new List<byte>();

        do
        {
            bytes.Insert(0, (byte)(value & 0x7F));
            value >>= 7;
        }
        while (value > 0);

        for (var i = 0; i < bytes.Count - 1; i++)
        {
            bytes[i] |= 0x80;
        }

        return [.. bytes];
    }
}
