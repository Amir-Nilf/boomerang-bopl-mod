using BepInEx;
using BepInEx.Logging;
using HarmonyLib;

namespace Boomerang
{
    // Depends on BepInEx only. The ability is built by cloning one the game already has.
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.maha.boplbattle.boomerang";
        public const string PluginName = "Boomerang";
        public const string PluginVersion = "1.0.0";

        internal static ManualLogSource Log;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo("Boomerang: loading...");
            new Harmony(PluginGuid).PatchAll();
            Log.LogInfo($"Boomerang: loaded (spin {BoomerangAbility.SpinTicks} ticks, cooldown "
                        + $"{BoomerangAbility.CooldownSeconds}s, return speed "
                        + $"{(float)BoomerangAbility.ReturnSpeed:0.#}, gives up after "
                        + $"{BoomerangAbility.MaxReturnTicks} ticks).");
        }
    }
}
