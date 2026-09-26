using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BepInEx;
using UnityEngine;
using valheimCLI;
using valheimCLI.Extensions;

namespace ProceduralRoads.TestAdapter;
[BepInPlugin("testing.proceduralroads.adapter", "ProceduralRoads Test Adapter", "0.1.0")]
[BepInDependency("warpalicious.ProceduralRoads")]
public sealed class Plugin : BaseUnityPlugin
{
    private ExtensionRegistration? _registration;
    private IEnumerator Start()
    {
        float deadline = Time.realtimeSinceStartup + 30;
        while (valheimCLIPlugin.Instance?.Extensions == null)
        {
            if (Time.realtimeSinceStartup > deadline) { Logger.LogError("Stable CLI extension API did not become ready; adapter disabled."); yield break; }
            yield return null;
        }
        _registration = valheimCLIPlugin.Instance.Extensions.Register("roads.testing", "0.1.0", 1,
            new ExtensionCommand("network", "Read completed network and outstanding append counts", Network, readOnly: true, role: ExtensionRole.Server, needsWorld: true),
            new ExtensionCommand("bridge-zone", "Read marked bridge ZDOs in one zone: <zoneX> <zoneZ>", BridgeZone, readOnly: true, role: ExtensionRole.Server, needsWorld: true));
    }
    private void OnDestroy() => _registration?.Dispose();
    private static IEnumerator Network(ExtensionContext context)
    {
        if (context.Arguments.Count != 0) { context.Fail("usage", "network takes no arguments"); yield break; }
        context.Succeed(new Dictionary<string, object?>
        {
            ["source"] = "roads-memory", ["complete"] = RoadNetworkGenerator.RoadsAvailable,
            ["version"] = RoadSpatialGrid.RoadNetworkVersion, ["cells"] = RoadSpatialGrid.GridCellsWithRoads,
            ["points"] = RoadSpatialGrid.TotalRoadPoints, ["crossings"] = RoadNetworkGenerator.GetRoadCrossings().Count,
            ["pendingZones"] = BridgeAppendQueue.Count, ["loadedFromSave"] = RoadNetworkGenerator.RoadsLoadedFromZDO
        }); yield break;
    }
    private static IEnumerator BridgeZone(ExtensionContext context)
    {
        if (context.Arguments.Count != 2 || !int.TryParse(context.Arguments[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out int x) ||
            !int.TryParse(context.Arguments[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out int z) || Math.Abs((long)x) > 320 || Math.Abs((long)z) > 320)
        { context.Fail("usage", "bridge-zone <zoneX> <zoneZ> within +/-320"); yield break; }
        var zone = new Vector2s(x, z); var found = new List<ZDO>();
        ZDOMan.instance.FindObjects(zone, found, new HashSet<ZoneSystem.SectorIndex>());
        var pieces = found.Where(item => item.GetInt(BridgePlans.MarkerHash) == 1).ToArray();
        if (pieces.Length > 512) { context.Fail("census_limit", "More than 512 marked pieces in one zone; result omitted, not truncated."); yield break; }
        context.Succeed(new Dictionary<string, object?>
        {
            ["source"] = "zdo-store", ["complete"] = true, ["zoneX"] = x, ["zoneZ"] = z,
            ["pieces"] = pieces.Select(item => new Dictionary<string, object?>
            {
                ["id"] = item.m_uid.ToString(), ["prefabHash"] = item.GetPrefab(),
                ["position"] = new[] { item.GetPosition().x, item.GetPosition().y, item.GetPosition().z },
                ["rotation"] = new[] { item.GetRotation().x, item.GetRotation().y, item.GetRotation().z, item.GetRotation().w }
            }).ToArray()
        }); yield break;
    }
}
