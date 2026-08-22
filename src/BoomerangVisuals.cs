using BoplFixedMath;
using UnityEngine;

namespace Boomerang
{
    // Boomerang artwork, and the spin it does in flight.
    //
    // Presentation only. The spin angle never feeds back into gameplay, so the floats here
    // cannot reach the lockstep simulation.
    public static class BoomerangVisuals
    {
        private static Sprite boomerang;
        private static bool loadAttempted;

        public static Sprite BoomerangSprite()
        {
            EnsureLoaded();
            return boomerang;
        }

        public static void Draw(GameObject visual, SpriteRenderer renderer, Vec2 position,
                                ref float spinAngle)
        {
            if (visual == null)
            {
                return;
            }
            visual.transform.position = (Vector2)position;
            spinAngle += BoomerangAbility.SpinDegreesPerTick;
            visual.transform.rotation = Quaternion.Euler(0f, 0f, spinAngle);
        }

        // Flashes a card that is about to give up, so it visibly runs out of time rather
        // than disappearing between one frame and the next. A card that is caught never gets
        // here, because it is removed the moment it reaches its owner.
        public static void Blink(SpriteRenderer renderer, int ticksLeft)
        {
            if (renderer == null)
            {
                return;
            }
            if (ticksLeft < 0 || ticksLeft > BoomerangAbility.BlinkTicks)
            {
                renderer.enabled = true;
                return;
            }
            renderer.enabled = (ticksLeft / BoomerangAbility.BlinkPeriodTicks) % 2 == 0;
        }

        private static void EnsureLoaded()
        {
            if (loadAttempted)
            {
                return;
            }
            loadAttempted = true;

            boomerang = BoomerangAbility.LoadBoomerang();
            if (boomerang == null)
            {
                Plugin.Log.LogError("Boomerang: the art failed to load, so a thrown boomerang will "
                                    + "be invisible. It still cuts and still returns.");
                return;
            }
            Plugin.Log.LogInfo("Boomerang: art ready.");
        }
    }
}
