using System;
using System.Collections.Generic;
using BoplFixedMath;
using HarmonyLib;
using UnityEngine;

namespace Boomerang
{
    // Every card in flight, and whose turn it is to throw one or two.
    //
    // Cards are moved by hand rather than by the physics engine, because their flight is
    // scripted: out to a fixed point, hang and spin, then home back to the thrower. Giving
    // them a physics body would fight all three of those.
    //
    // Every position and speed is BoplFixedMath.Fix and every timer a whole tick count.
    // Bopl Battle runs a deterministic lockstep simulation with replays and network
    // checksums, so a float in here would desync online play even when it looked right.
    public static class BoomerangState
    {
        private enum Phase
        {
            Outbound,
            Spinning,
            Returning,
        }

        private class Card
        {
            public GameObject Visual;
            public SpriteRenderer Renderer;
            public int OwnerId;
            public int Slot;
            public Phase Phase;
            public Vec2 Position;
            public Vec2 Direction;      // unit vector, outbound
            public Vec2 StopPoint;      // where it stops and spins
            public Fix OutSpeed;        // set by the strength of the throw
            public int SpinTicksLeft;
            public int TicksAlive;
            public int ReturnTicks;
            public float SpinAngle;     // presentation only

            // External forces the boomerang has picked up: black hole pull, and shoves
            // from gusts and blasts. Carried separately from its own flight so the two
            // can be reasoned about independently -- the boomerang always keeps trying to
            // do its own thing, and this is what the world does to it on top.
            public Vec2 Drift;

            // The thrower's scale at the moment of the throw. Everything about this card
            // that has a size is multiplied by it: what it looks like, what it cuts, and how
            // close you have to be to catch it.
            public Fix Scale;


            // Set when a black hole has eaten it; the flight loop then drops it.
            public bool Swallowed;
        }

        private static readonly List<Card> cards = new List<Card>();

        private static readonly Dictionary<int, SlimeController> controllers =
            new Dictionary<int, SlimeController>();

        public static long KeyFor(int playerId, int slot)
        {
            return (long)playerId * 16L + slot;
        }

        // Throws one boomerang. Always one: the alternating single/double volley went
        // away with the playing cards.
        // The distance comes from the caller, because it depends on how long the throw
        // button was held -- a tap goes ThrowDistance, a full hold goes 130% of it.
        public static void Throw(int playerId, int slot, Vec2 origin, Vec2 aim, Fix distance,
                                 Fix outSpeed)
        {
            if (Vec2.SqrMagnitude(aim) <= Fix.Zero)
            {
                // No aim input at all -- throw straight up rather than nowhere.
                aim = Vec2.up;
            }
            aim = Vec2.Normalized(aim);
            Spawn(playerId, slot, origin, aim, distance, outSpeed);
            Plugin.Log.LogInfo($"Boomerang: player {playerId} threw one "
                               + $"{(float)distance:0.#} units.");
        }

        private static void Spawn(int playerId, int slot, Vec2 origin, Vec2 direction, Fix range,
                                  Fix outSpeed)
        {
            // The outbound leg stops at the first wall, so a card cannot be thrown through
            // a platform. Worked out once here rather than tested every tick, which also
            // keeps the flight exact.
            Fix distance = range;
            DetPhysics physics = DetPhysics.Get();
            if (physics != null)
            {
                RaycastInformation hit = physics.RaycastToClosest(
                    origin, direction, range, LayerMask.GetMask("wall"));
                if ((bool)hit && hit.nearDist < distance)
                {
                    // Stop just short of the surface rather than inside it.
                    distance = Fix.Max(Fix.Zero, hit.nearDist - (Fix)0.4f);
                }
            }

            // Sized from the thrower, like every native projectile: a grown player throws a
            // big one and a shrunk player throws a small one. Captured at the throw rather
            // than read live, so a grow ray hitting the thrower afterwards does not resize
            // something already in the air.
            Fix scale = ScaleOf(playerId);

            Card card = new Card
            {
                OwnerId = playerId,
                Scale = scale,
                OutSpeed = outSpeed,
                Slot = slot,
                Phase = Phase.Outbound,
                Position = origin,
                Direction = direction,
                StopPoint = origin + direction * distance,
                SpinTicksLeft = BoomerangAbility.SpinTicks,
            };

            card.Visual = new GameObject("ThrownCard");
            card.Renderer = card.Visual.AddComponent<SpriteRenderer>();
            card.Renderer.sprite = BoomerangVisuals.BoomerangSprite();
            card.Renderer.sortingOrder = 900;
            card.Visual.transform.localScale = new Vector3((float)scale, (float)scale, 1f);
            cards.Add(card);
        }

        // The thrower's own scale, defaulting to 1 if they cannot be found. Player.Scale
        // stays correct whether they are a slime or inside an ability.
        private static Fix ScaleOf(int playerId)
        {
            PlayerHandler handler = PlayerHandler.Get();
            Player player = handler == null ? null : handler.GetPlayer(playerId);
            return player == null ? Fix.One : player.Scale;
        }

        // Called once per simulation tick from a patch on Updater.TickSimulation. Not a
        // MonoUpdatable of our own: Updater.PreLevelLoad clears the updatable list on every
        // level load, so anything registered before then silently stops ticking.
        // Time Stop. Everything belonging to a player caught in it must stand still.
        //
        // The game does this by scaling a player's delta time to zero -- native code asks
        // GameTime.FixedDeltaTime(ownerId, dt) and simply moves nothing. Our per-tick logic
        // hangs off a Postfix on Updater.TickSimulation, which keeps running regardless, so
        // nothing here freezes unless it is told to. GameTime.IsTimeStoppedFor is the whole
        // test: it accounts for the caster's own team being protected, so an ally's stopped
        // clock does not freeze your own.
        //
        // Frozen means frozen for the WHOLE update, kills included. A projectile that cannot
        // move cannot arrive at anyone, so leaving its kill test running would let a stopped
        // boomerang carry on cutting whoever walked into it.
        public static void Tick()
        {
            if (cards.Count == 0)
            {
                return;
            }
            Fix dt = GameTime.FixedTimeStep;

            for (int i = cards.Count - 1; i >= 0; i--)
            {
                Card card = cards[i];
                if (GameTime.IsTimeStoppedFor(card.OwnerId))
                {
                    // Time stopped for its owner: the boomerang hangs in the air. See the
                    // note on Tick.
                    continue;
                }
                card.TicksAlive++;

                bool finished = false;
                switch (card.Phase)
                {
                    case Phase.Outbound:
                        finished = Advance(card, card.StopPoint, card.OutSpeed * dt,
                                           () => card.Phase = Phase.Spinning);
                        break;

                    case Phase.Spinning:
                        card.SpinTicksLeft--;
                        if (card.SpinTicksLeft <= 0)
                        {
                            card.Phase = Phase.Returning;
                        }
                        break;

                    case Phase.Returning:
                        // Homes on where the thrower is *now*, and passes straight through
                        // platforms on the way -- only the outbound leg is blocked.
                        card.ReturnTicks++;
                        if (card.ReturnTicks > BoomerangAbility.MaxReturnTicks)
                        {
                            // Give up rather than trail the thrower around the map forever.
                            finished = true;
                            break;
                        }
                        Vec2 home;
                        if (!TryGetPlayerPosition(card.OwnerId, out home))
                        {
                            finished = true;
                            break;
                        }
                        // Faster the further it still has to come, so it whips back
                        // from a long throw and eases in for a short one. Recomputed
                        // every tick from the CURRENT gap, so a thrower running away
                        // makes it speed up rather than losing it.
                        Fix gap = Vec2.Magnitude(home - card.Position);
                        Fix speed = BoomerangAbility.ReturnSpeed;
                        bool reached = Advance(card, home, speed * dt, null)
                                       || Vec2.SqrMagnitude(home - card.Position)
                                          <= BoomerangAbility.CatchRadius * card.Scale
                                             * (BoomerangAbility.CatchRadius * card.Scale);
                        if (reached)
                        {
                            // Caught, as opposed to having run out of time -- only this
                            // deserves the pickup chime.
                            BoomerangSound.PlayPickup((Vector3)(Vector2)card.Position);
                            finished = true;
                        }
                        break;
                }

                ApplyExternalForces(card, dt);
                if (card.Swallowed)
                {
                    Remove(i);
                    continue;
                }
                HitThings(card);
                BoomerangVisuals.Draw(card.Visual, card.Renderer, card.Position, ref card.SpinAngle);
                BoomerangVisuals.Blink(card.Renderer, TicksBeforeGivingUp(card));

                if (finished)
                {
                    Remove(i);
                }
            }
        }

        // Lets the world move the boomerang.
        //
        // It flies on a hand-written path rather than as a physics body, which is what
        // keeps its flight exact under lockstep -- but it also means nothing in the game
        // can push it, because every force applier in the game looks for a BoplBody and
        // this has none. So rather than being pushed, it samples what is around it and
        // carries the result as a drift on top of its own motion.
        //
        // The drift bleeds away every tick, so a boomerang knocked off course recovers and
        // still finds its way home instead of being permanently deflected.
        private static void ApplyExternalForces(Card card, Fix dt)
        {
            PullTowardsBlackHoles(card, dt);

            if (card.Drift == Vec2.zero)
            {
                return;
            }
            Fix speed = Vec2.Magnitude(card.Drift);
            if (speed > BoomerangAbility.MaxDriftSpeed)
            {
                card.Drift = Vec2.Normalized(card.Drift) * BoomerangAbility.MaxDriftSpeed;
            }
            card.Position += card.Drift * dt;
            card.Drift *= BoomerangAbility.DriftDecayPerTick;
        }

        // The same shape of pull the game uses on everything else: acceleration is
        // G * mass / distance^2 towards the hole, and only inside the hole's own influence
        // radius. A white hole has negative mass, so the identical maths pushes instead --
        // nothing special is needed for it.
        private static void PullTowardsBlackHoles(Card card, Fix dt)
        {
            foreach (BlackHole hole in UnityEngine.Object.FindObjectsOfType<BlackHole>())
            {
                if (hole == null || hole.IsDestroyed || hole.dCircle == null)
                {
                    continue;
                }
                Vec2 toHole = hole.dCircle.position - card.Position;
                Fix distanceSquared = Vec2.SqrMagnitude(toHole);
                if (distanceSquared <= Fix.Zero)
                {
                    continue;
                }
                Fix distance = Fix.Sqrt(distanceSquared);

                // Close enough to be swallowed. Without this a boomerang dragged into a
                // black hole just sat there being pulled forever: it is not a physics body,
                // so the hole's own "eat what touches me" collision never saw it.
                if (distance <= hole.dCircle.radius * BoomerangAbility.BlackHoleSwallowRadius)
                {
                    card.Swallowed = true;
                    Plugin.Log.LogInfo("Boomerang: a black hole swallowed a boomerang.");
                    return;
                }

                Fix influence = hole.dCircle.radius * hole.influenceRadiusMultiplier;
                if (distance >= influence)
                {
                    continue;
                }

                Fix mass = MassOf(hole);
                // dir / distance^2, written as toHole / distance^3 so the vector is
                // normalised and the falloff applied in one step.
                Fix cube = distanceSquared * distance;
                if (cube <= Fix.Zero)
                {
                    continue;
                }
                Vec2 pull = toHole * (hole.G * mass * BoomerangAbility.BlackHolePull) / cube;
                card.Drift += pull * dt;
            }
        }

        // A black hole's mass is private and changes as it eats things, so it is read by
        // reflection rather than assumed. Falls back to a constant if the field ever moves.
        private static Fix MassOf(BlackHole hole)
        {
            try
            {
                return Traverse.Create(hole).Field("mass").GetValue<Fix>();
            }
            catch (Exception ex)
            {
                if (warnedAboutMass)
                {
                    return BoomerangAbility.FallbackBlackHoleMass;
                }
                warnedAboutMass = true;
                Plugin.Log.LogWarning("Boomerang: could not read a black hole's mass, so its pull on "
                                      + $"the boomerang uses a fixed strength: {ex.Message}");
                return BoomerangAbility.FallbackBlackHoleMass;
            }
        }

        private static bool warnedAboutMass;

        // Called from a patch on Shockwave, which is what gusts and explosions both use.
        // Applied as a single shove per activation rather than sampled per tick, because a
        // shockwave IS a single event -- sampling it would apply it once per frame for as
        // long as the object happened to exist.
        public static void Shove(Vec2 origin, Fix radius, Fix strength)
        {
            if (cards.Count == 0)
            {
                return;
            }
            Fix radiusSquared = radius * radius;
            foreach (Card card in cards)
            {
                Vec2 away = card.Position - origin;
                Fix distanceSquared = Vec2.SqrMagnitude(away);
                if (distanceSquared > radiusSquared || distanceSquared <= Fix.Zero)
                {
                    continue;
                }
                // Strongest at the centre, fading to nothing at the edge, which is how a
                // blast reads.
                Fix falloff = Fix.One - Vec2.Magnitude(away) / radius;
                card.Drift += Vec2.Normalized(away) * strength * falloff
                              * BoomerangAbility.ShockwavePush;
            }
            Plugin.Log.LogInfo("Boomerang: a shockwave shoved a boomerang off course.");
        }

        // How long a card has left before it gives up, or -1 if it is not on that clock.
        // Only a returning card is running out; one still flying out or spinning is not.
        private static int TicksBeforeGivingUp(Card card)
        {
            if (card.Phase != Phase.Returning)
            {
                return -1;
            }
            return BoomerangAbility.MaxReturnTicks - card.ReturnTicks;
        }

        // Moves a card toward a target, returning true once it is there.
        private static bool Advance(Card card, Vec2 target, Fix step, Action onArrive)
        {
            Vec2 toTarget = target - card.Position;
            Fix distance = Vec2.Magnitude(toTarget);
            if (distance <= step)
            {
                card.Position = target;
                if (onArrive != null)
                {
                    onArrive();
                }
                return onArrive == null;
            }
            card.Position += Vec2.Normalized(toTarget) * step;
            return false;
        }

        // Cards cut players and set off anything explosive they pass.
        private static void HitThings(Card card)
        {
            Fix hitRadius = BoomerangAbility.HitRadius * card.Scale;
            Fix radiusSquared = hitRadius * hitRadius;

            PlayerHandler handler = PlayerHandler.Get();
            if (handler != null)
            {
                foreach (Player victim in handler.PlayerList())
                {
                    if (victim == null || !victim.IsAlive)
                    {
                        continue;
                    }
                    // Your own cards never cut you. They are thrown from your hand and are
                    // meant to be caught on the way back, so any window at all in which they
                    // are lethal to their thrower just kills you for catching them.
                    if (victim.Id == card.OwnerId)
                    {
                        continue;
                    }
                    // Tested per BODY, not against Player.Position. A player can be several
                    // bodies at once -- one per clone, and an ability object rather than a
                    // slime while they are inside an ability -- and Player.Position is a
                    // single point that cannot describe them all. See PlayerBodies.
                    Vec2 centre = card.Position;
                    int bodiesSeen;
                    PlayerBodies.KillWhere(
                        victim.Id, card.OwnerId, CauseOfDeath.Other,
                        position => Vec2.SqrMagnitude(position - centre) <= radiusSquared,
                        out bodiesSeen);

                    if (bodiesSeen == 0)
                    {
                        // The scan found nothing to aim at, which should be impossible for a
                        // living player. Fall back to the old lookup so a wrong assumption
                        // here can only cost accuracy, never every kill in the mod.
                        PlayerBodies.WarnIfBlind(bodiesSeen, "Boomerang");
                        if (Vec2.SqrMagnitude(victim.Position - card.Position) <= radiusSquared)
                        {
                            SlimeController slime = ControllerFor(victim.Id);
                            PlayerCollision collision =
                                slime == null ? null : slime.GetActivePlayerCollision();
                            if (collision != null)
                            {
                                collision.killPlayer(card.OwnerId, spawnEffect: true,
                                                     ignoreInvulnerability: false,
                                                     CauseOfDeath.Other);
                            }
                        }
                    }
                }
            }

            // The concrete components are searched for rather than the IGrenadeDetonation
            // interface they share, because that interface is internal to the game's
            // assembly and cannot be referenced from here.
            foreach (GrenadeExplode grenade in UnityEngine.Object.FindObjectsOfType<GrenadeExplode>())
            {
                if (!Near(grenade, card.Position, radiusSquared))
                {
                    continue;
                }
                // The Sheep Bomb's sheep is built on a grenade body, so it turns up in this
                // scan and used to be DETONATED -- a boomerang setting off an explosion,
                // and skipping the sheep's own red-splat death entirely. Sheep are cut
                // down, everything else goes off.
                if (SheepInterop.TryKill(grenade))
                {
                    continue;
                }
                Detonate(() => grenade.Detonate(), "grenade");
            }
            foreach (Mine mine in UnityEngine.Object.FindObjectsOfType<Mine>())
            {
                if (Near(mine, card.Position, radiusSquared))
                {
                    Detonate(() => mine.Detonate(), "mine");
                }
            }
        }

        private static bool Near(Component component, Vec2 point, Fix radiusSquared)
        {
            if (component == null)
            {
                return false;
            }
            FixTransform fixTrans = component.GetComponent<FixTransform>();
            if (fixTrans == null || fixTrans.IsDestroyed)
            {
                return false;
            }
            return Vec2.SqrMagnitude(fixTrans.position - point) <= radiusSquared;
        }

        // Another mod's or the game's own detonation code runs here, and an object part-way
        // through being destroyed can throw. Logged rather than swallowed, and never allowed
        // to escape into the simulation loop.
        private static void Detonate(Action detonate, string what)
        {
            try
            {
                detonate();
            }
            catch (Exception ex)
            {
                Plugin.Log.LogWarning($"Boomerang: setting off a {what} threw, so it was left "
                                      + $"alone: {ex.Message}");
            }
        }

        private static bool TryGetPlayerPosition(int playerId, out Vec2 position)
        {
            PlayerHandler handler = PlayerHandler.Get();
            Player player = handler == null ? null : handler.GetPlayer(playerId);
            if (player == null || !player.IsAlive)
            {
                position = Vec2.zero;
                return false;
            }
            position = player.Position;
            return true;
        }

        private static SlimeController ControllerFor(int playerId)
        {
            SlimeController cached;
            if (controllers.TryGetValue(playerId, out cached) && cached != null)
            {
                return cached;
            }
            foreach (SlimeController slime in UnityEngine.Object.FindObjectsOfType<SlimeController>())
            {
                if (slime.GetPlayerId() == playerId)
                {
                    controllers[playerId] = slime;
                    return slime;
                }
            }
            return null;
        }

        private static void Remove(int index)
        {
            if (cards[index].Visual != null)
            {
                UnityEngine.Object.Destroy(cards[index].Visual);
            }
            cards.RemoveAt(index);
        }

        // True while this ability still has cards in the air.
        public static bool HasCardsOut(long key)
        {
            foreach (Card card in cards)
            {
                if (KeyFor(card.OwnerId, card.Slot) == key)
                {
                    return true;
                }
            }
            return false;
        }

        // Holds the cooldown timer at zero for any ability with cards still out, so the
        // wait only begins once the last one is gone. Without this the cooldown ran while
        // the cards were still flying, which meant a fast enough player could keep a fresh
        // volley in the air permanently.
        //
        // abilityCooldownTimers is a private Fix[] on SlimeController that counts up, and
        // isAbilityReady compares it against the ability's cooldown, so zeroing this slot's
        // entry is what "not yet" means.
        public static void PinCooldownsWhileCardsOut()
        {
            if (cards.Count == 0)
            {
                return;
            }
            foreach (Card card in cards)
            {
                SlimeController slime = ControllerFor(card.OwnerId);
                if (slime == null)
                {
                    continue;
                }
                Fix[] timers = Traverse.Create(slime).Field("abilityCooldownTimers").GetValue<Fix[]>();
                if (timers != null && card.Slot >= 0 && card.Slot < timers.Length)
                {
                    timers[card.Slot] = Fix.Zero;
                }
            }
        }

        public static void ClearAll()
        {
            for (int i = cards.Count - 1; i >= 0; i--)
            {
                Remove(i);
            }
            controllers.Clear();
        }
    }
}
