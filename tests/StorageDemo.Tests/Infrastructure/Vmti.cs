using System.Buffers.Binary;
using System.Text;

namespace StorageDemo.Tests.Infrastructure;

/// <summary>One VTarget Pack read back: its BER-OID target id, then its TLV triplets by tag.</summary>
internal sealed record VmtiPack(int Id, IReadOnlyDictionary<int, byte[]> Items);

/// <summary>A VMTI LS read back: the set's own TLV triplets by tag, and the VTargetSeries.</summary>
internal sealed record VmtiPacket(IReadOnlyDictionary<int, byte[]> Items, IReadOnlyList<VmtiPack> Targets);

/// <summary>
/// Just enough of an ST 0903.4 reader to prove the encoder wrote what it meant to.
///
/// It lives in the test project on purpose. Nothing in this service consumes VMTI, so a production
/// decoder would be code with no caller; and a decoder written here, from the standard's structure
/// rather than from the encoder's source, is what catches a length or an offset that the encoder
/// and a hand-built expectation would otherwise agree on.
///
/// Written from MISB ST 0903.4, 23 October 2014: section 9.1 for the VTarget Pack's shape
/// (BER-OID id then TLVs), section 8.3 for the variable-length integers, SMPTE ST 336 for BER.
/// </summary>
internal static class Vmti
{
    public static VmtiPacket Decode(byte[] packet)
    {
        Assert.True(packet.Length > 17, "a VMTI LS is at least a key, a length and a checksum");

        var offset = 16;
        var length = ReadLength(packet, ref offset);

        Assert.Equal(packet.Length, offset + length);

        var items = new Dictionary<int, byte[]>();
        var targets = new List<VmtiPack>();

        while (offset < packet.Length)
        {
            var tag = ReadOid(packet, ref offset);
            var value = ReadValue(packet, ref offset);

            if (tag == 101)
            {
                targets.AddRange(ReadSeries(value));
            }

            items[tag] = value;
        }

        return new VmtiPacket(items, targets);
    }

    /// <summary>ST 0903.4-06: a Series carries no count, so it is read until its value runs out.</summary>
    private static List<VmtiPack> ReadSeries(byte[] series)
    {
        var packs = new List<VmtiPack>();
        var offset = 0;

        while (offset < series.Length)
        {
            var length = ReadLength(series, ref offset);
            var pack = series.AsSpan(offset, length).ToArray();
            offset += length;

            var inner = 0;
            var id = ReadOid(pack, ref inner);
            var items = new Dictionary<int, byte[]>();

            while (inner < pack.Length)
            {
                var tag = ReadOid(pack, ref inner);
                items[tag] = ReadValue(pack, ref inner);
            }

            Assert.NotEmpty(items); // ST 0903.4-10: at least one TLV follows the target id.
            packs.Add(new VmtiPack(id, items));
        }

        return packs;
    }

    /// <summary>A local set nested in a value, such as the VObject LS under VTarget Pack tag 102.</summary>
    public static IReadOnlyDictionary<int, byte[]> Nested(byte[] value)
    {
        var items = new Dictionary<int, byte[]>();
        var offset = 0;

        while (offset < value.Length)
        {
            var tag = ReadOid(value, ref offset);
            items[tag] = ReadValue(value, ref offset);
        }

        return items;
    }

    /// <summary>ST 0903.4 section 8.3: an unsigned integer in however many bytes it needed.</summary>
    public static ulong Integer(byte[] value)
    {
        Assert.InRange(value.Length, 1, 8);

        var result = 0UL;

        foreach (var b in value)
        {
            result = (result << 8) | b;
        }

        return result;
    }

    public static string Text(byte[] value) => Encoding.UTF8.GetString(value);

    /// <summary>
    /// ST 0903.4 section 11.15 Tag 1 backwards: pixel number to (column, row), one-based from the
    /// top left. The division is the half of the formula the encoder never runs, which is why this
    /// catches a frame width the encoder got wrong.
    /// </summary>
    public static (int Column, int Row) Pixel(ulong number, int frameWidth)
    {
        Assert.True(number >= 1, "pixel numbering commences at 1");

        return ((int)((number - 1) % (ulong)frameWidth) + 1, (int)((number - 1) / (ulong)frameWidth) + 1);
    }

    private static byte[] ReadValue(byte[] bytes, ref int offset)
    {
        var length = ReadLength(bytes, ref offset);

        Assert.True(offset + length <= bytes.Length, "a length ran off the end of the packet");

        var value = bytes.AsSpan(offset, length).ToArray();
        offset += length;

        return value;
    }

    private static int ReadLength(byte[] bytes, ref int offset)
    {
        var first = bytes[offset++];

        if ((first & 0x80) == 0)
        {
            return first;
        }

        var count = first & 0x7F;

        Assert.InRange(count, 1, 4);

        var length = 0;

        for (var i = 0; i < count; i++)
        {
            length = (length << 8) | bytes[offset++];
        }

        Assert.True(length >= 128, "the long form is only correct for a length the short form cannot hold");

        return length;
    }

    private static int ReadOid(byte[] bytes, ref int offset)
    {
        var value = 0;

        while (true)
        {
            var b = bytes[offset++];
            value = (value << 7) | (b & 0x7F);

            if ((b & 0x80) == 0)
            {
                return value;
            }
        }
    }

    /// <summary>
    /// ST 0903.4-16 spelled independently of the production checksum: a 16-bit sum over the packet
    /// from the first byte of the key to the last byte of the checksum item's own length field,
    /// taken as big-endian words rather than as a running byte sum.
    /// </summary>
    public static ushort Checksum(ReadOnlySpan<byte> covered)
    {
        ushort sum = 0;

        for (var i = 0; i + 1 < covered.Length; i += 2)
        {
            sum += BinaryPrimitives.ReadUInt16BigEndian(covered[i..]);
        }

        return (covered.Length % 2 == 1) ? (ushort)(sum + (covered[^1] << 8)) : sum;
    }
}
