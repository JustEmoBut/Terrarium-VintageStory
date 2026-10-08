using System;
using Vintagestory.API.Common;
using Vintagestory.ServerMods;

namespace Terrarium
{
    /// <summary>
    /// Replaces the temperature and rainfall of vanilla's climate map with latitude-based values,
    /// keeping vanilla's geologic activity channel. Installed as GenMaps.climateGen so forest, shrub and
    /// flower maps derived from it stay consistent.
    /// </summary>
    public sealed class EarthClimateLayer : MapLayerBase
    {
        private const int ByteMask = 0xFF;

        private readonly MapLayerBase _vanilla;
        private readonly EarthProjection _projection;

        public EarthClimateLayer(MapLayerBase vanilla, EarthProjection projection, long seed) : base(seed)
        {
            _vanilla = vanilla;
            _projection = projection;
        }

        /// <param name="xCoord">Climate cell coordinates; one cell spans TerraGenConfig.climateMapScale blocks.</param>
        public override int[] GenLayer(int xCoord, int zCoord, int sizeX, int sizeZ)
        {
            int[] climate = _vanilla.GenLayer(xCoord, zCoord, sizeX, sizeZ);
            int cellSize = TerraGenConfig.climateMapScale;

            for (int dz = 0; dz < sizeZ; dz++)
            {
                double lat = _projection.Latitude((zCoord + dz) * (double)cellSize + cellSize / 2.0);
                int temperature = Climate.DescaleTemperature((float)EarthMath.SeaLevelTemperature(lat));
                int rain = Math.Clamp((int)(EarthMath.Precipitation(lat) * ByteMask), 0, ByteMask);

                for (int dx = 0; dx < sizeX; dx++)
                {
                    int i = dz * sizeX + dx;
                    int geologicActivity = climate[i] & ByteMask;
                    climate[i] = (temperature << 16) | (rain << 8) | geologicActivity;
                }
            }
            return climate;
        }
    }
}
