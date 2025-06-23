using BotanistUseSoilPourer;
using FishNet;
using HarmonyLib;
using MelonLoader;
using Newtonsoft.Json;
using ScheduleOne;
using ScheduleOne.DevUtilities;
using ScheduleOne.Employees;
using ScheduleOne.EntityFramework;
using ScheduleOne.ItemFramework;
using ScheduleOne.NPCs.Behaviour;
using ScheduleOne.ObjectScripts;
using ScheduleOne.ObjectScripts.Soil;
using ScheduleOne.Tiles;
using ScheduleOne.Trash;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEngine;

[assembly: MelonInfo(typeof(BotanistUseSoilPourerMod), "Botanist Use Soil Pourer", "1.0.1", "Fortis")]
[assembly: MelonGame("TVGS", "Schedule I")]

namespace BotanistUseSoilPourer;

public class BotanistUseSoilPourerMod : MelonMod
{
    private static Configuration _config = null!;

    private static readonly string ConfigDirectoryPath = Path.Combine(MelonLoader.Utils.MelonEnvironment.UserDataDirectory, "BotanistUseSoilPourer");
    private static readonly string ConfigPath = Path.Combine(ConfigDirectoryPath, "configuration.json");

    public override void OnInitializeMelon()
    {
        Log("Initializing...", LogLevel.Info);
        LoadConfigFromFile();

        Log($"Enabled: {_config.enabled}", LogLevel.Info);
        if (_config.enabled)
        {
            if (_config.debug)
                Log("Debug Mode Enabled", LogLevel.Debug);

            Log("Initialized", LogLevel.Info);
        }
    }

    public override void OnDeinitializeMelon()
    {
        Log("(OnDeinitializeMelon) Mod unloaded", LogLevel.Info);
    }

    private static Dictionary<Pot, SoilPourer> CheckPreventer = new Dictionary<Pot, SoilPourer>();

    // Reflection methods I need
    private static MethodInfo IsAtPot = typeof(PotActionBehaviour).GetMethod("IsAtPot", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
    private static MethodInfo StopPerformAction = typeof(PotActionBehaviour).GetMethod("StopPerformAction", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
    private static MethodInfo CompleteAction = typeof(PotActionBehaviour).GetMethod("CompleteAction", BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);

    [HarmonyPatch(typeof(PotActionBehaviour), "StartAction")]
    public static class PotActionBehaviourStartActionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PotActionBehaviour __instance)
        {
            if (IsAtPot is not null)
            {
                if (__instance.AssignedPot is null)
                    return;
                if (__instance.CurrentActionType is not PotActionBehaviour.EActionType.PourSoil)
                    return;
                // Prevent checking for soil pourers again after finishing one
                if ((bool)IsAtPot.Invoke(__instance, null))
                    return;

                SoilPourer? pourer = GetPotSoilPourer(__instance.AssignedPot);
                if (pourer is null)
                    return;

                if (!CheckPreventer.ContainsKey(__instance.AssignedPot))
                {
                    Log("(PotActionBehaviourStartActionPatch/Postfix) Adding pot to check preventer", LogLevel.Debug);
                    CheckPreventer.Add(__instance.AssignedPot, pourer);
                }
            }
            else
                Log("(PotActionBehaviourStartActionPatch/Postfix) IsAtPot is null", LogLevel.Warn);
        }
    }

    [HarmonyPatch(typeof(PotActionBehaviour), nameof(PotActionBehaviour.ActiveMinPass))]
    public static class PotActionBehviourActiveMinPassPatch
    {
        [HarmonyPrefix]
        private static bool Prefix(PotActionBehaviour __instance)
        {
            if (__instance.CurrentActionType is not PotActionBehaviour.EActionType.PourSoil)
                return true;
            if (__instance.CurrentState is PotActionBehaviour.EState.Idle)
            {
                SoilPourer? pourer = null;
                if (CheckPreventer.ContainsKey(__instance.AssignedPot))
                    pourer = CheckPreventer[__instance.AssignedPot];
                if (pourer is null)
                    return true;
                if (pourer.SoilID == string.Empty)
                    return true;

                __instance.WalkToPot();
                return false;
            }

            return true;
        }

        [HarmonyPostfix]
        private static void Postfix(PotActionBehaviour __instance)
        {
            if (IsAtPot is not null)
            {
                if (__instance.CurrentActionType is not PotActionBehaviour.EActionType.PourSoil)
                    return;

                SoilPourer? pourer = null;
                if (CheckPreventer.ContainsKey(__instance.AssignedPot))
                    pourer = CheckPreventer[__instance.AssignedPot];
                if (pourer is null)
                    return;

                if (pourer.SoilID == string.Empty)
                {
                    if (!(bool)IsAtPot.Invoke(__instance, null))
                        return;

                    PourSoilIntoPourer(__instance, pourer);
                    if (pourer.SoilID != string.Empty)
                    {
                        pourer.ActivateSound.Play();
                        DispenseSoilQuick(pourer.SoilID, __instance.AssignedPot);
                        pourer.SendSoil(string.Empty);
                        pourer.SetSoilLevel(0f);
                        EndBotanistTask(__instance);
                        return;
                    }
                    return;
                }

                if (!(bool)IsAtPot.Invoke(__instance, null))
                    return;

                Log("(PotActionBehaviourActiveMinPassPatch/Postfix) Activating soil pourer", LogLevel.Debug);
                pourer.ActivateSound.Play();
                DispenseSoilQuick(pourer.SoilID, __instance.AssignedPot);
                pourer.SendSoil(string.Empty);
                pourer.SetSoilLevel(0f);
                EndBotanistTask(__instance);
            }
            else
                Log("(PotActionBehaviourActiveMinPassPatch/Postfix) IsAtPot is null", LogLevel.Warn);
        }
    }

    private static void DispenseSoilQuick(string id, Pot pot)
    {
        pot.SetSoilID(id);
        pot.SetSoilState(Pot.ESoilState.Flat);
        pot.AddSoil(pot.SoilCapacity);
        pot.SetSoilUses(Registry.GetItem<SoilDefinition>(id).Uses);

        if (InstanceFinder.IsServer)
            pot.PushSoilDataToServer();
    }

    private static void EndBotanistTask(PotActionBehaviour behaviour)
    {
        if (StopPerformAction is not null && CompleteAction is not null)
        {
            if (CheckPreventer.ContainsKey(behaviour.AssignedPot))
            {
                Log("(EndBotanistTask) Removing pot from check preventer", LogLevel.Debug);
                CheckPreventer.Remove(behaviour.AssignedPot);
            }

            StopPerformAction.Invoke(behaviour, null);
            CompleteAction.Invoke(behaviour, null);
            behaviour.SendEnd();

            Botanist botanist = behaviour.Npc as Botanist;
            if (botanist is not null)
            {
                botanist.SetIdle(true);
            }
            else
                Log("(EndBotanistTask) Botanist is null", LogLevel.Warn);
        }
        else
            Log($"(EndBotanistTask) StopPeformAction is null: {StopPerformAction is null}, CompleteAction is null: {CompleteAction is null}", LogLevel.Debug);
    }

    private static void PourSoilIntoPourer(PotActionBehaviour behaviour, SoilPourer pourer)
    {
        if (behaviour.AssignedPot is null)
        {
            Log("(PourSoilIntoPourer) PotActionBehaviour is null", LogLevel.Warn);
            return;
        }

        ItemInstance soil = null;
        string[] requiredItemIds = GetSoilIDS();
        for (int i = 0; i < requiredItemIds.Length; i++)
        {
            soil = behaviour.Npc.Inventory.GetFirstItem(requiredItemIds[i]);
            if (soil is not null)
                break;
        }

        if (soil is null)
        {
            Log("(PourSoilIntoPourer) Botanist does not have soil", LogLevel.Warn);
            return;
        }

        SoilDefinition soilDefinition = soil.Definition as SoilDefinition;
        if (soilDefinition is null)
        {
            Log("(PourSoilIntoPourer) SoilDefinition is null", LogLevel.Warn);
            return;
        }

        pourer.SendSoil(soilDefinition.ID);
        NetworkSingleton<TrashManager>.Instance.CreateTrashItem((soilDefinition.Equippable as Equippable_Soil).PourablePrefab.TrashItem.ID, behaviour.transform.position + Vector3.up * 0.5f, Random.rotation);
        soil.ChangeQuantity(-1);
    }

    private static string[] GetSoilIDS()
        => new string[3] { "soil", "longlifesoil", "extralonglifesoil" };

    private static SoilPourer? GetPotSoilPourer(Pot pot)
    {
        Log("(GetSoilPourer) Fired", LogLevel.Debug);
        Coordinate assignedPotCoords = new Coordinate(pot.OriginCoordinate);
        Log($"(GetPotSoilPourers) Assigned Pot Coords x: {assignedPotCoords.x}, y: {assignedPotCoords.y}", LogLevel.Debug);
        List<SoilPourer> pourersAroundPot = new List<SoilPourer>();
        for (int t = 1; t < 4; t++)
        {
            Coordinate posXCoord = new Coordinate(assignedPotCoords.x + t, assignedPotCoords.y);
            Coordinate negXCoord = new Coordinate(assignedPotCoords.x - t, assignedPotCoords.y);
            Coordinate posYCoord = new Coordinate(assignedPotCoords.x, assignedPotCoords.y + t);
            Coordinate negYCoord = new Coordinate(assignedPotCoords.x, assignedPotCoords.y - t);


            Tile posXTile = pot.OwnerGrid.GetTile(posXCoord);
            Tile negXTile = pot.OwnerGrid.GetTile(negXCoord);
            Tile posYTile = pot.OwnerGrid.GetTile(posYCoord);
            Tile negYTile = pot.OwnerGrid.GetTile(negYCoord);

            Tile[] tiles = { posXTile, negXTile, posYTile, negYTile };

            for (int i = 0; i < tiles.Length; i++)
            {
                if (tiles[i] is null)
                {
                    //Log($"(GetPotSoilPourers) Tile at index {i} is null, skipping", LogLevel.Debug);
                    continue;
                }
                Log($"(GetPotSoilPourers) Checking tile at x: {tiles[i].x}, y: {tiles[i].y}", LogLevel.Debug);
                if (!TryGetSoilPourer(tiles[i], out SoilPourer? pourer))
                {
                    Log($"(GetPotSoilPourers) Tile at x: {tiles[i].x}, y: {tiles[i].y} contains no soil pourers", LogLevel.Debug);
                    continue;
                }
                if (pourer is null)
                {
                    Log($"(GetPotSoilPourers) Tile at x: {tiles[i].x}, y: {tiles[i].y} contains a soil pourer but soil pourer returned is null", LogLevel.Warn);
                    continue;
                }

                Log($"(GetPotSoilPourers) Soil Pourer found at x: {tiles[i].x}, y: {tiles[i].y}", LogLevel.Debug);
                pourersAroundPot.Add(pourer);
            }
        }

        SoilPourer? potPourer = null;
        if (pourersAroundPot.Count <= 0)
            return potPourer;

        Log($"(GetPotSoilPourers) pourersAroundPot Count: {pourersAroundPot.Count}", LogLevel.Debug);

        for (int i = 0; i < pourersAroundPot.Count; i++)
        {
            Coordinate pourerCoords = new Coordinate(pourersAroundPot[i].OriginCoordinate);
            Log($"(GetPotSoilPourers) Checking if soil pourer at x: {pourerCoords.x}, y: {pourerCoords.y} pot is assigned pot", LogLevel.Debug);
            Coordinate coord1 = new Coordinate(pourersAroundPot[i].OriginCoordinate) + Coordinate.RotateCoordinates(new Coordinate(0, 1), pourersAroundPot[i].Rotation);
            Coordinate coord2 = new Coordinate(pourersAroundPot[i].OriginCoordinate) + Coordinate.RotateCoordinates(new Coordinate(1, 1), pourersAroundPot[i].Rotation);
            Tile tile = pourersAroundPot[i].OwnerGrid.GetTile(coord1);
            Tile tile2 = pourersAroundPot[i].OwnerGrid.GetTile(coord2);

            List<Pot> pots = new List<Pot>();
            if (tile != null && tile2 != null)
            {
                Pot tilePot = null;
                foreach (GridItem item in tile.BuildableOccupants)
                {
                    if (item is Pot)
                    {
                        tilePot = item as Pot;
                        break;
                    }
                }

                if (tilePot != null && tile2.BuildableOccupants.Contains(tilePot))
                {
                    pots.Add(tilePot);
                }
            }

            if (pots.Count <= 0)
            {
                Log("(GetPotSoilPourers) Tile pots count is 0 or less", LogLevel.Debug);
                return potPourer;
            }

            Log($"(GetPotSoilPourers) Soil pourer pots count: {pots.Count}", LogLevel.Debug);

            for (int p = 0; p < pots.Count; p++)
            {
                Coordinate tilePotCoords = new Coordinate(pots[p].OriginCoordinate);
                Log($"(GetPotSoilPourers) Checking soil pourer pot at x: {tilePotCoords.x}, y: {tilePotCoords.y} matches assigned pot at x: {assignedPotCoords.x}, y {assignedPotCoords.y}", LogLevel.Debug);
                if (tilePotCoords.x == assignedPotCoords.x && tilePotCoords.y == assignedPotCoords.y)
                {
                    Log("(GetPotSoilPourers) Adding Soil pourer", LogLevel.Debug);
                    potPourer = pourersAroundPot[i];
                    break;
                }
            }
        }

        Log($"(GetPotSoilPourers) Soil pourer to be activated is null: {potPourer is null}", LogLevel.Debug);
        return potPourer;
    }

    private static bool TryGetSoilPourer(Tile tile, out SoilPourer? pourer)
    {
        pourer = null;
        foreach (GridItem item in tile.BuildableOccupants)
        {
            if (item is null)
                continue;

            if (item is SoilPourer)
            {
                pourer = (SoilPourer)item;
                break;
            }
        }

        return pourer;
    }

    private static void LoadConfigFromFile()
    {
        if (!Directory.Exists(ConfigDirectoryPath))
            Directory.CreateDirectory(ConfigDirectoryPath);

        if (!File.Exists(ConfigPath))
        {
            CreateNewConfigFile();
            return;
        }

        Configuration baseConfiguration = new Configuration
        {
            enabled = true,
            debug = false
        };

        string text = File.ReadAllText(ConfigPath);
        var json = JsonConvert.DeserializeObject<Configuration>(text);
        if (json is null)
        {
            _config = baseConfiguration;
            return;
        }

        baseConfiguration.enabled = json.enabled;
        baseConfiguration.debug = json.debug;

        _config = baseConfiguration;
    }

    private static void CreateNewConfigFile()
    {
        Configuration baseConfiguration = new Configuration
        {
            enabled = true,
            debug = false
        };

        var json = JsonConvert.SerializeObject(baseConfiguration, formatting: Formatting.Indented);
        File.WriteAllText(ConfigPath, json);
        _config = baseConfiguration;
    }

    private static void Log(string message, LogLevel level)
    {
        string prefix = GetLogPrefix(level);
        if (level is LogLevel.Debug)
        {
            if (!_config.debug)
                return;
        }

        MelonLogger.Msg($"{prefix} {message}");
    }

    private static string GetLogPrefix(LogLevel level)
    {
        switch (level)
        {
            case LogLevel.Debug:
                return "[DEBUG]";
            case LogLevel.Info:
                return "[INFO]";
            case LogLevel.Warn:
                return "[WARN]";
            case LogLevel.Error:
                return "[ERROR]";
            case LogLevel.Fatal:
                return "[FATAL]";
            default:
                return "[MISC]";
        }
    }

    private enum LogLevel
    {
        Debug,
        Info,
        Warn,
        Error,
        Fatal
    }
}

internal class Configuration
{
    [JsonProperty("enabled")]
    public bool enabled { get; set; }

    [JsonProperty("debug")]
    public bool debug { get; set; }
}
