using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.GameContent;

namespace ModMenu.Patches
{
    /// <summary>
    /// One hit kill.
    ///
    /// Damage is worked out entirely on the server - the attack packet carries no number - so
    /// this only does anything with the mod installed there, singleplayer included.
    ///
    /// The important difference from invincibility: that one asks "does the entity taking the
    /// hit have the feature", while this asks about the entity dealing it. Reading the victim's
    /// toggle here would one-shot the player instead of their target.
    /// </summary>
    [HarmonyPatch(typeof(EntityBehaviorHealth), nameof(EntityBehaviorHealth.OnEntityReceiveDamage))]
    public static class OneHitKillPatch
    {
        /// <summary>Well past any creature's health, without being near float overflow.</summary>
        private const float LethalDamage = 99999f;

        [HarmonyPrefix]
        public static void Prefix(DamageSource damageSource, ref float damage)
        {
            if (damageSource == null) return;

            // Healing runs through the same door with a Heal damage type. Multiplying that
            // would top targets up instead of killing them.
            if (damageSource.Type == EnumDamageType.Heal) return;

            var attacker = (damageSource.SourceEntity ?? damageSource.CauseEntity) as EntityPlayer;
            if (attacker == null) return;

            if (!ModMenuState.Get(attacker.PlayerUID, EnumFeature.OneHitKill)) return;

            damage = LethalDamage;
        }
    }

    /// <summary>
    /// No hunger: saturation stops draining.
    ///
    /// EntityBehaviorHunger.ReduceSaturation is the one place the bar goes down - the tick
    /// timer, sprinting and swinging a weapon all funnel into it - so refusing it there covers
    /// every route without touching the value itself. Private, which Harmony does not mind.
    ///
    /// Saturation lives in WatchedAttributes, which the server owns and pushes to clients, so
    /// like invincibility this needs the mod on the server to mean anything.
    /// </summary>
    [HarmonyPatch(typeof(EntityBehaviorHunger), "ReduceSaturation")]
    public static class NoHungerPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(EntityBehaviorHunger __instance)
        {
            var player = __instance?.entity as EntityPlayer;
            if (player == null) return true;

            // Skipping the original leaves saturation, and the nutrient levels it also decays,
            // exactly where they were.
            return !ModMenuState.Get(player.PlayerUID, EnumFeature.NoHunger);
        }
    }

    /// <summary>
    /// Faster item pickup.
    ///
    /// EntityBehaviorCollectEntities collects at a fixed 23 items a second from a 1.5 block
    /// radius, which is the real bottleneck after a large vein mine. The rate is a private
    /// field read fresh on every tick, so raising it just before the tick runs is enough - and
    /// it is put back for anyone without the feature, since the same behavior class runs for
    /// every entity that picks things up.
    ///
    /// The radius is left alone: items still have to be near you. Pair it with drops at player
    /// to have them land there in the first place.
    /// </summary>
    [HarmonyPatch(typeof(EntityBehaviorCollectEntities), nameof(EntityBehaviorCollectEntities.OnGameTick))]
    public static class FastPickupPatch
    {
        /// <summary>
        /// Items a second while the feature is on. The tick loop runs at most one iteration per
        /// item per second of accumulated time, and that time is capped at one second, so this
        /// is also the worst case number of collections in a single tick.
        /// </summary>
        private const float FastRate = 500f;

        /// <summary>The game's own rate, restored for anyone not using the feature.</summary>
        private const float NormalRate = 23f;

        [HarmonyPrefix]
        public static void Prefix(EntityBehaviorCollectEntities __instance, ref float ___itemsPerSecond)
        {
            var player = __instance?.entity as EntityPlayer;
            if (player == null) return;

            ___itemsPerSecond = ModMenuState.Get(player.PlayerUID, EnumFeature.FastPickup)
                ? FastRate
                : NormalRate;
        }
    }
}
