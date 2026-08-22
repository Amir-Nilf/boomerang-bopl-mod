using BoplFixedMath;
using HarmonyLib;
using UnityEngine;
using UnityEngine.Events;

namespace Boomerang
{
    // Registers the Boomerang by cloning a native instant ability and stripping its effect.
    [HarmonyPatch(typeof(AbilityGrid), "Awake")]
    public static class CardInjectionPatch
    {
        private static bool hasInjected;

        public static void Prefix(AbilityGrid __instance)
        {
            if (hasInjected)
            {
                return;
            }

            NamedSpriteList icons = __instance.abilityIcons;
            if (icons == null || icons.sprites == null)
            {
                Plugin.Log.LogWarning("Boomerang: AbilityGrid.abilityIcons was empty this Awake; "
                                      + "not injecting yet (will retry on the next grid build).");
                return;
            }

            NamedSprite template = default(NamedSprite);
            bool found = false;
            foreach (NamedSprite sprite in icons.sprites)
            {
                if (sprite.name != null
                    && sprite.name.ToLower().Contains(BoomerangAbility.TemplateNameFragment))
                {
                    template = sprite;
                    found = true;
                    break;
                }
            }

            if (!found || template.associatedGameObject == null)
            {
                Plugin.Log.LogError("Boomerang: could not find a native instant ability to clone, so the "
                                    + "cards were not added. (Looked for an ability whose name contains "
                                    + $"'{BoomerangAbility.TemplateNameFragment}'.)");
                return;
            }

            GameObject clone = Object.Instantiate(template.associatedGameObject);
            Object.DontDestroyOnLoad(clone);
            clone.name = BoomerangAbility.AbilityName;

            Ability ability = clone.GetComponent<Ability>();
            if (ability == null)
            {
                Plugin.Log.LogError($"Boomerang: '{template.name}' has no Ability component, so its "
                                    + "cooldown could not be set; it keeps the grenade's.");
            }
            else
            {
                ability.Cooldown = (Fix)BoomerangAbility.CooldownSeconds;
            }

            SwapInHandSprite(clone);

            Sprite icon = BoomerangAbility.LoadIcon("AbilityIconBoomerang", template.sprite) ?? template.sprite;
            icons.sprites.Add(new NamedSprite(BoomerangAbility.AbilityName, icon, clone, true));
            hasInjected = true;
            Plugin.Log.LogInfo($"Boomerang: ability injected into the select grid (cloned "
                               + $"'{template.name}', cooldown {BoomerangAbility.CooldownSeconds}s).");
        }

        // ThrowItem2 shows a separate "dummy" object in your hands while you aim and only
        // swaps to the real body on release, so without this you visibly hold a grenade for
        // the whole wind-up. Swapped on the template: ThrowItem2.Awake resolves the dummy as
        // transform.GetChild(1), so the same child is addressed directly and every instance
        // cloned from this template inherits it.
        private static void SwapInHandSprite(GameObject clone)
        {
            if (clone.transform.childCount <= 1)
            {
                Plugin.Log.LogInfo("Boomerang: the clone has no in-hand dummy, so it may still look "
                                   + "like a grenade while you aim.");
                return;
            }
            SpriteRenderer dummy = clone.transform.GetChild(1).GetComponentInChildren<SpriteRenderer>(true);
            if (dummy == null)
            {
                Plugin.Log.LogInfo("Boomerang: no sprite on the in-hand dummy, so it may still look "
                                   + "like a grenade while you aim.");
                return;
            }
            Sprite sprite = BoomerangVisuals.BoomerangSprite();
            if (sprite != null)
            {
                dummy.sprite = sprite;
            }
        }
    }

    // The in-hand item stops looking like a grenade.
    //
    // Swapping the template's "dummy" child is not enough on its own, and this is why the
    // thing you were holding turned into a grenade about a second in. ThrowItem2 shows the
    // dummy only for the wind-up; when that animation ends it calls SpawnGrenade, which
    // instantiates the REAL grenade body and hides the dummy. That body is a fresh object
    // from the grenade prefab, so it carries the grenade's own sprite.
    [HarmonyPatch(typeof(ThrowItem2), "SpawnGrenade")]
    public static class BoomerangAppearancePatch
    {
        public static void Postfix(ThrowItem2 __instance)
        {
            if (!BoomerangAbility.IsBoomerang(__instance.gameObject))
            {
                return;
            }
            BoplBody spawned = Traverse.Create(__instance).Field("grenadeBody").GetValue<BoplBody>();
            if (spawned == null)
            {
                return;
            }
            // Disarmed, not just re-skinned.
            //
            // This body is a real grenade, and ThrowItem2.ExitAbility DROPS it live if the
            // ability is left without firing -- a cancelled throw, or a grounded throw where
            // the ground goes away. That is why holding too long, or letting go oddly,
            // occasionally produced an actual exploding grenade. Removing the explosion and
            // cancelling the fuse makes the object inert whatever happens to it.
            GrenadeExplode explode = spawned.GetComponent<GrenadeExplode>();
            if (explode != null)
            {
                Object.Destroy(explode);
            }
            // Grenade itself stays: ThrowItem2.SpawnGrenade and Fire both talk to it, and
            // removing it would break the throw. Only its timer is disarmed.
            Grenade grenade = spawned.GetComponent<Grenade>();
            if (grenade != null)
            {
                grenade.timedExplosion = false;
                grenade.selfDestructDelay = -Fix.One;
            }

            SpriteRenderer renderer = spawned.GetComponentInChildren<SpriteRenderer>();
            if (renderer == null)
            {
                Plugin.Log.LogWarning("Boomerang: the in-hand body has no SpriteRenderer, so it still "
                                      + "looks like a grenade while you aim.");
                return;
            }
            Sprite sprite = BoomerangVisuals.BoomerangSprite();
            if (sprite != null)
            {
                renderer.sprite = sprite;
            }
        }
    }

    // Nothing is ever left behind when the throw does not happen.
    //
    // ThrowItem2.ExitAbility DROPS the in-hand body if the ability is left without firing,
    // and it is left without firing more often than you would think: jump, hold to aim,
    // land, and the grounded-throw path bails out. The result was a loose object lying on
    // the platform behaving like a piece of scenery. Disarming it stopped it exploding but
    // it still had to stop existing.
    //
    // A Prefix, so it runs BEFORE the native drop code -- and it clears ThrowItem2's own
    // `grenade` reference, which is the flag that code checks, so the drop is skipped
    // entirely rather than being undone afterwards.
    [HarmonyPatch(typeof(ThrowItem2), "ExitAbility", new[] { typeof(AbilityExitInfo) })]
    public static class BoomerangNoDropPatch
    {
        public static void Prefix(ThrowItem2 __instance)
        {
            if (!BoomerangAbility.IsBoomerang(__instance.gameObject))
            {
                return;
            }
            Traverse traverse = Traverse.Create(__instance);
            if (traverse.Field("hasFired").GetValue<bool>())
            {
                return;
            }
            BoplBody spawned = traverse.Field("grenadeBody").GetValue<BoplBody>();
            if (spawned != null && !spawned.IsDestroyed)
            {
                Updater.DestroyFix(spawned.gameObject);
                Plugin.Log.LogInfo("Boomerang: the throw was cancelled, so the item was removed "
                                   + "rather than dropped.");
            }
            traverse.Field("grenade").SetValue(null);
            traverse.Field("grenadeBody").SetValue(null);
        }
    }

    // The throw, at the moment the button comes up.
    //
    // Hooked on ThrowItem2.Fire rather than on entering the ability, because entering is
    // when you START aiming. Everything that makes aiming feel right -- the slow-motion
    // hang, the aim indicator, the wind-up animation, releasing on the button coming up --
    // is the grenade's own code, running untouched.
    //
    // The grenade body it spawns is destroyed immediately. Only two things are taken from
    // it: the direction it was aimed, and how long the button was held.
    [HarmonyPatch(typeof(ThrowItem2), "Fire")]
    public static class BoomerangReleasePatch
    {
        public static void Postfix(ThrowItem2 __instance)
        {
            if (!BoomerangAbility.IsBoomerang(__instance.gameObject))
            {
                return;
            }

            Ability ability = __instance.GetComponent<Ability>();
            if (ability == null)
            {
                Plugin.Log.LogWarning("Boomerang: the thrower has no Ability component, so no boomerang "
                                      + "was thrown.");
                return;
            }
            int playerId = ability.GetPlayerId();
            int slot = ability.GetPlayerInfo().AbilityButtonUsedIndex012;

            PlayerHandler handler = PlayerHandler.Get();
            Player player = handler == null ? null : handler.GetPlayer(playerId);
            if (player == null)
            {
                return;
            }

            Traverse traverse = Traverse.Create(__instance);

            // Taken from ThrowItem2's own aim vector rather than from the body's velocity.
            // Fire launches with AddForce(..., Impulse), and the deterministic physics engine
            // does not apply that until its next step, so reading velocity here returns zero
            // every time. Sheep Bomb learned this the hard way.
            Vec2 aim = traverse.Field("dir").GetValue<Vec2>();

            // How long the button was held, which the grenade already measured for its own
            // throw-strength curve. Reusing it means the aim indicator's size and the
            // distance actually thrown can never disagree.
            Fix held = traverse.Field("FireInputTimeStamp").GetValue<Fix>();

            BoomerangState.Throw(playerId, slot, player.Position, aim,
                                 BoomerangAbility.ThrowDistanceFor(held),
                                 BoomerangAbility.OutSpeedFor(held));

            // The grenade body is not wanted at all -- the boomerang is flown by hand.
            BoplBody spawned = traverse.Field("grenadeBody").GetValue<BoplBody>();
            if (spawned != null && !spawned.IsDestroyed)
            {
                Updater.DestroyFix(spawned.gameObject);
            }
        }
    }

    // Gusts and explosions push the boomerang.
    //
    // Shockwave is what the gust ability and every blast in the game use to shove things
    // around. It only ever moves objects with a BoplBody, and the boomerang deliberately
    // has none, so without this it would sail through a gust completely unmoved.
    //
    // Hooked here rather than sampled per tick because a shockwave is a single event: it
    // fires, it pushes, it is done. Sampling would re-apply it every frame it existed.
    [HarmonyPatch(typeof(Shockwave), "ActivateShockWave")]
    public static class BoomerangShockwavePatch
    {
        public static void Postfix(Shockwave __instance)
        {
            FixTransform fixTrans = __instance.GetComponent<FixTransform>();
            if (fixTrans == null || fixTrans.IsDestroyed)
            {
                return;
            }
            Fix scale = Traverse.Create(__instance).Field("scale").GetValue<Fix>();
            if (scale <= Fix.Zero)
            {
                scale = Fix.One;
            }
            BoomerangState.Shove(fixTrans.position, __instance.radius * scale,
                                 __instance.defaultForce);
        }
    }

    // Flies every boomerang in the air.
    [HarmonyPatch(typeof(Updater), nameof(Updater.TickSimulation))]
    public static class CardTickPatch
    {
        public static void Postfix()
        {
            BoomerangState.Tick();
            BoomerangState.PinCooldownsWhileCardsOut();
        }
    }

    [HarmonyPatch(typeof(PlayerHandler), "ResetForNextStage")]
    public static class CardResetPatch
    {
        public static void Prefix()
        {
            // Instance ids of bodies we have killed are held for a round; drop them.
            PlayerBodies.ForgetAll();
            BoomerangState.ClearAll();
        }
    }

    // Also cleared on every level load.
    //
    // ResetForNextStage alone was not enough -- state banked at the end of a round came back
    // with the player on the next one. PreLevelLoad fires for every stage, so it is the
    // dependable place to forget anything still in the air.
    [HarmonyPatch(typeof(Updater), nameof(Updater.PreLevelLoad))]
    public static class CardLevelLoadPatch
    {
        public static void Prefix()
        {
            BoomerangState.ClearAll();
        }
    }
}
