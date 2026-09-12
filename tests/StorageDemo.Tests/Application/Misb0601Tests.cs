using StorageDemo.Core.Streaming;
using StorageDemo.Tests.Infrastructure;

namespace StorageDemo.Tests.Application;

/// <summary>
/// The decoder against a packet built by hand at known values. A wrong scale is invisible until
/// somebody looks at a map, so every item of the minimum set is asserted, to within the
/// resolution its integer encoding allows.
/// </summary>
public sealed class Misb0601Tests
{
    [Fact]
    public void The_minimum_set_decodes_to_the_values_it_was_built_from()
    {
        var set = Misb0601.Decode(Misb.MinimumSet());

        Assert.NotNull(set);

        Assert.Equal(Misb.Known.Timestamp, set.Timestamp);
        Assert.Equal(Misb.Known.MissionId, set.MissionId);
        Assert.Equal(Misb.Known.PlatformDesignation, set.PlatformDesignation);
        Assert.Equal(Misb.Known.ImageSourceSensor, set.ImageSourceSensor);
        Assert.Equal(Misb.Known.ImageCoordinateSystem, set.ImageCoordinateSystem);
        Assert.Equal(Misb.Known.Classification, set.Classification);
        Assert.Equal(Misb.Known.Version, set.Version);

        // Half a step of each encoding: a uint16 over 360 degrees resolves to 0.0055.
        Assert.Equal(Misb.Known.PlatformHeading, set.PlatformHeading!.Value, 360.0 / ushort.MaxValue);
        Assert.Equal(Misb.Known.PlatformPitch, set.PlatformPitch!.Value, 20.0 / short.MaxValue);
        Assert.Equal(Misb.Known.PlatformRoll, set.PlatformRoll!.Value, 50.0 / short.MaxValue);
        Assert.Equal(Misb.Known.SensorLatitude, set.SensorLatitude!.Value, 90.0 / int.MaxValue);
        Assert.Equal(Misb.Known.SensorLongitude, set.SensorLongitude!.Value, 180.0 / int.MaxValue);
        Assert.Equal(Misb.Known.SensorTrueAltitude, set.SensorTrueAltitude!.Value, 19900.0 / ushort.MaxValue);
        Assert.Equal(Misb.Known.SensorHorizontalFov, set.SensorHorizontalFov!.Value, 180.0 / ushort.MaxValue);
        Assert.Equal(Misb.Known.SensorVerticalFov, set.SensorVerticalFov!.Value, 180.0 / ushort.MaxValue);
        Assert.Equal(Misb.Known.SensorRelativeAzimuth, set.SensorRelativeAzimuth!.Value, 360.0 / uint.MaxValue);
        Assert.Equal(Misb.Known.SensorRelativeElevation, set.SensorRelativeElevation!.Value, 180.0 / int.MaxValue);
        Assert.Equal(Misb.Known.SensorRelativeRoll, set.SensorRelativeRoll!.Value, 360.0 / uint.MaxValue);
        Assert.Equal(Misb.Known.SlantRange, set.SlantRange!.Value, 5_000_000.0 / uint.MaxValue);
        Assert.Equal(Misb.Known.FrameCenterLatitude, set.FrameCenterLatitude!.Value, 90.0 / int.MaxValue);
        Assert.Equal(Misb.Known.FrameCenterLongitude, set.FrameCenterLongitude!.Value, 180.0 / int.MaxValue);
        Assert.Equal(Misb.Known.FrameCenterElevation, set.FrameCenterElevation!.Value, 19900.0 / ushort.MaxValue);

        // Outside the minimum set, so raw, and nothing else leaked into the raw bag.
        Assert.Equal(Misb.Known.TailNumber, Assert.Single(set.Unparsed).Value);
    }

    [Fact]
    public void A_packet_whose_checksum_does_not_match_is_rejected()
    {
        var packet = Misb.MinimumSet();

        // One bit in the last item before the checksum, which is the error this check exists to catch.
        packet[^5] ^= 0x01;

        Assert.Null(Misb0601.Decode(packet));
        Assert.True(Misb0601.IsUasDatalink(packet));
    }

    [Fact]
    public void A_packet_without_a_checksum_is_rejected()
    {
        var body = Misb.Items((13, [0x40, 0x00, 0x00, 0x00]));
        byte[] packet = [.. Misb0601.Key, (byte)body.Length, .. body];

        Assert.Null(Misb0601.Decode(packet));
    }

    [Fact]
    public void The_error_indicator_decodes_to_nothing_rather_than_a_place()
    {
        var set = Misb0601.Decode(Misb.Packet(
            (13, [0x80, 0x00, 0x00, 0x00]),
            (6, [0x80, 0x00])));

        Assert.NotNull(set);
        Assert.Null(set.SensorLatitude);
        Assert.Null(set.PlatformPitch);
    }

    [Fact]
    public void Something_under_another_key_is_not_a_uas_datalink_packet()
    {
        var packet = Misb.MinimumSet();
        packet[4] ^= 0xFF;

        Assert.False(Misb0601.IsUasDatalink(packet));
        Assert.Null(Misb0601.Decode(packet));
    }

    [Fact]
    public void A_security_set_without_a_classification_leaves_the_marking_null()
    {
        var set = Misb0601.Decode(Misb.Packet((48, Misb.Items((3, "//NOR"u8.ToArray())))));

        Assert.NotNull(set);
        Assert.Null(set.Classification);
    }
}
