using System;

namespace Terrarium
{
    /// <summary>Pure geo math, kept free of game types so the self-check can run it without the game.</summary>
    public static class EarthMath
    {
        public const double MetersPerDegree = 40075016.686 / 360.0;
        public const double MaxMercatorLatitude = 85.0511287798;
        public const int TileSize = 256;
        public const int TileSizeShift = 8;
        public const int MaxZoom = 15;
        private const double EquatorMetersPerPixelAtZoom0 = 156543.03392;
        public const double HighestPeakMeters = 8849.0;
        // Below the knee the auto curve is close to linear (1 block per ~6 m at 256 height), above it is compressed logarithmically.
        private const double AutoCurveKneeMeters = 200.0;

        /// <summary>
        /// Blocks above (or below, for negative input) sea level for the auto vertical scale: exaggerates lowlands
        /// and compresses mountains so the highest peak lands exactly at <paramref name="blocksAboveSea"/>.
        /// </summary>
        public static double AutoHeightBlocks(double elevationMeters, double blocksAboveSea)
        {
            double fit = blocksAboveSea / Math.Log(1 + HighestPeakMeters / AutoCurveKneeMeters);
            return Math.Sign(elevationMeters) * fit * Math.Log(1 + Math.Abs(elevationMeters) / AutoCurveKneeMeters);
        }

        /// <summary>Terrarium PNG encoding used by the AWS terrain tiles: meters = R*256 + G + B/256 - 32768.</summary>
        public static double DecodeTerrarium(byte r, byte g, byte b) => r * 256.0 + g + b / 256.0 - 32768.0;

        /// <summary>Smallest zoom whose equator pixel is no larger than one block.</summary>
        public static int ZoomForScale(double metersPerBlock)
        {
            int zoom = (int)Math.Ceiling(Math.Log2(EquatorMetersPerPixelAtZoom0 / metersPerBlock));
            return Math.Clamp(zoom, 0, MaxZoom);
        }

        /// <summary>Web Mercator global pixel coordinates at the given zoom.</summary>
        public static void LatLonToPixel(double lat, double lon, int zoom, out double px, out double py)
        {
            double worldPixels = (double)(TileSize << zoom);
            lat = Math.Clamp(lat, -MaxMercatorLatitude, MaxMercatorLatitude);
            double sinLat = Math.Sin(lat * Math.PI / 180.0);
            px = (lon + 180.0) / 360.0 * worldPixels;
            py = (0.5 - Math.Log((1 + sinLat) / (1 - sinLat)) / (4 * Math.PI)) * worldPixels;
        }

        public static double WrapLongitude(double lon)
        {
            lon = (lon + 180.0) % 360.0;
            if (lon < 0) lon += 360.0;
            return lon - 180.0;
        }

        /// <summary>Annual mean sea level temperature in °C, a zonal fit of real climate (27 °C at the equator, ~10 °C at 45°, ~-40 °C at the poles).</summary>
        public static double SeaLevelTemperature(double lat) => 27.0 - 0.0084 * lat * lat;

        /// <summary>Relative precipitation 0..1: wet tropics, dry subtropical belt (~25°), wet mid-latitudes (~50°), dry poles.</summary>
        public static double Precipitation(double lat)
        {
            double a = Math.Abs(lat);
            double tropics = 0.7 * Math.Exp(-Math.Pow(lat / 12.0, 2));
            double midLatitudes = 0.45 * Math.Exp(-Math.Pow((a - 50.0) / 13.0, 2));
            double subtropicalHigh = 0.25 * Math.Exp(-Math.Pow((a - 25.0) / 8.0, 2));
            return Math.Clamp(0.2 + tropics + midLatitudes - subtropicalHigh, 0.0, 1.0);
        }
    }

    /// <summary>Maps block coordinates to real-world coordinates: the world center sits on the origin, north is -Z.</summary>
    public sealed class EarthProjection
    {
        public readonly double OriginLatitude;
        public readonly double OriginLongitude;
        public readonly double MetersPerBlock;
        public readonly double CenterX;
        public readonly double CenterZ;

        public EarthProjection(double originLatitude, double originLongitude, double metersPerBlock, double centerX, double centerZ)
        {
            OriginLatitude = originLatitude;
            OriginLongitude = originLongitude;
            MetersPerBlock = metersPerBlock;
            CenterX = centerX;
            CenterZ = centerZ;
        }

        // Clamped: beyond a pole the world continues as polar terrain instead of latitudes past ±90.
        public double Latitude(double blockZ) => Math.Clamp(OriginLatitude - (blockZ - CenterZ) * MetersPerBlock / EarthMath.MetersPerDegree, -90.0, 90.0);

        public double Longitude(double blockX) => EarthMath.WrapLongitude(OriginLongitude + (blockX - CenterX) * MetersPerBlock / EarthMath.MetersPerDegree);

        public double BlockZ(double latitude) => CenterZ - (latitude - OriginLatitude) * EarthMath.MetersPerDegree / MetersPerBlock;

        public double BlockX(double longitude) => CenterX + EarthMath.WrapLongitude(longitude - OriginLongitude) * EarthMath.MetersPerDegree / MetersPerBlock;
    }
}
