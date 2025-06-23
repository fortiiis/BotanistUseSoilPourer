using BotanistUseSoilPourer;
using HarmonyLib;
using Il2CppFishNet;
using Il2CppScheduleOne.DevUtilities;
using Il2CppScheduleOne.EntityFramework;
using Il2CppScheduleOne.ItemFramework;
using Il2CppScheduleOne.NPCs.Behaviour;
using Il2CppScheduleOne.ObjectScripts.Soil;
using Il2CppScheduleOne.ObjectScripts;
using Il2CppScheduleOne.Tiles;
using Il2CppScheduleOne.Trash;
using MelonLoader;
using System.Text.Json;
using System.Text.Json.Serialization;
using UnityEngine;
using Il2CppScheduleOne;

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

    private static Dictionary<Pot, SoilPourer> CheckPreventer = new Dictionary<Pot, SoilPourer>();

    public override void OnDeinitializeMelon()
    {
        Log("(OnDeinitializeMelon) Mod unloaded", LogLevel.Info);
    }

    [HarmonyPatch(typeof(PotActionBehaviour), nameof(PotActionBehaviour.StartAction))]
    public static class PotActionBehaviourStartActionPatch
    {
        [HarmonyPostfix]
        private static void Postfix(PotActionBehaviour __instance)
        {
            if (__instance.AssignedPot is null)
                return;
            if (__instance.CurrentActionType is not PotActionBehaviour.EActionType.PourSoil)
                return;
            // Prevent checking for soil pourers again after finishing one
            if (__instance.IsAtPot())
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
            if (__instance.CurrentActionType is not PotActionBehaviour.EActionType.PourSoil)
                return;

            SoilPourer? pourer = null;
            if (CheckPreventer.ContainsKey(__instance.AssignedPot))
                pourer = CheckPreventer[__instance.AssignedPot];
            if (pourer is null)
                return;

            if (pourer.SoilID == string.Empty)
            {
                if (!__instance.IsAtPot())
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

            if (!__instance.IsAtPot())
                return;

            Log("(PotActionBehaviourActiveMinPassPatch/Postfix) Activating soil pourer", LogLevel.Debug);
            pourer.ActivateSound.Play();
            DispenseSoilQuick(pourer.SoilID, __instance.AssignedPot);
            pourer.SendSoil(string.Empty);
            pourer.SetSoilLevel(0f);
            EndBotanistTask(__instance);
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
        if (CheckPreventer.ContainsKey(behaviour.AssignedPot))
        {
            Log("(EndBotanistTask) Removing pot from check preventer", LogLevel.Debug);
            CheckPreventer.Remove(behaviour.AssignedPot);
        }
        behaviour.StopPerformAction();
        behaviour.CompleteAction();
        behaviour.SendEnd();
        behaviour.botanist.SetIdle(true);
    }

    private static void PourSoilIntoPourer(PotActionBehaviour behaviour, SoilPourer pourer)
    {
        if (behaviour.AssignedPot is null)
        {
            Log("(PourSoilIntoPourer) PotActionBehaviour is null", LogLevel.Warn);
            return;
        }

        ItemInstance? soil = null;
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

        soil.Definition.TryCast<SoilDefinition>();

        SoilDefinition? soilDefinition = soil.Definition.TryCast<SoilDefinition>();
        if (soilDefinition is null)
        {
            Log("(PourSoilIntoPourer) SoilDefinition is null", LogLevel.Warn);
            return;
        }

        pourer.SendSoil(soilDefinition.ID);
        Equippable_Soil? equippableSoil = soilDefinition.Equippable.TryCast<Equippable_Soil>();
        if (equippableSoil is not null)
            NetworkSingleton<TrashManager>.Instance.CreateTrashItem(equippableSoil.PourablePrefab.TrashItem.ID, behaviour.transform.position + Vector3.up * 0.5f, UnityEngine.Random.rotation);
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
                    Log($"(GetPotSoilPourers) Tile at index {i} is null, skipping", LogLevel.Debug);
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
                Pot? tilePot = null;
                foreach (GridItem item in tile.BuildableOccupants)
                {
                    if (item.TryGetComponent<Pot>(out Pot itemPot))
                    {
                        tilePot = itemPot;
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

            if (item.TryGetComponent<SoilPourer>(out SoilPourer soilPourer))
            {
                pourer = soilPourer;
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
        var json = JsonSerializer.Deserialize<Configuration>(text);
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

        JsonSerializerOptions options = new JsonSerializerOptions();
        options.WriteIndented = true;

        var json = JsonSerializer.Serialize(baseConfiguration, options);
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
    [JsonPropertyName("enabled")]
    public bool enabled { get; set; }

    [JsonPropertyName("debug")]
    public bool debug { get; set; }
}
