using System;

public static class GeoDistanceCalculator
{
    private const double EarthRadiusMeters = 6371000d;
    private const double DegreesToRadians = Math.PI / 180d;

    public static double DistanceMeters(
        double latitude1,
        double longitude1,
        double latitude2,
        double longitude2)
    {
        double latitudeDelta = (latitude2 - latitude1) * DegreesToRadians;
        double longitudeDelta = (longitude2 - longitude1) * DegreesToRadians;
        double latitude1Radians = latitude1 * DegreesToRadians;
        double latitude2Radians = latitude2 * DegreesToRadians;

        double sinLatitude = Math.Sin(latitudeDelta * 0.5d);
        double sinLongitude = Math.Sin(longitudeDelta * 0.5d);
        double haversine = sinLatitude * sinLatitude
            + Math.Cos(latitude1Radians) * Math.Cos(latitude2Radians)
            * sinLongitude * sinLongitude;
        double centralAngle = 2d * Math.Atan2(
            Math.Sqrt(haversine),
            Math.Sqrt(Math.Max(0d, 1d - haversine)));

        return EarthRadiusMeters * centralAngle;
    }
}
