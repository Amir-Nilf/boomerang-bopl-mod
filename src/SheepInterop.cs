using System;
using System.Reflection;
using UnityEngine;

namespace Boomerang
{
    // Kills another mod's sheep properly, instead of blowing it up.
    //
    // The Sheep Bomb's sheep is built on a grenade body, so it carries a GrenadeExplode --
    // which meant the boomerang's "set off anything explosive nearby" pass detonated it.
    // That is wrong twice over: a boomerang is a cutting weapon, not a blast, and the sheep
    // has its own death for exactly this (a red splat), which detonating skips entirely.
    //
    // Done by reflection rather than by referencing that mod, deliberately: the Boomerang
    // must keep working on its own, with no hard dependency, no load-order requirement and
    // no breakage if Sheep Bomb is absent, renamed or updated. The contract is loose on
    // purpose -- any component named "Sheep" exposing a public KillBySplat(string) is
    // killable. Freeze Ray reaches the same mod the same way.
    internal static class SheepInterop
    {
        private const string TypeName = "Sheep";
        private const string MethodName = "KillBySplat";

        private static MethodInfo cachedKill;
        private static Type cachedType;
        private static bool searched;

        // True if this object is a sheep, whether or not it could be killed. Callers use it
        // to skip their explosion pass, so a sheep is never blown up as a fallback.
        public static bool IsSheep(Component component)
        {
            return component != null && FindSheep(component.gameObject) != null;
        }

        public static bool TryKill(Component component)
        {
            if (component == null)
            {
                return false;
            }
            Component sheep = FindSheep(component.gameObject);
            if (sheep == null)
            {
                return false;
            }
            MethodInfo kill = ResolveKill(sheep.GetType());
            if (kill == null)
            {
                Plugin.Log.LogInfo($"Boomerang: '{component.gameObject.name}' has a {TypeName} "
                                   + $"component but no usable {MethodName}(string), so it was left "
                                   + "alone rather than blown up.");
                return true;
            }
            try
            {
                kill.Invoke(sheep, new object[] { "cut down by a boomerang" });
                Plugin.Log.LogInfo("Boomerang: cut down a sheep.");
            }
            catch (Exception ex)
            {
                // Another mod's code ran here. Log it and carry on rather than letting the
                // exception escape into the simulation loop.
                Plugin.Log.LogWarning($"Boomerang: killing a sheep threw, so it was left alone: "
                                      + ex.Message);
            }
            return true;
        }

        private static Component FindSheep(GameObject go)
        {
            foreach (Component component in go.GetComponents<Component>())
            {
                // Null components are possible when a script is missing.
                if (component != null && component.GetType().Name == TypeName)
                {
                    return component;
                }
            }
            return null;
        }

        private static MethodInfo ResolveKill(Type type)
        {
            if (searched && cachedType == type)
            {
                return cachedKill;
            }
            searched = true;
            cachedType = type;
            cachedKill = type.GetMethod(
                MethodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { typeof(string) },
                null);
            return cachedKill;
        }
    }
}
