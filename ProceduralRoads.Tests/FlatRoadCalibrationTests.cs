using System;
using System.Collections.Generic;
using ProceduralRoads.TestFixtures;
using UnityEngine;
using Xunit;

namespace ProceduralRoads.Tests;

public sealed class FlatRoadCalibrationTests
{
    [Fact]
    public void RealWriterMatchesDeclaredTwoZoneFixtureIncludingFalloffClampAndRepeat()
    {
        float amplitude = RoadEarthworkNoise.Amplitude, spread = RoadEarthworkNoise.FillSpread;
        float batter = RoadTerrainModifier.BatterPerMetre;
        var oldWorld = WorldGenerator.instance;
        var oldZdo = ZDOMan.instance;
        try
        {
            RoadTerrainModifier.ResetDebugCounters(); RoadSpatialGrid.Clear();
            WorldGenerator.instance = new SyntheticWorld(); ZDOMan.instance = new ZDOMan();
            RoadEarthworkNoise.Amplitude = 0; RoadEarthworkNoise.FillSpread = 0; RoadTerrainModifier.BatterPerMetre = 0;
            for (int offset = 0; offset != 2; offset++)
            {
                var zone = new Vector2s(FlatRoadFixture.ZoneX + offset, FlatRoadFixture.ZoneZ);
                var hm = Heightmap.CreateForZone(zone);
                Heightmap.Registered = hm;
                hm.AuthoredHeight = (_, _) => FlatRoadFixture.Platform;
                var tc = hm.m_terrainComp!;
                hm.RebuildTerrain(); Check(hm, offset, "baseline");
                for (int stage = 0; stage < FlatRoadFixture.Stages.Length; stage++)
                {
                    var road = new List<RoadSpatialGrid.RoadPoint>();
                    for (int x = 16; x <= 48; x++) road.Add(new(new Vector2(FlatRoadFixture.Origin + x, FlatRoadFixture.Origin), FlatRoadFixture.Width, FlatRoadFixture.Targets[stage]));
                    RoadTerrainModifier.ApplyRoadTerrainModsWithContext(zone, road, hm, tc);
                    Check(hm, offset, FlatRoadFixture.Stages[stage]);
                }
            }
        }
        finally
        {
            RoadEarthworkNoise.Amplitude = amplitude; RoadEarthworkNoise.FillSpread = spread; RoadTerrainModifier.BatterPerMetre = batter;
            RoadTerrainModifier.ResetDebugCounters(); RoadSpatialGrid.Clear(); Heightmap.Registered = null;
            WorldGenerator.instance = oldWorld; ZDOMan.instance = oldZdo;
        }
    }
    private static void Check(Heightmap hm, int offset, string stage)
    {
        foreach (var sample in FlatRoadFixture.Samples(offset))
        {
            int x = sample.x - offset * 64 + 32, z = sample.z + 32;
            Assert.InRange(Math.Abs(hm.LastRenderedHeights![z * 65 + x] - FlatRoadFixture.Expected(stage, sample.z)), 0, .001f);
        }
    }
}
