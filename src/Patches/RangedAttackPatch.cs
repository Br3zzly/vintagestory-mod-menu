using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Server;

namespace ModMenu.Patches
{
    /// <summary>
    /// Lets attacks reach as far as the crosshair does, when reach is extended and the server
    /// runs this mod.
    ///
    /// There is no toggle for this. Extending reach already lets the client put a distant
    /// entity under the crosshair; the server is what refuses the swing, in
    /// HandleEntityInteraction:
    ///
    ///   float num = itemStack?.Collectible.GetAttackRange(itemStack) ?? DefaultAttackRange;
    ///   if (((cuboidd.ShortestDistanceFrom(x, y, z) > (double)(num * 2f)) &amp; flag) || ...) return;
    ///
    /// That check has no AntiAbuse condition and no privilege that skips it, so vanilla caps
    /// attacks at twice the weapon's range - about 3 blocks - however far you can aim.
    ///
    /// Rather than reimplement the handler to drop one clause, the weapon's own attack range is
    /// answered larger for the duration of that call. Two patches, because the range is asked
    /// for in a place that does not know which player is swinging: the first marks who is, the
    /// second answers while the mark is set. Packet handling runs to completion on one thread,
    /// so the mark is thread-local and cannot leak into another player's swing.
    /// </summary>
    public static class RangedAttack
    {
        [ThreadStatic] public static bool Active;

        /// <summary>
        /// Whether this client should reach further when it swings. Set from the reach slider,
        /// and only when the server runs this mod - without that the server drops the swing
        /// anyway, and letting the client play the hit locally would just look like a miss that
        /// should have landed.
        /// </summary>
        public static bool ClientEnabled;

        /// <summary>
        /// Answered as the weapon's attack range while a marked swing is in flight. The client
        /// compares the distance against it directly, the server against twice it.
        /// </summary>
        public const float Range = 128f;

        /// <summary>
        /// What the server thought this player could reach, put back once the swing is handled.
        /// </summary>
        [ThreadStatic] public static float SavedPickingRange;
    }

    /// <summary>
    /// The client half. TryAttackEntity measures the target against the held weapon's attack
    /// range and simply does not send the packet when it is further - a stricter gate than the
    /// server's, so without this the server never hears about the swing at all.
    /// </summary>
    [HarmonyPatch(typeof(Vintagestory.Client.NoObf.ClientMain), nameof(Vintagestory.Client.NoObf.ClientMain.TryAttackEntity))]
    public static class RangedAttackClientPatch
    {
        [HarmonyPrefix]
        public static void Prefix()
        {
            RangedAttack.Active = RangedAttack.ClientEnabled;
        }

        [HarmonyFinalizer]
        public static void Finalizer()
        {
            RangedAttack.Active = false;
        }
    }

    [HarmonyPatch(typeof(ServerSystemEntitySimulation), "HandleEntityInteraction")]
    public static class RangedAttackMarkPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ConnectedClient client)
        {
            RangedAttack.Active = false;

            string uid = client?.Player?.PlayerUID;
            if (uid == null) return;
            if (!ModMenuState.Get(uid, EnumFeature.RangedAttack)) return;

            RangedAttack.Active = true;

            // Before any range check, the handler only looks for the target within the server's
            // own picking range plus ten - so a distant animal is never found in the first
            // place. Lifted for the length of this swing and put straight back.
            IWorldPlayerData data = client.Player.WorldData;
            if (data != null)
            {
                RangedAttack.SavedPickingRange = data.PickingRange;
                data.PickingRange = RangedAttack.Range;
            }
        }

        /// <summary>Runs even if the handler threw, so neither change outlives the swing.</summary>
        [HarmonyFinalizer]
        public static void Finalizer(ConnectedClient client)
        {
            IWorldPlayerData data = client?.Player?.WorldData;
            if (RangedAttack.Active && data != null)
            {
                data.PickingRange = RangedAttack.SavedPickingRange;
            }

            RangedAttack.Active = false;
        }
    }

    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.GetAttackRange))]
    public static class RangedAttackRangePatch
    {
        [HarmonyPostfix]
        public static void Postfix(ref float __result)
        {
            if (RangedAttack.Active) __result = RangedAttack.Range;
        }
    }
}
