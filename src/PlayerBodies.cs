using System;
using System.Collections.Generic;
using BoplFixedMath;
using UnityEngine;

namespace Boomerang
{
    // Finding the thing to kill.
    //
    // A "player" is not one object, and the one you can see is not always the one that dies:
    //
    //   * A DUPLICATED player has a separate SlimeController and PlayerCollision per clone,
    //     each standing somewhere different, all sharing one player id.
    //   * A player INSIDE AN ABILITY has had their slime switched off
    //     (SlimeController.gameObject.SetActive(false)); the ability object stands in for
    //     them and carries its own PlayerCollision.
    //
    // Two consequences, and both are easy to walk into:
    //
    //   * FindObjectsOfType SKIPS INACTIVE OBJECTS, so searching for SlimeControllers cannot
    //     see anyone who is inside an ability. A projectile aimed at someone mid-Roll passes
    //     straight through them.
    //   * Player.Position is a SINGLE POINT for the whole player. It cannot describe where
    //     several clones are, so testing it and then killing one remembered body kills the
    //     wrong one -- and, because the test stays true next tick, kills again. Player.Kill
    //     only ends a player when their last clone goes, so removing them all at once reads
    //     as the whole player dying and hands out the round.
    //
    // So target BODIES, not players: ask the scene for every live PlayerCollision, test each
    // at ITS OWN position, and kill the ones actually inside the effect. One contact removes
    // one body.
    internal static class PlayerBodies
    {
        private static bool warnedEmpty;

        // Bodies this mod has already killed, by instance id.
        //
        // Killing a body normally destroys it, and Updater.DestroyFix marks it destroyed
        // immediately, so the checks above would be enough -- except when the victim is
        // inside an ability. There, killPlayer hands off to ability.ExitAbility(isDead) and
        // returns true WITHOUT destroying anything. If the ability does not exit that same
        // tick, a multi-tick effect finds the same live PlayerCollision next tick and kills
        // it again, and Player.Kill takes a second life for a single body. Two clones, one
        // double-kill, and the player is out.
        //
        // Keyed by BODY, never by player. Keying it by player would mean nothing could ever
        // be killed twice by anything, which stops kills working entirely. Per body, every
        // clone still dies on its own and only the same object twice is refused.
        private static readonly HashSet<int> alreadyKilled = new HashSet<int>();

        // Called when a round ends. Instance ids are not reused by live objects, so holding
        // them for a whole round is safe; this just stops the set growing forever.
        public static void ForgetAll()
        {
            alreadyKilled.Clear();
        }


        // Kills every body belonging to victimId whose own position is inside the effect.
        // Returns the number actually killed.
        //
        // `bodiesSeen` reports how many bodies were found for that player regardless of
        // position, which is what tells a legitimate miss apart from this scan being blind.
        // A player the game itself has made untouchable.
        //
        // The block ability (PlatformTransform) protects its user by DISABLING THEIR HURTBOX
        // -- it does not make them invulnerable, it removes the thing collisions land on. Our
        // kill tests are positional and call killPlayer directly, so they never meet that
        // hurtbox and would cheerfully kill someone the rest of the game cannot touch.
        //
        // Tested through the game's own public flag rather than by inspecting colliders: it
        // is exact, and it cannot mistake some other disabled component for protection and
        // quietly stop kills working.
        public static bool IsProtected(int playerId)
        {
            PlayerHandler handler = PlayerHandler.Get();
            Player player = handler == null ? null : handler.GetPlayer(playerId);
            if (player != null && player.InPlatformTransform)
            {
                return true;
            }

            // Steering a platform protects them too.
            //
            // This one is a house rule rather than the game's. ControlPlatform leaves the
            // player's hurtbox switched ON, so their face on the platform genuinely is a
            // target and the game's own laser kills them through it. We refuse anyway,
            // because being killed through a platform you are driving reads as a bug to the
            // person it happens to.
            //
            // Found by component rather than by a flag: unlike PlatformTransform, there is
            // no InControlPlatform on Player to ask.
            foreach (ControlPlatform steering in UnityEngine.Object.FindObjectsOfType<ControlPlatform>())
            {
                if (steering == null || !steering.isActiveAndEnabled)
                {
                    continue;
                }
                IPlayerIdHolder holder = steering.GetComponent<IPlayerIdHolder>();
                if (holder != null && holder.GetPlayerId() == playerId)
                {
                    return true;
                }
            }
            return false;
        }

        // respectProtection: true for anything thrown, fired or swung at a player -- those
        // must not reach someone the game has made untouchable. False only for an effect the
        // victim is CARRYING, which is nobody's aim and cannot be hidden from.
        public static int KillWhere(int victimId, int killerId, CauseOfDeath causeOfDeath,
                                    Func<Vec2, bool> isInside, out int bodiesSeen,
                                    bool respectProtection = true)
        {
            bodiesSeen = 0;
            int killed = 0;

            if (respectProtection && IsProtected(victimId))
            {
                return 0;
            }

            foreach (PlayerCollision collision in UnityEngine.Object.FindObjectsOfType<PlayerCollision>())
            {
                if (collision == null || collision.IsDestroyed)
                {
                    continue;
                }
                if (!collision.gameObject.activeInHierarchy)
                {
                    continue;
                }
                if (collision.GetPlayerId() != victimId)
                {
                    continue;
                }

                FixTransform fixTrans = collision.GetComponent<FixTransform>();
                if (fixTrans == null || fixTrans.IsDestroyed)
                {
                    continue;
                }
                bodiesSeen++;

                if (!isInside(fixTrans.position))
                {
                    continue;
                }
                if (alreadyKilled.Contains(collision.GetInstanceID()))
                {
                    // Already killed once by us. See alreadyKilled.
                    continue;
                }
                if (collision.killPlayer(killerId, spawnEffect: true,
                                         ignoreInvulnerability: !respectProtection,
                                         causeOfDeath))
                {
                    alreadyKilled.Add(collision.GetInstanceID());
                    killed++;
                }
            }

            return killed;
        }

        // Every live body of a player, wherever it is. For effects that are not positional --
        // a fuse running out on whoever is carrying the bomb kills them wherever they are.
        public static int KillAll(int victimId, int killerId, CauseOfDeath causeOfDeath,
                                  out int bodiesSeen, bool respectProtection = true)
        {
            return KillWhere(victimId, killerId, causeOfDeath, position => true, out bodiesSeen,
                             respectProtection);
        }

        // Said once per session, not once per tick: if this scan ever finds nothing, the
        // assumption it rests on has changed and every kill in this mod is silently doing
        // nothing. That is worth shouting about, because it looks exactly like the ability
        // being broken.
        public static void WarnIfBlind(int bodiesSeen, string what)
        {
            if (bodiesSeen > 0 || warnedEmpty)
            {
                return;
            }
            warnedEmpty = true;
            Plugin.Log.LogWarning(
                $"{what}: no live PlayerCollision was found for the target, so nothing can be "
                + "killed. Every kill in this mod goes through that lookup, so this is not a "
                + "near miss -- the scan itself is blind and the ability will appear to do "
                + "nothing. Falling back to the old single-body lookup for this hit.");
        }
    }
}
