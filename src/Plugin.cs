using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using UnityEngine;

namespace ResourceScanner
{
    public enum ValueFormat { Percent, Numbers }

    // Shows the health of the tree, log, stump, rock, ore deposit or nest under the crosshair,
    // in a small box at the bottom of the screen. Health is read from the object's synced data,
    // so it works whoever owns the object (unlike mods that listen to the damage RPC).
    [BepInPlugin(GUID, NAME, VERSION)]
    public class Plugin : BaseUnityPlugin
    {
        public const string GUID = "felih.ResourceScanner";
        public const string NAME = "Resource Scanner";
        public const string VERSION = "0.5.0";

        public static ManualLogSource Log;
        public static ConfigEntry<bool> Enabled;
        public static ConfigEntry<bool> RequireTool;
        public static ConfigEntry<ValueFormat> Format;
        public static ConfigEntry<float> Reach;
        public static ConfigEntry<float> HideDelay;
        public static ConfigEntry<float> PosX;
        public static ConfigEntry<float> PosY;
        public static ConfigEntry<float> Scale;
        public static ConfigEntry<bool> XRay;
        public static ConfigEntry<KeyCode> XRayKey;
        public static ConfigEntry<float> XRayCloseAfter;
        public static ConfigEntry<float> XRaySize;
        public static ConfigEntry<bool> XRayGroundPlane;
        public static ConfigEntry<float> XRayOffsetX;
        public static ConfigEntry<float> XRayOffsetY;
        public static ConfigEntry<bool> DetailedLog;

        private void Awake()
        {
            Log = Logger;
            Enabled = Config.Bind("1 - General", "Enabled", true, "Turns the mod on or off.");
            RequireTool = Config.Bind("1 - General", "RequireTool", true,
                "Only show the box while holding the right tool: an axe for trees, logs and stumps, a pickaxe for rocks and ores. Nests show with anything in hand.");
            Reach = Config.Bind("1 - General", "Reach", 4f,
                new ConfigDescription("How far past your character (meters) an object can be.", new AcceptableValueRange<float>(1f, 15f)));
            HideDelay = Config.Bind("1 - General", "HideDelay", 1f,
                new ConfigDescription("Seconds the box stays after you stop aiming.", new AcceptableValueRange<float>(0f, 5f)));

            Format = Config.Bind("2 - Display", "Format", ValueFormat.Percent, "Percent = 62%. Numbers = 1240 / 2000.");
            PosX = Config.Bind("2 - Display", "OffsetX", 0f, "Horizontal offset from the center of the screen.");
            PosY = Config.Bind("2 - Display", "OffsetY", 0f, "Vertical offset from the bottom of the screen (positive = up).");
            Scale = Config.Bind("2 - Display", "Scale", 1f,
                new ConfigDescription("Size of the box.", new AcceptableValueRange<float>(0.5f, 3f)));
            XRay = Config.Bind("3 - X-ray", "Enabled", true,
                "Press XRayKey with a pickaxe while aiming at an ore deposit or a big rock to open a small window under the minimap " +
                "with a live 3D view of the whole deposit without the ground, buried pieces included. The piece you aim at is highlighted.");
            XRayKey = Config.Bind("3 - X-ray", "XRayKey", KeyCode.Mouse1, "Key or mouse button that opens the window. Mouse1 = right mouse button.");
            XRayCloseAfter = Config.Bind("3 - X-ray", "CloseAfter", 3f,
                new ConfigDescription("The window closes after this many seconds without aiming at the deposit (or when you put the pickaxe away).",
                    new AcceptableValueRange<float>(0.5f, 30f)));
            XRaySize = Config.Bind("3 - X-ray", "WindowSize", 220f,
                new ConfigDescription("Size of the 3D view (applies after restarting the game).", new AcceptableValueRange<float>(100f, 600f)));
            XRayGroundPlane = Config.Bind("3 - X-ray", "GroundPlane", true, "Draw a see-through plane at ground level, so you can tell what's buried.");
            XRayOffsetX = Config.Bind("3 - X-ray", "WindowOffsetX", 0f, "Moves the window sideways (positive = right).");
            XRayOffsetY = Config.Bind("3 - X-ray", "WindowOffsetY", 0f, "Moves the window up or down (positive = up).");

            DetailedLog = Config.Bind("9 - Debug", "DetailedLog", false,
                "Writes what the box is showing and how it was built to the BepInEx log.");

            GameObject go = new GameObject("ResourceScanner_Manager");
            Object.DontDestroyOnLoad(go);
            go.AddComponent<HealthHud>();
            go.AddComponent<XRay>();
            Log.LogInfo(NAME + " " + VERSION + " loaded.");
        }

        public static void Debug(string msg)
        {
            if (DetailedLog.Value) Log.LogInfo(msg);
        }
    }
}
