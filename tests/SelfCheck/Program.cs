using System;
using System.IO;
using Terrarium;

// Assert-based self-check for the geo math and real tile decoding. Run: dotnet run --project tests/SelfCheck
int failures = 0;
void Check(bool ok, string what)
{
    Console.WriteLine((ok ? "PASS " : "FAIL ") + what);
    if (!ok) failures++;
}

Check(EarthMath.DecodeTerrarium(128, 0, 0) == 0, "Terrarium decode: (128,0,0) = 0 m");
Check(EarthMath.DecodeTerrarium(162, 144, 0) == 8848, "Terrarium decode: (162,144,0) = 8848 m");
Check(EarthMath.ZoomForScale(40) == 12, "zoom for 40 m/block is 12");
Check(EarthMath.ZoomForScale(10) == 14, "zoom for 10 m/block is 14");
Check(EarthMath.ZoomForScale(0.1) == EarthMath.MaxZoom, "zoom is capped");

EarthMath.LatLonToPixel(0, 0, 0, out double px, out double py);
Check(Math.Abs(px - 128) < 1e-9 && Math.Abs(py - 128) < 1e-9, "lat/lon 0,0 is the center of the zoom 0 tile");
EarthMath.LatLonToPixel(85.0511287798, -180, 1, out px, out py);
Check(Math.Abs(px) < 1e-6 && Math.Abs(py) < 1e-3, "top-left corner of the Mercator world");

Check(Math.Abs(EarthMath.WrapLongitude(190) - -170) < 1e-9 && Math.Abs(EarthMath.WrapLongitude(-190) - 170) < 1e-9, "longitude wraps across the antimeridian");

var projection = new EarthProjection(41.0082, 28.9784, 40, 512000, 512000);
Check(Math.Abs(projection.Latitude(512000) - 41.0082) < 1e-9 && Math.Abs(projection.Longitude(512000) - 28.9784) < 1e-9, "world center is the origin");
Check(projection.Latitude(511000) > 41.0082, "north is -Z");
Check(Math.Abs(projection.Latitude(projection.BlockZ(27.988)) - 27.988) < 1e-9 && Math.Abs(projection.Longitude(projection.BlockX(86.925)) - 86.925) < 1e-9, "block <-> lat/lon round trip");
Check(Math.Abs(projection.Longitude(projection.BlockX(-150)) - -150) < 1e-9, "round trip across the antimeridian");

Check(EarthMath.SeaLevelTemperature(0) == 27 && EarthMath.SeaLevelTemperature(90) < -30, "temperature: hot equator, frozen poles");
Check(EarthMath.Precipitation(0) > 0.8 && EarthMath.Precipitation(25) < 0.15 && EarthMath.Precipitation(-50) > 0.5, "precipitation: wet tropics, dry 25°, wet 50°");

const double blocksAboveSea256 = 256 - 12 - 110;
Check(Math.Abs(EarthMath.AutoHeightBlocks(EarthMath.HighestPeakMeters, blocksAboveSea256) - blocksAboveSea256) < 1e-9, "auto scale: highest peak fits the world height exactly");
Check(EarthMath.AutoHeightBlocks(0, blocksAboveSea256) == 0, "auto scale: sea level stays at sea level");
double hills = EarthMath.AutoHeightBlocks(100, blocksAboveSea256);
Check(hills > 10 && hills < 20, $"auto scale: 100 m hills are {hills:0.0} blocks high (visible relief)");
Check(EarthMath.AutoHeightBlocks(-4000, blocksAboveSea256) < -100, "auto scale: deep ocean is deep");
Check(EarthMath.AutoHeightBlocks(5000, blocksAboveSea256) > EarthMath.AutoHeightBlocks(4000, blocksAboveSea256), "auto scale: monotonic");

Check(TerrariumModSystem.TryParseCoordinates("27.988 86.925", out double lat, out double lon, out string err) && lat == 27.988 && lon == 86.925 && err == null, "parse 'lat lon'");
Check(TerrariumModSystem.TryParseCoordinates("-33.86, 151.2", out lat, out lon, out err) && lat == -33.86 && lon == 151.2, "parse 'lat, lon'");
Check(!TerrariumModSystem.TryParseCoordinates("Mount Everest", out _, out _, out err) && err == null, "place name is not coordinates");
Check(!TerrariumModSystem.TryParseCoordinates("100 0", out _, out _, out err) && err != null, "out of range coordinates are rejected");

// Real data from AWS Terrain Tiles (needs internet).
string cache = Path.Combine(Path.GetTempPath(), "terrarium-selfcheck-cache");
var source = new ElevationSource(12, cache, Console.WriteLine, () => false);
double everest = source.Sample(27.98806, 86.92521);
Check(everest > 8000 && everest < 8900, $"Everest elevation {everest:0} m");
double deadSea = source.Sample(31.5, 35.5);
Check(deadSea < -350, $"Dead Sea elevation {deadSea:0} m");
double pacific = source.Sample(0, -150);
Check(pacific < -3000, $"Central Pacific depth {pacific:0} m");
double bosphorusShore = source.Sample(41.0082, 28.9784);
Check(bosphorusShore > -50 && bosphorusShore < 150, $"Istanbul old town elevation {bosphorusShore:0} m");
double marmara = source.Sample(40.80, 28.50);
Check(marmara < -100, $"Sea of Marmara depth {marmara:0} m (zoom 12 has 0 m there, bathymetry comes from zoom 10)");
double marmaraShore = source.Sample(40.99488, 28.96616);
Check(marmaraShore < 0, $"Marmara off Kumkapı is sea ({marmaraShore:0.0} m)");
double kumkapiNoise = source.Sample(40.99961, 28.97465);
Check(kumkapiNoise <= -0.5, $"Masked-sea noise near Kumkapı becomes sea ({kumkapiNoise:0.0} m)");
double cached = new ElevationSource(12, cache, Console.WriteLine, () => false).Sample(27.98806, 86.92521);
Check(cached == everest, "disk cache returns identical data");

Console.WriteLine(failures == 0 ? "All checks passed" : $"{failures} check(s) failed");
return failures == 0 ? 0 : 1;
