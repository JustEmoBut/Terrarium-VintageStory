using System;
using Vintagestory.API.Common;
using Vintagestory.API.Server;
using Vintagestory.ServerMods;
using Vintagestory.ServerMods.NoObf;

namespace Terrarium
{
    /// <summary>
    /// Stands in for vanilla GenTerra in the Terrain pass: fills the base rock and sea from real elevation.
    /// Vanilla passes that follow (rock strata, caves, soil layers, ores, vegetation, structures) work on top of it.
    /// </summary>
    public sealed class EarthTerrainGenerator
    {
        private const int ChunkSize = 32;
        private const int ColumnsPerChunk = ChunkSize * ChunkSize;
        private const int MinTerrainY = 2;
        // Headroom above the tallest terrain for trees and snow.
        private const int TopHeadroom = 12;

        private readonly EarthProjection _projection;
        private readonly ElevationSource _elevation;
        private readonly double _verticalScale;
        private readonly GlobalConfig _blocks;
        private readonly int _mapSizeY;

        public EarthTerrainGenerator(ICoreServerAPI api, EarthProjection projection, ElevationSource elevation, double verticalScale)
        {
            _projection = projection;
            _elevation = elevation;
            _verticalScale = verticalScale;
            _blocks = GlobalConfig.GetInstance(api);
            _mapSizeY = api.WorldManager.MapSizeY;
        }

        /// <summary>Y of the topmost solid terrain block for a real-world elevation.</summary>
        public int TerrainHeight(double elevationMeters)
        {
            int maxY = _mapSizeY - TopHeadroom;
            double blocks = _verticalScale == TerrariumSettings.AutoVerticalScale
                ? EarthMath.AutoHeightBlocks(elevationMeters, maxY - TerraGenConfig.seaLevel)
                : elevationMeters / _verticalScale;
            // Land just above 0 m sits one block above the water surface (seaLevel - 1).
            int y = TerraGenConfig.seaLevel + (int)Math.Floor(blocks);
            // Anything below 0 m keeps at least one water block (top water block is seaLevel - 1), even if it rounds to less.
            if (elevationMeters < 0) y = Math.Min(y, TerraGenConfig.seaLevel - 2);
            return Math.Clamp(y, MinTerrainY, maxY);
        }

        public double ElevationAt(double blockX, double blockZ)
            => _elevation.Sample(_projection.Latitude(blockZ), _projection.Longitude(blockX));

        public int TerrainHeightAt(double blockX, double blockZ) => TerrainHeight(ElevationAt(blockX, blockZ));

        public void OnChunkColumnGen(IChunkColumnGenerateRequest request)
        {
            IServerChunk[] chunks = request.Chunks;
            int seaLevel = TerraGenConfig.seaLevel;
            int waterTopY = seaLevel - 1;

            var heights = new int[ColumnsPerChunk];
            int minHeight = int.MaxValue;
            for (int dz = 0; dz < ChunkSize; dz++)
            {
                double blockZ = request.ChunkZ * ChunkSize + dz;
                double lat = _projection.Latitude(blockZ);
                for (int dx = 0; dx < ChunkSize; dx++)
                {
                    double lon = _projection.Longitude(request.ChunkX * ChunkSize + dx);
                    int h = TerrainHeight(_elevation.Sample(lat, lon));
                    heights[dz * ChunkSize + dx] = h;
                    minHeight = Math.Min(minHeight, h);
                }
            }

            // Bulk fill the horizontal layers that are solid in every column.
            chunks[0].Data.SetBlockBulk(0, ChunkSize, ChunkSize, _blocks.mantleBlockId);
            for (int y = 1; y <= minHeight; y++)
            {
                chunks[y / ChunkSize].Data.SetBlockBulk(y % ChunkSize * ColumnsPerChunk, ChunkSize, ChunkSize, _blocks.defaultRockId);
            }

            IMapChunk mapChunk = chunks[0].MapChunk;
            ushort[] terrainHeightMap = mapChunk.WorldGenTerrainHeightMap;
            ushort[] rainHeightMap = mapChunk.RainHeightMap;
            ushort yMax = 0;

            for (int dz = 0; dz < ChunkSize; dz++)
            {
                double lat = _projection.Latitude(request.ChunkZ * ChunkSize + dz);
                int surfaceWaterId = EarthMath.SeaLevelTemperature(lat) < TerraGenConfig.WaterFreezingTempOnGen
                    ? _blocks.lakeIceBlockId
                    : _blocks.saltWaterBlockId;

                for (int dx = 0; dx < ChunkSize; dx++)
                {
                    int column = dz * ChunkSize + dx;
                    int h = heights[column];

                    for (int y = minHeight + 1; y <= h; y++)
                    {
                        chunks[y / ChunkSize].Data[ChunkIndex3d(dx, y % ChunkSize, dz)] = _blocks.defaultRockId;
                    }
                    for (int y = h + 1; y <= waterTopY; y++)
                    {
                        int fluid = y == waterTopY ? surfaceWaterId : _blocks.saltWaterBlockId;
                        chunks[y / ChunkSize].Data.SetFluid(ChunkIndex3d(dx, y % ChunkSize, dz), fluid);
                    }

                    terrainHeightMap[column] = (ushort)h;
                    ushort rainY = (ushort)Math.Max(h, waterTopY);
                    rainHeightMap[column] = rainY;
                    yMax = Math.Max(yMax, rainY);
                }
            }

            mapChunk.YMax = yMax;
        }

        private static int ChunkIndex3d(int x, int y, int z) => (y * ChunkSize + z) * ChunkSize + x;
    }
}
