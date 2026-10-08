using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using SkiaSharp;

namespace Terrarium
{
    /// <summary>
    /// Real-world elevation from the public AWS Terrain Tiles (Terrarium PNG encoding), cached on disk and in memory.
    /// Thread-safe: worldgen and commands sample it concurrently.
    /// </summary>
    public sealed class ElevationSource
    {
        private const string TileUrlFormat = "https://s3.amazonaws.com/elevation-tiles-prod/terrarium/{0}/{1}/{2}.png";
        // ponytail: whole cache is dropped when full (~64 MB of tiles); fine because worldgen touches a few tiles at a time. Upgrade to LRU if exploring stutters.
        private const int MaxCachedTiles = 256;
        private const float MissingTileElevation = -50f;
        private const int MaxRetryDelaySeconds = 30;
        // Above this zoom some seas (Mediterranean, Black Sea, Marmara, US east coast) are encoded as ~0 m with no bathymetry; this zoom still has it.
        private const int BathymetryZoom = 10;
        // Water-masked sea is not exactly 0 m: it carries ±0.3 m noise, which made straight 1-block land/water stripes.
        private const float MaskedSeaTolerance = 0.5f;
        private const float ShallowSeaElevation = -1f;

        private static readonly HttpClient Http = CreateHttpClient();

        private readonly ConcurrentDictionary<long, Lazy<float[]>> _tiles = new ConcurrentDictionary<long, Lazy<float[]>>();
        private readonly string _cacheDir;
        private readonly Action<string> _logWarning;
        private readonly Func<bool> _isShuttingDown;
        private readonly ElevationSource _bathymetry;

        public readonly int Zoom;

        public ElevationSource(int zoom, string cacheDir, Action<string> logWarning, Func<bool> isShuttingDown)
        {
            Zoom = zoom;
            _cacheDir = cacheDir;
            _logWarning = logWarning;
            _isShuttingDown = isShuttingDown;
            if (zoom > BathymetryZoom) _bathymetry = new ElevationSource(BathymetryZoom, cacheDir, logWarning, isShuttingDown);
        }

        /// <summary>Bilinear elevation in meters; negative values are ocean floor.</summary>
        public double Sample(double lat, double lon)
        {
            EarthMath.LatLonToPixel(lat, lon, Zoom, out double px, out double py);
            return SamplePixel(px, py);
        }

        /// <summary>Bilinear elevation at global pixel coordinates of this source's zoom.</summary>
        private double SamplePixel(double px, double py)
        {
            // Pixel values describe pixel centers.
            px -= 0.5;
            py -= 0.5;
            int x0 = (int)Math.Floor(px);
            int y0 = (int)Math.Floor(py);
            double fx = px - x0;
            double fy = py - y0;

            double top = Lerp(Pixel(x0, y0), Pixel(x0 + 1, y0), fx);
            double bottom = Lerp(Pixel(x0, y0 + 1), Pixel(x0 + 1, y0 + 1), fx);
            return Lerp(top, bottom, fy);
        }

        private static double Lerp(double a, double b, double t) => a + (b - a) * t;

        private float Pixel(int x, int y)
        {
            int worldPixels = EarthMath.TileSize << Zoom;
            x = ((x % worldPixels) + worldPixels) % worldPixels; // wraps across the antimeridian
            y = Math.Clamp(y, 0, worldPixels - 1);
            float[] tile = GetTile(x >> EarthMath.TileSizeShift, y >> EarthMath.TileSizeShift);
            int mask = EarthMath.TileSize - 1;
            return tile[(y & mask) * EarthMath.TileSize + (x & mask)];
        }

        private float[] GetTile(int tileX, int tileY)
        {
            long key = ((long)tileX << 32) | (uint)tileY;
            if (_tiles.TryGetValue(key, out Lazy<float[]> cached)) return cached.Value;
            // Count takes every internal lock, so only check it on a miss.
            if (_tiles.Count > MaxCachedTiles) _tiles.Clear();
            return _tiles.GetOrAdd(key, _ => new Lazy<float[]>(() => LoadTile(tileX, tileY))).Value;
        }

        private float[] LoadTile(int tileX, int tileY)
        {
            string path = Path.Combine(_cacheDir, Zoom.ToString(), tileX.ToString(), tileY + ".png");
            if (File.Exists(path))
            {
                try
                {
                    return FillMissingBathymetry(Decode(File.ReadAllBytes(path)), tileX, tileY);
                }
                catch (Exception e) when (e is InvalidDataException || e is IOException)
                {
                    _logWarning($"Terrarium: cached tile {path} is unreadable ({e.Message}), downloading again");
                    File.Delete(path);
                }
            }

            string url = string.Format(TileUrlFormat, Zoom, tileX, tileY);
            // Generated chunks are permanent, so never fall back to fake terrain on a network error: wait and retry instead.
            for (int attempt = 1; ; attempt++)
            {
                if (_isShuttingDown()) throw new OperationCanceledException("Terrarium: server is shutting down, tile download aborted");
                try
                {
                    using HttpResponseMessage response = Http.GetAsync(url).GetAwaiter().GetResult();
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        _logWarning($"Terrarium: tile {url} does not exist, using flat sea floor");
                        float[] flat = new float[EarthMath.TileSize * EarthMath.TileSize];
                        Array.Fill(flat, MissingTileElevation);
                        return flat;
                    }
                    response.EnsureSuccessStatusCode();
                    byte[] bytes = response.Content.ReadAsByteArrayAsync().GetAwaiter().GetResult();
                    float[] data = Decode(bytes);
                    WriteAtomically(path, bytes);
                    return FillMissingBathymetry(data, tileX, tileY);
                }
                catch (Exception e)
                {
                    int delay = Math.Min(MaxRetryDelaySeconds, 1 << Math.Min(attempt, 5));
                    _logWarning($"Terrarium: downloading {url} failed (attempt {attempt}: {e.Message}), retrying in {delay}s. Terrain generation waits until elevation data is available.");
                    Thread.Sleep(TimeSpan.FromSeconds(delay));
                }
            }
        }

        /// <summary>
        /// Near-0 m pixels at high zoom are water-masked sea: they get the coarser bathymetry, or a shallow
        /// depth near coasts where the coarse data is blended with land.
        /// </summary>
        private float[] FillMissingBathymetry(float[] tile, int tileX, int tileY)
        {
            if (_bathymetry == null) return tile;
            double scale = 1 << (Zoom - BathymetryZoom);
            for (int i = 0; i < tile.Length; i++)
            {
                if (Math.Abs(tile[i]) >= MaskedSeaTolerance) continue;
                double gx = (tileX << EarthMath.TileSizeShift) + (i & (EarthMath.TileSize - 1)) + 0.5;
                double gy = (tileY << EarthMath.TileSizeShift) + (i >> EarthMath.TileSizeShift) + 0.5;
                double coarse = _bathymetry.SamplePixel(gx / scale, gy / scale);
                tile[i] = coarse < 0 ? (float)coarse : ShallowSeaElevation;
            }
            return tile;
        }

        public static float[] Decode(byte[] png)
        {
            using SKBitmap bitmap = SKBitmap.Decode(png) ?? throw new InvalidDataException("not a decodable PNG");
            if (bitmap.Width != EarthMath.TileSize || bitmap.Height != EarthMath.TileSize)
                throw new InvalidDataException($"unexpected tile size {bitmap.Width}x{bitmap.Height}");

            SKColor[] pixels = bitmap.Pixels;
            float[] elevation = new float[pixels.Length];
            for (int i = 0; i < pixels.Length; i++)
            {
                SKColor c = pixels[i];
                elevation[i] = (float)EarthMath.DecodeTerrarium(c.Red, c.Green, c.Blue);
            }
            return elevation;
        }

        private static void WriteAtomically(string path, byte[] bytes)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path));
            string temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, path, overwrite: true);
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Terrarium-VintageStory/1.0");
            return client;
        }
    }
}
