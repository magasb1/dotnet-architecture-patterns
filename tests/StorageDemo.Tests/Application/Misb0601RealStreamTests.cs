using StorageDemo.Core.Streaming;
using StorageDemo.Tests.Infrastructure;

namespace StorageDemo.Tests.Application;

/// <summary>
/// The decoder against metadata this project did not write: Esri's public "Sample video for Full
/// Motion Video" tutorial stream, 148 seconds of a Cessna 208B orbiting a truck near Cheyenne,
/// Wyoming on 19 September 2012.
///
/// Every other MISB test here encodes a packet and decodes it again, which proves the two halves
/// agree and nothing about whether either matches the standard. This one has no encoder to agree
/// with. The assertions are the ones the file can be trusted to keep: that every packet verifies,
/// that the geometry closes, and that the carriage is synchronous.
///
/// Fetched, not committed: see scripts/fetch-fmv-sample.sh.
/// </summary>
public sealed class Misb0601RealStreamTests
{
    /// <summary>
    /// Where the fetch script puts it, and where a person who unpacked the archive by hand is
    /// likely to have left it. Searched rather than fixed because the first arrangement of these
    /// tests skipped in silence the moment the file moved, and a test that reports "not fetched"
    /// when the file is sitting two directories away is worse than one that simply fails.
    /// </summary>
    private static readonly string[] Candidates =
    [
        Path.Combine("data", "fmv", "FMV tutorial data", "Truck.ts"),
        Path.Combine("data", "Truck.ts"),
    ];

    private static readonly string Root = Path.Combine(
        AppContext.BaseDirectory, "..", "..", "..", "..", "..");

    private static readonly string? Sample = Candidates
        .Select(candidate => Path.Combine(Root, candidate))
        .FirstOrDefault(File.Exists);

    private static List<(long? Pts, Misb0601Set Set)> Decoded()
    {
        Assert.SkipUnless(
            Sample is not null,
            "Run scripts/fetch-fmv-sample.sh to fetch the Esri FMV sample. Looked for "
                + string.Join(" and ", Candidates.Select(c => $"'{c}'"))
                + $" under {Path.GetFullPath(Root)}.");

        var packets = Misb.ReadKlv(Sample);

        Assert.NotEmpty(packets);

        // Not Where(...): a packet the decoder drops must fail the test, not vanish from it.
        return [.. packets.Select(p => (p.Pts, Set: Decode(p.Data)))];
    }

    private static Misb0601Set Decode(byte[] data)
    {
        Assert.True(Misb0601.IsUasDatalink(data), "A KLV packet in the sample was not a UAS Datalink LS.");

        return Misb0601.Decode(data)
            ?? throw new Xunit.Sdk.XunitException("A UAS Datalink packet in the sample failed its checksum.");
    }

    [Fact]
    public void Every_packet_verifies_and_carries_the_whole_minimum_set()
    {
        var decoded = Decoded();

        // 5 Hz over the file's 148 seconds. Asserted loosely: the point is that none were dropped.
        Assert.InRange(decoded.Count, 700, 760);

        foreach (var (_, set) in decoded)
        {
            Assert.NotNull(set.Timestamp);
            Assert.NotNull(set.MissionId);
            Assert.NotNull(set.PlatformHeading);
            Assert.NotNull(set.PlatformPitch);
            Assert.NotNull(set.PlatformRoll);
            Assert.NotNull(set.PlatformDesignation);
            Assert.NotNull(set.ImageSourceSensor);
            Assert.NotNull(set.ImageCoordinateSystem);
            Assert.NotNull(set.SensorLatitude);
            Assert.NotNull(set.SensorLongitude);
            Assert.NotNull(set.SensorTrueAltitude);
            Assert.NotNull(set.SensorHorizontalFov);
            Assert.NotNull(set.SensorVerticalFov);
            Assert.NotNull(set.SensorRelativeAzimuth);
            Assert.NotNull(set.SensorRelativeElevation);
            Assert.NotNull(set.SensorRelativeRoll);
            Assert.NotNull(set.SlantRange);
            Assert.NotNull(set.FrameCenterLatitude);
            Assert.NotNull(set.FrameCenterLongitude);
            Assert.NotNull(set.FrameCenterElevation);
        }

        var first = decoded[0].Set;

        Assert.Equal("ESRI_Metadata_Collect", first.MissionId);
        Assert.Equal("C208B", first.PlatformDesignation);
        Assert.Equal("UNCLASSIFIED", first.Classification);
        Assert.Equal(1, first.Version);
    }

    /// <summary>
    /// ST 1402.2 synchronous carriage. The sample is stream type 0x15 in metadata PES packets, so
    /// every packet has a presentation timestamp and the KLV extractor reports
    /// <see cref="KlvAlignment.PresentationTimestamp"/>. The two clocks are then checked against
    /// each other: the PES timeline and ST 0601 tag 2 must span the same interval.
    /// </summary>
    [Fact]
    public void The_carriage_is_synchronous_and_the_two_clocks_agree()
    {
        var decoded = Decoded();

        Assert.All(decoded, d => Assert.NotNull(d.Pts));

        var pts = (decoded[^1].Pts!.Value - decoded[0].Pts!.Value) / 90_000.0;
        var tag2 = (decoded[^1].Set.Timestamp!.Value - decoded[0].Set.Timestamp!.Value).TotalSeconds;

        Assert.Equal(pts, tag2, 0.05);
        Assert.InRange(pts, 140, 150);
    }

    /// <summary>
    /// The scale check that no hand-built packet can make. Slant range, both positions and both
    /// elevations are four independently encoded items on three different scales; if any one of
    /// them is decoded wrong the triangle stops closing.
    /// </summary>
    [Fact]
    public void Slant_range_agrees_with_the_distance_between_the_positions_it_was_sent_with()
    {
        foreach (var (_, set) in Decoded())
        {
            var north = (set.FrameCenterLatitude!.Value - set.SensorLatitude!.Value) * 111_320;
            var east = (set.FrameCenterLongitude!.Value - set.SensorLongitude!.Value) * 111_320
                       * Math.Cos(set.SensorLatitude.Value * Math.PI / 180);
            var up = set.SensorTrueAltitude!.Value - set.FrameCenterElevation!.Value;

            // Ten metres on a 1.6-2.3 km range. A wrong scale on any of the five is off by a
            // factor, not by metres; the residual here is the spherical-earth approximation.
            Assert.Equal(Math.Sqrt(north * north + east * east + up * up), set.SlantRange!.Value, 10.0);
        }
    }

    /// <summary>
    /// The check that keeps a north arrow honest. Where the sensor points comes from ST 0601 tag 5
    /// plus tag 18; where it points can also be had from the sensor position and the frame centre
    /// position alone, by spherical trigonometry, with no heading, no azimuth and no standard
    /// involved. The two are computed from disjoint items, so a flipped sign or a dropped term in
    /// the first is a hundred-odd degrees away from the second rather than a plausible arrow.
    ///
    /// Measured over the sample's 711 packets on 2026-09-13: mean -2.96 degrees, from -5.78 to
    /// +0.39, worst 5.78. The bias is the platform's attitude, which
    /// <see cref="SensorGeometry.SensorBearing"/> deliberately leaves out: the Cessna is banked
    /// left through most of its orbit (platform roll -25.5 to +2.4 degrees, pitch +0.7 to +8.9)
    /// and the sensor looks 10 to 40 degrees below the horizon, so the platform's own tilt swings
    /// the look direction a few degrees off the heading-plus-azimuth answer. Composing the full
    /// body-to-NED rotation from tags 5, 6, 7, 18 and 19 instead was measured at mean +0.13 and
    /// worst 0.79 degrees against the same geodetic bearing, which is what says the residual is
    /// the attitude term and not a decode error.
    /// </summary>
    [Fact]
    public void Where_the_sensor_says_it_points_agrees_with_the_bearing_to_the_frame_centre()
    {
        var worst = 0.0;

        foreach (var (_, set) in Decoded())
        {
            var pointed = SensorGeometry.SensorBearing(set.PlatformHeading, set.SensorRelativeAzimuth);
            var geodetic = SensorGeometry.BearingBetween(
                set.SensorLatitude, set.SensorLongitude, set.FrameCenterLatitude, set.FrameCenterLongitude);

            worst = Math.Max(worst, Math.Abs(SensorGeometry.BearingDifference(pointed!.Value, geodetic!.Value)));

            // The one item in the north arrow this file cannot check: the sensor is never rolled
            // here, so tag 20's sign is on the reading of the standard alone. Asserted so that a
            // sample that did roll would say so rather than quietly making the claim untrue.
            Assert.Equal(0, set.SensorRelativeRoll!.Value);
        }

        Assert.InRange(worst, 0, 6);
    }

    [Fact]
    public void The_platform_flies_one_continuous_path_at_a_plausible_attitude()
    {
        var decoded = Decoded();

        Assert.Equal(new DateOnly(2012, 9, 19), DateOnly.FromDateTime(decoded[0].Set.Timestamp!.Value.UtcDateTime));

        for (var i = 0; i < decoded.Count; i++)
        {
            var set = decoded[i].Set;

            // Cheyenne, Wyoming, a degree either way. A flipped sign or a doubled scale leaves it.
            Assert.InRange(set.SensorLatitude!.Value, 40, 42);
            Assert.InRange(set.SensorLongitude!.Value, -106, -104);

            // Terrain there is about 1870 m; the aircraft is roughly a kilometre above it.
            Assert.InRange(set.SensorTrueAltitude!.Value, 2500, 3500);
            Assert.InRange(set.FrameCenterElevation!.Value, 1500, 2200);

            Assert.InRange(set.PlatformHeading!.Value, 0, 360);
            Assert.InRange(set.PlatformPitch!.Value, -20, 20);
            Assert.InRange(set.PlatformRoll!.Value, -50, 50);

            // A narrow sensor, and its two fields of view share the picture's 16:9.
            Assert.InRange(set.SensorHorizontalFov!.Value, 0.1, 10);
            Assert.Equal(16.0 / 9.0, set.SensorHorizontalFov.Value / set.SensorVerticalFov!.Value, 0.02);

            Assert.InRange(set.SensorRelativeElevation!.Value, -90, 0);

            if (i == 0)
            {
                continue;
            }

            // 0.2 s between packets, so a step of a degree is not an aircraft, it is a bad decode.
            var previous = decoded[i - 1].Set;
            Assert.Equal(previous.SensorLatitude!.Value, set.SensorLatitude.Value, 0.001);
            Assert.Equal(previous.SensorLongitude!.Value, set.SensorLongitude.Value, 0.001);
        }
    }
}
