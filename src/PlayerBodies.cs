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
        public static int KillWhere(int victimId, int killerId, CauseOfDeath causeOfDeath,
                                    Func<Vec2, bool> isInside, out int bodiesSeen)
        {
            bodiesSeen = 0;
            int killed = 0;

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
                if (collision.killPlayer(killerId, spawnEffect: true, ignoreInvulnerability: false,
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
                                  out int bodiesSeen)
        {
            return KillWhere(victimId, killerId, causeOfDeath, position => true, out bodiesSeen);
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
