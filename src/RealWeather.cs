using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using ProtoBuf;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.GameContent;

namespace Terrarium
{
    /// <summary>Which parts of the real weather are applied. Unlike the terrain settings these can change at any time.</summary>
    [ProtoContract]
    public sealed class RealWeatherSettings
    {
        public const string EnabledKey = "terrariumRealWeather";
        public static readonly string[] CategoryKeys = { "Precipitation", "Clouds", "Wind", "Temperature" };

        [ProtoMember(1)] public bool Enabled;
        [ProtoMember(2)] public bool Precipitation = true;
        [ProtoMember(3)] public bool Clouds = true;
        [ProtoMember(4)] public bool Wind = true;
        [ProtoMember(5)] public bool Temperature = true;

        /// <summary>A world without the key (old save) has real weather off; categories default to on.</summary>
        public static RealWeatherSettings Read(ITreeAttribute config) => new RealWeatherSettings
        {
            Enabled = Flag(config, EnabledKey, false),
            Precipitation = Flag(config, EnabledKey + "Precipitation", true),
            Clouds = Flag(config, EnabledKey + "Clouds", true),
            Wind = Flag(config, EnabledKey + "Wind", true),
            Temperature = Flag(config, EnabledKey + "Temperature", true),
        };

        private static bool Flag(ITreeAttribute config, string key, bool fallback)
        {
            object value = config?[key]?.GetValue();
            return value == null ? fallback : bool.TryParse(Convert.ToString(value, CultureInfo.InvariantCulture), out bool b) ? b : fallback;
        }
    }

    /// <summary>Real weather of one weather region, already converted to game values.</summary>
    [ProtoContract]
    public sealed class RegionWeather
    {
        [ProtoMember(1)] public int RegionX;
        [ProtoMember(2)] public int RegionZ;
        /// <summary>Game precipitation 0..1 (ClimateCondition.Rainfall).</summary>
        [ProtoMember(3)] public float Rainfall;
        /// <summary>Game rain cloud overlay 0..1; above 0.5 it rains.</summary>
        [ProtoMember(4)] public float RainCloudOverlay;
        /// <summary>Today's real temperature minus the long-term normal for this date, °C.</summary>
        [ProtoMember(5)] public float TemperatureAnomaly;
        [ProtoMember(6)] public string CloudPattern;
        [ProtoMember(7)] public string WindPattern;
        [ProtoMember(8)] public string WeatherEvent;
    }

    [ProtoContract]
    public sealed class RealWeatherPacket
    {
        [ProtoMember(1)] public RealWeatherSettings Settings;
        [ProtoMember(2)] public List<RegionWeather> Regions = new List<RegionWeather>();
    }

    /// <summary>Sent by clients that have the mod, so the server only sends weather packets to clients that can read them.</summary>
    [ProtoContract]
    public sealed class RealWeatherHello
    {
    }

    /// <summary>Pure conversion from Open-Meteo values to game values; no game types, so the self-check can test it.</summary>
    public static class RealWeatherMath
    {
        // Rain rate that counts as the heaviest game rain (Rainfall = 1).
        private const double HeavyRainMmPerHour = 8.0;
        // Even a drizzle should be visible.
        private const float MinRainfall = 0.1f;
        private const float MaxTemperatureAnomaly = 15f;

        /// <summary>Open-Meteo's "precipitation" is the sum over the preceding 15 minutes.</summary>
        public static float Rainfall(double precipitationMmPer15Min)
        {
            if (precipitationMmPer15Min <= 0) return 0f;
            double perHour = precipitationMmPer15Min * 4.0;
            return (float)Math.Clamp(perHour / HeavyRainMmPerHour, MinRainfall, 1.0);
        }

        /// <summary>Vanilla derives Rainfall = cloudness - 0.5, so raining cloudness is above 0.5; dry skies stay below it.</summary>
        public static float RainCloudOverlay(float rainfall, double cloudCoverPercent)
            => rainfall > 0 ? Math.Min(1f, rainfall + 0.5f) : (float)Math.Clamp(cloudCoverPercent / 100.0 * 0.45, 0.0, 0.45);

        public static float TemperatureAnomaly(double todayMean, double normalMean)
            => (float)Math.Clamp(todayMean - normalMean, -MaxTemperatureAnomaly, MaxTemperatureAnomaly);

        /// <summary>WMO weather code and cloud cover to a vanilla weather pattern code.</summary>
        public static string CloudPattern(int wmoCode, double cloudCoverPercent)
        {
            if (wmoCode == 45 || wmoCode == 48) return "haze";
            if (wmoCode >= 95) return "cumulonimbus";
            if (wmoCode >= 51) return cloudCoverPercent >= 80 ? "overcast" : "stratus";
            if (cloudCoverPercent < 10) return "clearsky";
            if (cloudCoverPercent < 30) return "cirrocumulus";
            if (cloudCoverPercent < 55) return "cumulus";
            if (cloudCoverPercent < 80) return "altocumulus";
            return "overcast";
        }

        /// <summary>Wind speed at 10 m (km/h) to a vanilla wind pattern code, roughly by Beaufort scale.</summary>
        public static string WindPattern(double windKmh)
        {
            if (windKmh < 2) return "still";
            if (windKmh < 12) return "lightbreeze";
            if (windKmh < 29) return "mediumbreeze";
            if (windKmh < 50) return "strongbreeze";
            return "storm";
        }

        /// <summary>WMO thunderstorm codes to vanilla weather events (95 thunderstorm, 96/99 with hail).</summary>
        public static string WeatherEvent(int wmoCode) => wmoCode switch
        {
            95 => "lightthunder",
            96 => "smallhail",
            99 => "largehail",
            _ => "noevent",
        };

        /// <summary>Mean of the daily values within ±windowDays of the given day of year, over all years in the series.</summary>
        public static double NormalForDayOfYear(IReadOnlyList<DateTime> days, IReadOnlyList<double?> values, int dayOfYear, int windowDays)
        {
            double sum = 0;
            int count = 0;
            for (int i = 0; i < days.Count; i++)
            {
                if (values[i] is not double v) continue;
                int diff = Math.Abs(days[i].DayOfYear - dayOfYear);
                if (Math.Min(diff, 365 - diff) > windowDays) continue;
                sum += v;
                count++;
            }
            if (count == 0) throw new InvalidOperationException("no temperature normals for this date");
            return sum / count;
        }
    }

    /// <summary>Open-Meteo client (free, non-commercial, CC BY 4.0, &lt;10 000 calls per day).</summary>
    public static class OpenMeteo
    {
        private const string ForecastUrl = "https://api.open-meteo.com/v1/forecast?latitude={0}&longitude={1}"
            + "&current=temperature_2m,precipitation,weather_code,cloud_cover,wind_speed_10m&daily=temperature_2m_mean&forecast_days=1&timezone=GMT";
        private const string ArchiveUrl = "https://archive-api.open-meteo.com/v1/archive?latitude={0}&longitude={1}"
            + "&start_date={2:yyyy}-01-01&end_date={3:yyyy}-12-31&daily=temperature_2m_mean&timezone=GMT";
        private const int NormalYears = 10;
        private const int NormalWindowDays = 7;

        public sealed class Current
        {
            public double Precipitation;
            public int WeatherCode;
            public double CloudCover;
            public double WindSpeed;
            public double TodayMean;
        }

        public static List<Current> FetchCurrent(HttpClient http, IReadOnlyList<(double Lat, double Lon)> points)
        {
            var result = new List<Current>();
            foreach (JsonElement e in Locations(Get(http, string.Format(ForecastUrl, Join(points, p => p.Lat), Join(points, p => p.Lon)))))
            {
                JsonElement current = e.GetProperty("current");
                result.Add(new Current
                {
                    Precipitation = current.GetProperty("precipitation").GetDouble(),
                    WeatherCode = current.GetProperty("weather_code").GetInt32(),
                    CloudCover = current.GetProperty("cloud_cover").GetDouble(),
                    WindSpeed = current.GetProperty("wind_speed_10m").GetDouble(),
                    TodayMean = e.GetProperty("daily").GetProperty("temperature_2m_mean")[0].GetDouble(),
                });
            }
            if (result.Count != points.Count) throw new InvalidOperationException($"Open-Meteo returned {result.Count} locations for {points.Count}");
            return result;
        }

        /// <summary>Long-term mean temperature around today's date, from the last full years of reanalysis data.</summary>
        public static List<double> FetchNormals(HttpClient http, IReadOnlyList<(double Lat, double Lon)> points, DateTime todayUtc)
        {
            int lastYear = todayUtc.Year - 1;
            string url = string.Format(CultureInfo.InvariantCulture, ArchiveUrl, Join(points, p => p.Lat), Join(points, p => p.Lon),
                new DateTime(lastYear - NormalYears + 1, 1, 1), new DateTime(lastYear, 1, 1));
            var result = new List<double>();
            foreach (JsonElement e in Locations(Get(http, url)))
            {
                JsonElement daily = e.GetProperty("daily");
                List<DateTime> days = daily.GetProperty("time").EnumerateArray()
                    .Select(t => DateTime.ParseExact(t.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture)).ToList();
                List<double?> values = daily.GetProperty("temperature_2m_mean").EnumerateArray()
                    .Select(v => v.ValueKind == JsonValueKind.Number ? v.GetDouble() : (double?)null).ToList();
                result.Add(RealWeatherMath.NormalForDayOfYear(days, values, todayUtc.DayOfYear, NormalWindowDays));
            }
            if (result.Count != points.Count) throw new InvalidOperationException($"Open-Meteo returned {result.Count} locations for {points.Count}");
            return result;
        }

        /// <summary>One location returns an object, several return an array; errors return {"error":true,"reason":...}.</summary>
        public static List<JsonElement> Locations(string json)
        {
            JsonElement root = JsonDocument.Parse(json).RootElement;
            if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty("error", out JsonElement error) && error.ValueKind == JsonValueKind.True)
                throw new InvalidOperationException("Open-Meteo error: " + root.GetProperty("reason").GetString());
            return root.ValueKind == JsonValueKind.Array ? root.EnumerateArray().ToList() : new List<JsonElement> { root };
        }

        private static string Get(HttpClient http, string url)
        {
            using HttpResponseMessage response = http.GetAsync(url).GetAwaiter().GetResult();
            string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                Locations(body); // throws with Open-Meteo's reason when it sent one
                response.EnsureSuccessStatusCode();
            }
            return body;
        }

        private static string Join(IReadOnlyList<(double Lat, double Lon)> points, System.Func<(double Lat, double Lon), double> pick)
            => string.Join(",", points.Select(p => pick(p).ToString("0.####", CultureInfo.InvariantCulture)));
    }

    /// <summary>
    /// Climate hook shared by server and client: replaces the current rain and shifts the temperature by the real anomaly.
    /// Only "now" queries are touched; worldgen values and past dates (crop catch-up simulation) stay vanilla.
    /// </summary>
    public sealed class RealWeatherClimate
    {
        // Queries this close to the current time count as "now".
        private const double NowToleranceDays = 1.0 / 24.0;

        private readonly ICoreAPI _api;
        private volatile RealWeatherSettings _settings = new RealWeatherSettings();
        private volatile Dictionary<long, RegionWeather> _regions = new Dictionary<long, RegionWeather>();

        public RealWeatherClimate(ICoreAPI api)
        {
            _api = api;
        }

        public RealWeatherSettings Settings => _settings;

        public IReadOnlyCollection<RegionWeather> Regions => _regions.Values;

        public void Update(RealWeatherSettings settings, IEnumerable<RegionWeather> regions)
        {
            _regions = regions.ToDictionary(r => Key(r.RegionX, r.RegionZ));
            _settings = settings;
        }

        public static long Key(int regionX, int regionZ) => ((long)regionX << 32) | (uint)regionZ;

        public void OnGetClimate(ref ClimateCondition climate, BlockPos pos, EnumGetClimateMode mode, double totalDays)
        {
            RealWeatherSettings settings = _settings;
            if (!settings.Enabled || climate == null || mode == EnumGetClimateMode.WorldGenValues) return;
            if (mode != EnumGetClimateMode.NowValues && Math.Abs(totalDays - _api.World.Calendar.TotalDays) > NowToleranceDays) return;

            int regionSize = _api.World.BlockAccessor.RegionSize;
            if (!_regions.TryGetValue(Key(pos.X / regionSize, pos.Z / regionSize), out RegionWeather weather)) return;

            if (settings.Temperature) climate.Temperature += weather.TemperatureAnomaly;
            if (settings.Precipitation && mode != EnumGetClimateMode.ForSuppliedDate_TemperatureOnly)
            {
                climate.Rainfall = weather.Rainfall;
                climate.RainCloudOverlay = weather.RainCloudOverlay;
            }
        }
    }

    /// <summary>
    /// Server side: fetches real weather for the regions around players, sets the vanilla region weather (clouds, wind, thunder/hail),
    /// and sends the data to clients that have the mod. On any failure the region keeps vanilla weather: unlike terrain,
    /// weather is transient, so falling back is harmless.
    /// </summary>
    public sealed class RealWeatherServer
    {
        public const string ChannelName = "terrarium-realweather";
        private const int TickIntervalMs = 10_000;
        // Open-Meteo updates current conditions every 15 minutes.
        private static readonly TimeSpan RefreshInterval = TimeSpan.FromMinutes(15);
        private static readonly TimeSpan RetryInterval = TimeSpan.FromMinutes(2);
        // Regions nobody has been near for this long are dropped, so travel does not grow memory and packets without bound.
        private static readonly TimeSpan ForgetAfter = TimeSpan.FromHours(1);
        // Regions around each player that get real weather (1 = 3x3 regions).
        private const int RegionRadius = 1;
        // Keeps URLs short and well under Open-Meteo's per-request location limit.
        private const int MaxLocationsPerRequest = 50;

        private readonly ICoreServerAPI _api;
        private readonly EarthProjection _projection;
        private readonly HttpClient _http;
        private readonly RealWeatherClimate _climate;
        private readonly IServerNetworkChannel _channel;
        private readonly HashSet<string> _clientsWithMod = new HashSet<string>();
        private readonly Dictionary<long, (RegionWeather Weather, DateTime FetchedUtc)> _weather = new Dictionary<long, (RegionWeather, DateTime)>();
        private readonly Dictionary<long, double> _normals = new Dictionary<long, double>();
        private readonly Dictionary<long, DateTime> _failedUtc = new Dictionary<long, DateTime>();
        private DateTime _normalsDateUtc;
        private bool _fetching;

        public RealWeatherServer(ICoreServerAPI api, EarthProjection projection, HttpClient http)
        {
            _api = api;
            _projection = projection;
            _http = http;
            _climate = new RealWeatherClimate(api);
            _climate.Update(RealWeatherSettings.Read(api.WorldManager.SaveGame.WorldConfiguration), Array.Empty<RegionWeather>());

            _channel = api.Network.RegisterChannel(ChannelName)
                .RegisterMessageType<RealWeatherPacket>()
                .RegisterMessageType<RealWeatherHello>()
                .SetMessageHandler<RealWeatherHello>((player, _) =>
                {
                    _clientsWithMod.Add(player.PlayerUID);
                    _channel.SendPacket(Snapshot(), player);
                });
            api.Event.PlayerDisconnect += player => _clientsWithMod.Remove(player.PlayerUID);
            api.Event.OnGetClimate += _climate.OnGetClimate;
            api.Event.RegisterGameTickListener(_ => Tick(), TickIntervalMs);
        }

        public RealWeatherSettings Settings => _climate.Settings;

        /// <summary>Stores the settings in the save (so they persist) and applies them right away.</summary>
        public void ChangeSettings(Action<RealWeatherSettings> change)
        {
            RealWeatherSettings s = _climate.Settings;
            var next = new RealWeatherSettings { Enabled = s.Enabled, Precipitation = s.Precipitation, Clouds = s.Clouds, Wind = s.Wind, Temperature = s.Temperature };
            change(next);
            ITreeAttribute config = _api.WorldManager.SaveGame.WorldConfiguration;
            config.SetBool(RealWeatherSettings.EnabledKey, next.Enabled);
            config.SetBool(RealWeatherSettings.EnabledKey + "Precipitation", next.Precipitation);
            config.SetBool(RealWeatherSettings.EnabledKey + "Clouds", next.Clouds);
            config.SetBool(RealWeatherSettings.EnabledKey + "Wind", next.Wind);
            config.SetBool(RealWeatherSettings.EnabledKey + "Temperature", next.Temperature);
            _climate.Update(next, _weather.Values.Select(w => w.Weather));
            Broadcast();
            if (next.Enabled) Tick();
        }

        public RegionWeather WeatherAt(double blockX, double blockZ)
        {
            int regionSize = _api.WorldManager.RegionSize;
            return _weather.TryGetValue(RealWeatherClimate.Key((int)blockX / regionSize, (int)blockZ / regionSize), out var w) ? w.Weather : null;
        }

        private void Tick()
        {
            if (!_climate.Settings.Enabled) return;
            ForgetOldRegions();
            ApplyVanillaRegionWeather();
            if (_fetching) return;

            int regionSize = _api.WorldManager.RegionSize;
            DateTime now = DateTime.UtcNow;
            var wanted = new List<(int X, int Z)>();
            foreach (IPlayer player in _api.World.AllOnlinePlayers)
            {
                if (player.Entity == null) continue;
                int px = (int)player.Entity.Pos.X / regionSize;
                int pz = (int)player.Entity.Pos.Z / regionSize;
                for (int dz = -RegionRadius; dz <= RegionRadius; dz++)
                    for (int dx = -RegionRadius; dx <= RegionRadius; dx++)
                    {
                        long key = RealWeatherClimate.Key(px + dx, pz + dz);
                        bool fresh = _weather.TryGetValue(key, out var w) && now - w.FetchedUtc < RefreshInterval;
                        bool backingOff = _failedUtc.TryGetValue(key, out DateTime failed) && now - failed < RetryInterval;
                        if (!fresh && !backingOff && !wanted.Contains((px + dx, pz + dz))) wanted.Add((px + dx, pz + dz));
                    }
            }
            if (wanted.Count == 0) return;
            if (wanted.Count > MaxLocationsPerRequest) wanted = wanted.GetRange(0, MaxLocationsPerRequest);

            if (_normalsDateUtc != now.Date)
            {
                _normals.Clear();
                _normalsDateUtc = now.Date;
            }
            var needNormals = wanted.Where(r => !_normals.ContainsKey(RealWeatherClimate.Key(r.X, r.Z))).ToList();
            List<(double Lat, double Lon)> points = wanted.Select(r => RegionCenter(r.X, r.Z, regionSize)).ToList();
            List<(double Lat, double Lon)> normalPoints = needNormals.Select(r => RegionCenter(r.X, r.Z, regionSize)).ToList();

            _fetching = true;
            // HTTP must stay off the main thread.
            Task.Run(() =>
            {
                try
                {
                    List<double> normals = normalPoints.Count > 0 ? OpenMeteo.FetchNormals(_http, normalPoints, now) : new List<double>();
                    List<OpenMeteo.Current> current = OpenMeteo.FetchCurrent(_http, points);
                    _api.Event.EnqueueMainThreadTask(() => Store(wanted, current, needNormals, normals, now), "terrarium-realweather");
                }
                catch (Exception e)
                {
                    _api.Logger.Warning("Terrarium: real weather download failed, keeping vanilla weather there for now: {0}", e.Message);
                    _api.Event.EnqueueMainThreadTask(() =>
                    {
                        foreach (var r in wanted) _failedUtc[RealWeatherClimate.Key(r.X, r.Z)] = now;
                        _fetching = false;
                    }, "terrarium-realweather");
                }
            });
        }

        private void ForgetOldRegions()
        {
            DateTime now = DateTime.UtcNow;
            List<long> old = _weather.Where(w => now - w.Value.FetchedUtc > ForgetAfter).Select(w => w.Key).ToList();
            if (old.Count == 0) return;
            foreach (long key in old) _weather.Remove(key);
            _climate.Update(_climate.Settings, _weather.Values.Select(w => w.Weather));
            Broadcast();
        }

        private void Store(List<(int X, int Z)> regions, List<OpenMeteo.Current> current, List<(int X, int Z)> normalRegions, List<double> normals, DateTime fetchedUtc)
        {
            try
            {
                StoreUnsafe(regions, current, normalRegions, normals, fetchedUtc);
            }
            finally
            {
                // A failure here must not stop all future refreshes.
                _fetching = false;
            }
        }

        private void StoreUnsafe(List<(int X, int Z)> regions, List<OpenMeteo.Current> current, List<(int X, int Z)> normalRegions, List<double> normals, DateTime fetchedUtc)
        {
            for (int i = 0; i < normalRegions.Count; i++) _normals[RealWeatherClimate.Key(normalRegions[i].X, normalRegions[i].Z)] = normals[i];
            for (int i = 0; i < regions.Count; i++)
            {
                long key = RealWeatherClimate.Key(regions[i].X, regions[i].Z);
                OpenMeteo.Current c = current[i];
                float rainfall = RealWeatherMath.Rainfall(c.Precipitation);
                _weather[key] = (new RegionWeather
                {
                    RegionX = regions[i].X,
                    RegionZ = regions[i].Z,
                    Rainfall = rainfall,
                    RainCloudOverlay = RealWeatherMath.RainCloudOverlay(rainfall, c.CloudCover),
                    TemperatureAnomaly = RealWeatherMath.TemperatureAnomaly(c.TodayMean, _normals[key]),
                    CloudPattern = RealWeatherMath.CloudPattern(c.WeatherCode, c.CloudCover),
                    WindPattern = RealWeatherMath.WindPattern(c.WindSpeed),
                    WeatherEvent = RealWeatherMath.WeatherEvent(c.WeatherCode),
                }, fetchedUtc);
                _failedUtc.Remove(key);
            }
            _climate.Update(_climate.Settings, _weather.Values.Select(w => w.Weather));
            ApplyVanillaRegionWeather();
            Broadcast();
        }

        /// <summary>
        /// Region patterns are synced to every client by vanilla, so clouds, wind and thunder show even without the mod on the client.
        /// Vanilla swaps patterns when they expire; re-applying every tick keeps the real ones.
        /// </summary>
        private void ApplyVanillaRegionWeather()
        {
            RealWeatherSettings settings = _climate.Settings;
            WeatherSystemServer weatherSystem = _api.ModLoader.GetModSystem<WeatherSystemServer>();
            if (weatherSystem == null) return;
            foreach (var (weather, _) in _weather.Values)
            {
                WeatherSimulationRegion sim = weatherSystem.getOrCreateWeatherSimForRegion(weather.RegionX, weather.RegionZ);
                if (sim == null) continue;
                if (settings.Clouds && sim.NewWePattern?.config.Code != weather.CloudPattern) sim.SetWeatherPattern(weather.CloudPattern, false);
                if (settings.Wind && sim.CurWindPattern?.config.Code != weather.WindPattern) sim.SetWindPattern(weather.WindPattern, false);
                if (settings.Precipitation && sim.CurWeatherEvent?.config.Code != weather.WeatherEvent) sim.SetWeatherEvent(weather.WeatherEvent, false);
            }
        }

        private (double Lat, double Lon) RegionCenter(int regionX, int regionZ, int regionSize)
            => (_projection.Latitude((regionZ + 0.5) * regionSize), _projection.Longitude((regionX + 0.5) * regionSize));

        private RealWeatherPacket Snapshot() => new RealWeatherPacket
        {
            Settings = _climate.Settings,
            Regions = _weather.Values.Select(w => w.Weather).ToList(),
        };

        private void Broadcast()
        {
            IServerPlayer[] targets = _api.World.AllOnlinePlayers.OfType<IServerPlayer>().Where(p => _clientsWithMod.Contains(p.PlayerUID)).ToArray();
            if (targets.Length > 0) _channel.SendPacket(Snapshot(), targets);
        }
    }

    /// <summary>Client side: applies the server's real rain and temperature to the client's own climate queries (rain particles, sounds).</summary>
    public static class RealWeatherClient
    {
        public static void Start(ICoreClientAPI api)
        {
            var climate = new RealWeatherClimate(api);
            IClientNetworkChannel channel = api.Network.RegisterChannel(RealWeatherServer.ChannelName)
                .RegisterMessageType<RealWeatherPacket>()
                .RegisterMessageType<RealWeatherHello>()
                .SetMessageHandler<RealWeatherPacket>(packet => climate.Update(packet.Settings ?? new RealWeatherSettings(), packet.Regions ?? new List<RegionWeather>()));
            api.Event.OnGetClimate += climate.OnGetClimate;
            api.Event.LevelFinalize += () =>
            {
                // Only Terrarium worlds register the channel on the server.
                if (channel.Connected) channel.SendPacket(new RealWeatherHello());
            };
        }
    }
}
