namespace StorageDemo.Core.Streaming;

/// <summary>
/// Where the sensor is looking, and where north is in the picture it produced. Pure arithmetic on
/// the ST 0601 pointing items, so the expression a heads-up display draws its north arrow from can
/// be checked against geodesy in a test rather than against a plausible-looking arrow.
///
/// Tag semantics, from ST 0601.8 Table 1 (the same revision Misb0601.cs cites, and these three
/// items are unchanged through 0601.14):
/// - Tag 5, Platform Heading Angle: the platform's heading, degrees clockwise from true north,
///   0..360.
/// - Tag 18, Sensor Relative Azimuth Angle: the sensor's rotation from the platform's longitudinal
///   axis about the platform's vertical axis, degrees clockwise seen from above, 0..360.
/// - Tag 20, Sensor Relative Roll Angle: the twist of the camera about its own lens axis, "top of
///   image is zero degrees", positive clockwise looking from behind the camera, 0..360.
/// </summary>
public static class SensorGeometry
{
    /// <summary>
    /// Where the sensor points, degrees clockwise from true north: tag 5 + tag 18, both measured
    /// clockwise, so they add. Null when either item is absent, because a bearing computed from
    /// half the pointing is a bearing pointing somewhere else.
    ///
    /// Platform pitch and roll (tags 6 and 7) are left out. ST 0601 defines tag 18 against the
    /// platform's own axes, so a banking platform does tilt the look direction out of this
    /// horizontal answer; on the Esri sample, which banks up to 30 degrees in its orbit, that
    /// costs about a degree against the geodetic bearing (see Misb0601RealStreamTests). Carrying
    /// the full rotation would mean composing three Euler rotations to gain that degree.
    /// </summary>
    public static double? SensorBearing(double? platformHeading, double? sensorRelativeAzimuth)
        => platformHeading is { } heading && sensorRelativeAzimuth is { } azimuth
            ? Wrap(heading + azimuth)
            : null;

    /// <summary>
    /// Where true north lies in the displayed image, degrees clockwise from the top of the
    /// picture, which is what an arrow on screen is rotated by.
    ///
    /// Two steps. With the camera unrolled and looking down, the top of the image is the direction
    /// the sensor points, and the picture reads like a map turned so that the sensor's bearing is
    /// up: a world bearing b sits at b - <see cref="SensorBearing"/> clockwise from the top, so
    /// north (b = 0) sits at minus the sensor bearing. Then tag 20 twists the camera itself
    /// clockwise about the lens axis, which turns the scene inside the frame the other way, so the
    /// roll subtracts as well: north = -(bearing + roll).
    ///
    /// An absent tag 20 is taken as an unrolled camera. That is the one assumption here that no
    /// test can catch, since the geodetic check below is blind to roll; the display names the roll
    /// it used, and says when the packet did not carry one.
    /// </summary>
    public static double? NorthInImage(double? platformHeading, double? sensorRelativeAzimuth, double? sensorRelativeRoll)
        => SensorBearing(platformHeading, sensorRelativeAzimuth) is { } bearing
            ? NorthInImage(bearing, sensorRelativeRoll)
            : null;

    /// <summary>
    /// Where true north lies in the displayed image when the sensor's absolute look bearing is
    /// already known. A bearing derived from the sensor and frame-centre positions is preferable
    /// to tag 5 + tag 18: it includes the effect of platform pitch and roll on an oblique camera.
    /// </summary>
    public static double? NorthInImage(double? sensorBearing, double? sensorRelativeRoll)
        => sensorBearing is { } bearing ? Wrap(-(bearing + (sensorRelativeRoll ?? 0))) : null;

    /// <summary>
    /// The initial great-circle bearing from one WGS84 position to another, degrees clockwise from
    /// true north. No heading, no attitude, no standard: two latitude and longitude pairs and
    /// spherical trigonometry, which is what makes it worth checking <see cref="SensorBearing"/>
    /// against.
    /// </summary>
    public static double? BearingBetween(double? fromLatitude, double? fromLongitude, double? toLatitude, double? toLongitude)
    {
        if (fromLatitude is not { } lat1 || fromLongitude is not { } lon1
            || toLatitude is not { } lat2 || toLongitude is not { } lon2)
        {
            return null;
        }

        var (f1, f2) = (Radians(lat1), Radians(lat2));
        var dl = Radians(lon2 - lon1);

        return Wrap(Degrees(Math.Atan2(
            Math.Sin(dl) * Math.Cos(f2),
            Math.Cos(f1) * Math.Sin(f2) - Math.Sin(f1) * Math.Cos(f2) * Math.Cos(dl))));
    }

    /// <summary>The signed difference between two bearings, -180..180, so 359 and 1 are 2 apart.</summary>
    public static double BearingDifference(double a, double b) => Wrap(a - b + 180) - 180;

    private static double Wrap(double degrees) => ((degrees % 360) + 360) % 360;

    private static double Radians(double degrees) => degrees * Math.PI / 180;

    private static double Degrees(double radians) => radians * 180 / Math.PI;
}
