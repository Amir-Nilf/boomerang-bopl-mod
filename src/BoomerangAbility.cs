using System.IO;
using System.Reflection;
using BoplFixedMath;
using UnityEngine;

namespace Boomerang
{
    // Every tunable number the boomerang has, plus loading its art.
    //
    // Gameplay values are BoplFixedMath.Fix, not float. Bopl runs a deterministic lockstep
    // simulation -- both machines in an online game compute the same frame from the same
    // inputs -- and floating point is not reliably identical across machines. Anything that
    // affects where something is or who dies must be Fix; only visuals may use float.
    public static class BoomerangAbility
    {
        // Instances the game spawns from the template pick up Unity's "(Clone)" suffix, so
        // identity is always tested with Contains rather than equality.
        public const string AbilityName = "boomerang";


        // The native ability this one is built from, matched by a substring of its name.
        //
        // The grenade is the template because it already does the hard part: holding the
        // button slows the thrower to a hang, draws the aim indicator, plays the wind-up
        // animation and fires on release. All of that is inherited for free. The grenade
        // body it spawns is discarded -- only the aim direction and the hold time are kept.
        public const string TemplateNameFragment = "grenade";

        public const float CooldownSeconds = 2.45f;

        // How far a throw travels before the boomerang stops and hangs. A wall stops it
        // sooner. This is the distance at HALF strength; the scales below stretch it.
        public static readonly Fix ThrowDistance = (Fix)14.5f;

        // What the orange strength meter multiplies that distance by, from a tap to a full
        // hold. Changing ThrowDistance alone rescales every throw, light and heavy together.
        public static readonly Fix MinThrowDistanceScale = (Fix)0.25;
        public static readonly Fix MaxThrowDistanceScale = (Fix)1.5;

        // Seconds of holding to reach full strength. Matches the grenade's own
        // ThrowForceGainSpeed of 1, so the meter and the distance fill together.
        public static readonly Fix FullHoldSeconds = (Fix)1L;

        public static Fix ThrowDistanceFor(Fix heldSeconds)
        {
            Fix t = Fix.Clamp01(heldSeconds / FullHoldSeconds);
            return ThrowDistance * Fix.Lerp(MinThrowDistanceScale, MaxThrowDistanceScale, t);
        }

        // Outbound speed, also scaled by the strength meter.
        //
        // Distance and speed both scale, deliberately. If only the distance scaled, a hard
        // throw would cover more ground at the same speed and therefore take LONGER to
        // arrive -- the opposite of what throwing something hard should feel like.
        public static readonly Fix MinOutSpeed = (Fix)18L;
        public static readonly Fix MaxOutSpeed = (Fix)46L;

        public static Fix OutSpeedFor(Fix heldSeconds)
        {
            Fix t = Fix.Clamp01(heldSeconds / FullHoldSeconds);
            return Fix.Lerp(MinOutSpeed, MaxOutSpeed, t);
        }


        // How fast it comes home. One flat speed, whatever the distance.
        //
        // This MUST stay above a player's own top speed of 19 (PlayerPhysics.maxSpeed), or
        // the boomerang can be outrun and will chase its owner until it times out.
        public static readonly Fix ReturnSpeed = (Fix)60L;

        // Whole ticks at the game's fixed 1/60 timestep: 1.5s hanging and spinning.
        public const int SpinTicks = 90;

        // How long it chases its thrower before giving up and vanishing, in ticks. Needed
        // because the thrower can move: without a limit it follows them around the map
        // indefinitely. 180 ticks = 3 seconds.
        public const int MaxReturnTicks = 180;

        // How close the owner must be to catch it, and how close anyone else must be to be
        // cut by it. Both match the sprite's own radius so that what you see is what it
        // touches; both are multiplied by the thrower's scale at the moment of the throw.
        public static readonly Fix CatchRadius = (Fix)2L;
        public static readonly Fix HitRadius = (Fix)2L;

        // Spin rate of the sprite, degrees per tick. Presentation only.
        public const float SpinDegreesPerTick = 13f;

        // The ability icon is drawn smaller than the ability it sits beside. Dividing by
        // this raises pixels-per-unit, which shrinks the sprite: world size is
        // width / pixelsPerUnit.
        public const float IconScale = 0.78f;

        // How wide the boomerang is drawn, in world units. A player is about 2.2 across, so
        // this is roughly two players wide.
        public const float BoomerangWorldWidth = 4.2f;

        // One about to time out blinks for this long first, so it visibly runs out of steam
        // instead of disappearing between one frame and the next.
        public const int BlinkTicks = 40;

        // Ticks per on/off step of that blink.
        public const int BlinkPeriodTicks = 5;

        // ------------------------------------------------------------------ physics
        //
        // The boomerang flies on its own hand-written path rather than as a physics body,
        // which is what keeps its flight exact under lockstep. That also means the game's
        // own force appliers cannot see it at all -- they look for a BoplBody, and it has
        // none. So instead of being pushed, it SAMPLES the forces around it each tick and
        // carries the result as a drift on top of its own motion.
        //
        // Two things reach it:
        //   * Black holes, sampled every tick while one is in range.
        //   * Gusts and blasts, which arrive as a single shove when a Shockwave goes off.

        // Multiplies the black hole pull. The SHAPE of the pull is the game's own formula
        // (acceleration = G * mass / distance squared); this only decides how strongly a
        // boomerang feels it compared to everything else in the game.
        public static readonly Fix BlackHolePull = (Fix)0.6;

        // A boomerang that reaches a black hole's actual event horizon is eaten, rather
        // than orbiting inside it forever. Multiplies the hole's own collider radius.
        public static readonly Fix BlackHoleSwallowRadius = (Fix)1L;

        // Used only if a black hole's private mass cannot be read.
        public static readonly Fix FallbackBlackHoleMass = (Fix)1L;

        // Multiplies the shove from a gust or an explosion.
        public static readonly Fix ShockwavePush = (Fix)0.55;

        // Drift bleeds away at this rate per tick, so a boomerang knocked off course
        // recovers and still finds its way home rather than being permanently deflected.
        public static readonly Fix DriftDecayPerTick = (Fix)0.97;

        // However hard it is pushed, it never drifts faster than this.
        public static readonly Fix MaxDriftSpeed = (Fix)30L;

        public static bool IsBoomerang(GameObject go)
        {
            return go != null && go.name.ToLower().Contains(AbilityName);
        }

        public static Sprite LoadBoomerang()
        {
            Texture2D texture = LoadTexture("Boomerang.Boomerang.png");
            return texture == null
                ? null
                : Build(texture, texture.width / BoomerangWorldWidth);
        }

        // Sized against the ability it sits beside; Sprite.Create defaults to 100 pixels
        // per unit, which renders custom art at the wrong size.
        public static Sprite LoadIcon(string name, Sprite reference)
        {
            Texture2D texture = LoadTexture($"Boomerang.{name}.png");
            if (texture == null)
            {
                return null;
            }
            float pixelsPerUnit = 100f;
            if (reference != null && reference.rect.width > 0f)
            {
                pixelsPerUnit = reference.pixelsPerUnit
                                * (texture.width / reference.rect.width) / IconScale;
            }
            return Build(texture, pixelsPerUnit);
        }

        private static Sprite Build(Texture2D texture, float pixelsPerUnit)
        {
            return Sprite.Create(
                texture,
                new Rect(0f, 0f, texture.width, texture.height),
                new Vector2(0.5f, 0.5f),
                pixelsPerUnit);
        }

        private static Texture2D LoadTexture(string resourceName)
        {
            using (Stream stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(resourceName))
            {
                if (stream == null)
                {
                    Plugin.Log.LogError($"Boomerang: embedded resource '{resourceName}' not found.");
                    return null;
                }
                byte[] data;
                using (MemoryStream buffer = new MemoryStream())
                {
                    stream.CopyTo(buffer);
                    data = buffer.ToArray();
                }
                Texture2D texture = new Texture2D(1, 1);
                if (!texture.LoadImage(data))
                {
                    Plugin.Log.LogError($"Boomerang: '{resourceName}' failed to decode as a PNG.");
                    return null;
                }
                return texture;
            }
        }
    }
}
