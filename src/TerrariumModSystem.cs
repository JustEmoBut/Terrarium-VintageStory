using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;

namespace Terrarium
{
    public sealed class TerrariumSettings
    {
        public bool Enabled;
        public double OriginLatitude = 41.0082;
        public double OriginLongitude = 28.9784;
        public double HorizontalScale = 40;
        /// <summary>Sentinel for the "auto" vertical scale (log curve fitted to the world height).</summary>
        public const double AutoVerticalScale = 0;
        public double VerticalScale = AutoVerticalScale;

        /// <summary>Reads the world configuration. A world without the key (created before the mod was installed) stays vanilla.</summary>
        public static TerrariumSettings Read(ITreeAttribute config, ILogger logger)
        {
            var s = new TerrariumSettings();
            s.Enabled = bool.TryParse(Value(config, "terrariumEnabled"), out bool enabled) && enabled;
            s.OriginLatitude = Number(config, "terrariumOriginLatitude", s.OriginLatitude, -90, 90, logger);
            s.OriginLongitude = Number(config, "terrariumOriginLongitude", s.OriginLongitude, -180, 180, logger);
            s.HorizontalScale = Number(config, "terrariumHorizontalScale", s.HorizontalScale, 1, 10000, logger);
            s.VerticalScale = Value(config, "terrariumVerticalScale") == "auto"
                ? AutoVerticalScale
                : Number(config, "terrariumVerticalScale", s.VerticalScale, 1, 10000, logger);
            return s;
        }

        private static string Value(ITreeAttribute config, string code)
        {
            object value = config?[code]?.GetValue();
            return value == null ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
        }

        private static double Number(ITreeAttribute config, string code, double fallback, double min, double max, ILogger logger)
        {
            string text = Value(config, code);
            if (text == null) return fallback;
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v) && v >= min && v <= max) return v;
            logger.Error("Terrarium: world setting {0}='{1}' is invalid (expected {2}..{3}), using {4}", code, text, min, max, fallback);
            return fallback;
        }

        public EarthProjection CreateProjection(int mapSizeX, int mapSizeZ)
            => new EarthProjection(OriginLatitude, OriginLongitude, HorizontalScale, mapSizeX / 2.0, mapSizeZ / 2.0);
    }

    public sealed class TerrariumModSystem : ModSystem
    {
        // After every vanilla worldgen system (GenTerra 0, GenMaps 0.1, ... ), so their handlers exist and ours run last.
        private const double AfterVanillaWorldGen = 1.0;
        private const string GeocodeUrlFormat = "https://nominatim.openstreetmap.org/search?q={0}&format=jsonv2&limit=1&accept-language=en";
        private const int TeleportHeadroom = 2;

        private static readonly HttpClient Http = CreateHttpClient();
        private static readonly TimeSpan GeocodeInterval = TimeSpan.FromSeconds(1);
        private static readonly object GeocodeLock = new object();
        private static DateTime _lastGeocodeUtc = DateTime.MinValue;

        private ICoreServerAPI _sapi;
        private TerrariumSettings _settings;
        private EarthProjection _projection;
        private EarthTerrainGenerator _generator;

        public override double ExecuteOrder() => AfterVanillaWorldGen;

        public override void StartClientSide(ICoreClientAPI api)
        {
            // Sun path, day length and seasons use the real latitude.
            api.Event.LevelFinalize += () =>
            {
                TerrariumSettings settings = TerrariumSettings.Read(api.World.Config, api.Logger);
                if (!settings.Enabled) return;
                EarthProjection projection = settings.CreateProjection(api.World.BlockAccessor.MapSizeX, api.World.BlockAccessor.MapSizeZ);
                api.World.Calendar.OnGetLatitude = z => projection.Latitude(z) / 90.0;
            };
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            _sapi = api;
            api.Event.InitWorldGenerator(OnInitWorldGen, "standard");
            RegisterCommands(api);
        }

        private void OnInitWorldGen()
        {
            TerrariumSettings settings = TerrariumSettings.Read(_sapi.WorldManager.SaveGame.WorldConfiguration, _sapi.Logger);
            if (!settings.Enabled) return;

            _settings = settings;
            _projection = settings.CreateProjection(_sapi.WorldManager.MapSizeX, _sapi.WorldManager.MapSizeZ);
            var elevation = new ElevationSource(
                EarthMath.ZoomForScale(settings.HorizontalScale),
                _sapi.GetOrCreateDataPath("TerrariumCache"),
                msg => _sapi.Logger.Warning(msg),
                () => _sapi.Server.IsShuttingDown);
            _generator = new EarthTerrainGenerator(_sapi, _projection, elevation, settings.VerticalScale);

            List<ChunkColumnGenerationDelegate> terrainPass = _sapi.Event.GetRegisteredWorldGenHandlers("standard").OnChunkColumnGen[(int)EnumWorldGenPass.Terrain];
            int vanillaIndex = terrainPass.FindIndex(handler => handler.Target is GenTerra);
            if (vanillaIndex < 0)
            {
                throw new InvalidOperationException("Terrarium: vanilla GenTerra handler not found, another mod may have replaced it. Earth terrain cannot be generated.");
            }
            // Replace in place so the vanilla passes after it (strata, caves, block layers) keep their order.
            terrainPass[vanillaIndex] = _generator.OnChunkColumnGen;

            GenMaps genMaps = _sapi.ModLoader.GetModSystem<GenMaps>();
            genMaps.climateGen = new EarthClimateLayer(genMaps.climateGen, _projection, _sapi.WorldManager.Seed);

            EarthProjection projection = _projection;
            _sapi.World.Calendar.OnGetLatitude = z => projection.Latitude(z) / 90.0;

            _sapi.Logger.Notification("Terrarium: generating Earth, world center = {0:0.####}, {1:0.####}, {2} m/block horizontal, vertical scale {3}, elevation zoom {4}",
                settings.OriginLatitude, settings.OriginLongitude, settings.HorizontalScale,
                settings.VerticalScale == TerrariumSettings.AutoVerticalScale ? "auto" : settings.VerticalScale + " m/block", elevation.Zoom);
        }

        private void RegisterCommands(ICoreServerAPI api)
        {
            api.ChatCommands.Create("geotp")
                .WithDescription("Teleport to a real-world location: /geotp <latitude> <longitude> or /geotp <place name>")
                .RequiresPrivilege(Privilege.tp)
                .RequiresPlayer()
                .WithArgs(api.ChatCommands.Parsers.All("location"))
                .HandleWith(OnGeoTp);

            api.ChatCommands.Create("geopos")
                .WithDescription("Show your real-world latitude, longitude and elevation")
                .RequiresPrivilege(Privilege.chat)
                .RequiresPlayer()
                .HandleWith(OnGeoPos);
        }

        private TextCommandResult OnGeoPos(TextCommandCallingArgs args)
        {
            if (_projection == null) return TextCommandResult.Error("This is not a Terrarium (Earth) world.");
            var pos = args.Caller.Entity.Pos;
            return TextCommandResult.Success(string.Format(CultureInfo.InvariantCulture,
                "Latitude {0:0.#####}, longitude {1:0.#####}, real elevation {2:0} m",
                _projection.Latitude(pos.Z), _projection.Longitude(pos.X), _generator.ElevationAt(pos.X, pos.Z)));
        }

        private TextCommandResult OnGeoTp(TextCommandCallingArgs args)
        {
            if (_generator == null) return TextCommandResult.Error("This is not a Terrarium (Earth) world.");
            string location = ((string)args[0])?.Trim();
            if (string.IsNullOrEmpty(location)) return TextCommandResult.Error("Usage: /geotp <latitude> <longitude> or /geotp <place name>");

            var player = (IServerPlayer)args.Caller.Player;
            int groupId = args.Caller.FromChatGroupId;

            // Geocoding and elevation download can block for seconds; keep them off the main thread.
            Task.Run(() =>
            {
                try
                {
                    if (!TryParseCoordinates(location, out double lat, out double lon, out string error))
                    {
                        if (error != null)
                        {
                            Reply(player, groupId, error, EnumChatType.CommandError);
                            return;
                        }
                        if (!TryGeocode(location, out lat, out lon, out string name))
                        {
                            Reply(player, groupId, $"No place found for '{location}'.", EnumChatType.CommandError);
                            return;
                        }
                        Reply(player, groupId, $"Found: {name}", EnumChatType.CommandSuccess);
                    }

                    double x = Math.Floor(_projection.BlockX(lon)) + 0.5;
                    double z = Math.Floor(_projection.BlockZ(lat)) + 0.5;
                    if (x < 0 || z < 0 || x >= _sapi.WorldManager.MapSizeX || z >= _sapi.WorldManager.MapSizeZ)
                    {
                        Reply(player, groupId, "That location is outside this world at its current scale.", EnumChatType.CommandError);
                        return;
                    }
                    int y = Math.Max(_generator.TerrainHeightAt(x, z), TerraGenConfig.seaLevel - 1) + TeleportHeadroom;

                    _sapi.Event.EnqueueMainThreadTask(() =>
                    {
                        player.Entity.TeleportToDouble(x, y, z);
                        player.SendMessage(groupId, string.Format(CultureInfo.InvariantCulture, "Teleported to {0:0.#####}, {1:0.#####}", lat, lon), EnumChatType.CommandSuccess);
                    }, "terrarium-geotp");
                }
                catch (Exception e)
                {
                    _sapi.Logger.Error("Terrarium: /geotp {0} failed: {1}", location, e);
                    Reply(player, groupId, "Teleport failed: " + e.Message, EnumChatType.CommandError);
                }
            });

            return TextCommandResult.Success("Locating...");
        }

        private void Reply(IServerPlayer player, int groupId, string message, EnumChatType type)
            => _sapi.Event.EnqueueMainThreadTask(() => player.SendMessage(groupId, message, type), "terrarium-reply");

        /// <summary>Accepts "lat lon" or "lat, lon". Returns false with a null error when the text is a place name.</summary>
        public static bool TryParseCoordinates(string text, out double lat, out double lon, out string error)
        {
            lat = lon = 0;
            error = null;
            string[] parts = text.Split(new[] { ' ', ',', ';' }, StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length != 2
                || !double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out lat)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out lon))
            {
                return false;
            }
            if (lat < -90 || lat > 90 || lon < -180 || lon > 180)
            {
                error = "Latitude must be between -90 and 90, longitude between -180 and 180.";
                return false;
            }
            return true;
        }

        private static bool TryGeocode(string query, out double lat, out double lon, out string name)
        {
            lat = lon = 0;
            name = null;
            string url = string.Format(GeocodeUrlFormat, Uri.EscapeDataString(query));
            string json;
            // Nominatim's usage policy allows at most one request per second; serialize all players' lookups.
            lock (GeocodeLock)
            {
                TimeSpan wait = _lastGeocodeUtc + GeocodeInterval - DateTime.UtcNow;
                if (wait > TimeSpan.Zero) Thread.Sleep(wait);
                try
                {
                    json = Http.GetStringAsync(url).GetAwaiter().GetResult();
                }
                finally
                {
                    _lastGeocodeUtc = DateTime.UtcNow;
                }
            }
            using JsonDocument doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0) return false;

            JsonElement first = doc.RootElement[0];
            lat = double.Parse(first.GetProperty("lat").GetString(), CultureInfo.InvariantCulture);
            lon = double.Parse(first.GetProperty("lon").GetString(), CultureInfo.InvariantCulture);
            name = first.TryGetProperty("display_name", out JsonElement display) ? display.GetString() : query;
            return true;
        }

        private static HttpClient CreateHttpClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            // Nominatim's usage policy requires an identifying User-Agent.
            client.DefaultRequestHeaders.UserAgent.ParseAdd("Terrarium-VintageStory/1.0 (+https://github.com/JustEmoBut/terrarium-vintagestory)");
            return client;
        }
    }
}
