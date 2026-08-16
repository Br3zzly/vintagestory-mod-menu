using System.Collections.Generic;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ModMenu
{
    /// <summary>
    /// Which players currently have which feature enabled, keyed by player UID.
    ///
    /// Harmony patches are static and process-wide, so they cannot reach into a ModSystem
    /// instance - they read this instead. Both sides write to it:
    ///
    ///   - client: the local player's own toggles, set straight from the dialog
    ///   - server: whatever clients reported over the network channel
    ///
    /// In singleplayer the client and the internal server share one process, so a single
    /// entry covers both and every feature works end to end. Against a remote server that
    /// does not have this mod installed only the client half exists, which is why
    /// invincibility and durability are client-visual only there.
    /// </summary>
    public static class ModMenuState
    {
        private static readonly HashSet<string> invincible = new HashSet<string>();
        private static readonly HashSet<string> instantMine = new HashSet<string>();
        private static readonly HashSet<string> noDurability = new HashSet<string>();
        private static readonly HashSet<string> dropsAtPlayer = new HashSet<string>();
        private static readonly HashSet<string> oneHitKill = new HashSet<string>();
        private static readonly HashSet<string> noHunger = new HashSet<string>();
        private static readonly HashSet<string> fastPickup = new HashSet<string>();
        private static readonly HashSet<string> rangedAttack = new HashSet<string>();

        private static readonly object sync = new object();

        public static void Set(string playerUid, EnumFeature feature, bool enabled)
        {
            if (playerUid == null) return;

            lock (sync)
            {
                HashSet<string> set = SetFor(feature);
                if (set == null) return;

                if (enabled) set.Add(playerUid);
                else set.Remove(playerUid);
            }
        }

        public static bool Get(string playerUid, EnumFeature feature)
        {
            if (playerUid == null) return false;

            lock (sync)
            {
                HashSet<string> set = SetFor(feature);
                return set != null && set.Contains(playerUid);
            }
        }

        /// <summary>Drops a disconnecting player so their toggles do not linger on the server.</summary>
        public static void Clear(string playerUid)
        {
            if (playerUid == null) return;

            lock (sync)
            {
                invincible.Remove(playerUid);
                instantMine.Remove(playerUid);
                noDurability.Remove(playerUid);
                dropsAtPlayer.Remove(playerUid);
                oneHitKill.Remove(playerUid);
                noHunger.Remove(playerUid);
                fastPickup.Remove(playerUid);
                rangedAttack.Remove(playerUid);
            }
        }

        /// <summary>Convenience for the patches, which are handed an Entity rather than a UID.</summary>
        public static bool EntityHas(Entity entity, EnumFeature feature)
        {
            var player = entity as EntityPlayer;
            return player != null && Get(player.PlayerUID, feature);
        }

        private static HashSet<string> SetFor(EnumFeature feature)
        {
            switch (feature)
            {
                case EnumFeature.Invincible: return invincible;
                case EnumFeature.InstantMine: return instantMine;
                case EnumFeature.NoDurability: return noDurability;
                case EnumFeature.DropsAtPlayer: return dropsAtPlayer;
                case EnumFeature.OneHitKill: return oneHitKill;
                case EnumFeature.NoHunger: return noHunger;
                case EnumFeature.FastPickup: return fastPickup;
                case EnumFeature.RangedAttack: return rangedAttack;

                // Null rather than a fallback set, because aliasing one feature onto another
                // would silently flip the wrong toggle - and null rather than an exception,
                // because these values arrive over the network. A client running a newer build
                // sends features an older server has never heard of, and throwing here means an
                // unhandled exception inside a packet handler, which disconnects that player
                // mid-join. Unknown features are simply not ours to track.
                default: return null;
            }
        }
    }

    public enum EnumFeature
    {
        Invincible,
        InstantMine,
        NoDurability,
        DropsAtPlayer,
        OneHitKill,
        NoHunger,
        FastPickup,

        /// <summary>
        /// Not a toggle of its own: the client reports this whenever its reach is extended, so
        /// the server can let attacks reach as far as the crosshair does.
        /// </summary>
        RangedAttack
    }

    /// <summary>Sent client -> server whenever a server-authoritative toggle flips.</summary>
    [ProtoBuf.ProtoContract]
    public class FeatureTogglePacket
    {
        [ProtoBuf.ProtoMember(1)]
        public EnumFeature Feature;

        [ProtoBuf.ProtoMember(2)]
        public bool Enabled;
    }
}
