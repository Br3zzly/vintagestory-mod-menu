using System;
using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Server;

namespace ModMenu.Patches
{
    /// <summary>
    /// Spawns what you mine at your feet instead of in the hole.
    ///
    /// Drops are the server's to hand out - Block.SpawnDropsAndRemoveBlock only calls
    /// SpawnItemEntity when world.Side is Server - so this needs the mod on the server, the
    /// same as invincibility and durability. In singleplayer the internal server is in this
    /// process, so it works there without anything extra.
    ///
    /// Two patches rather than one because the position is decided in a different place from
    /// where the breaking player is known: the first marks who is breaking, the second reroutes
    /// the spawns that happen while that mark is set. Breaking runs start to finish on one
    /// thread, so the mark is thread-local and cannot leak into another player's drops.
    /// </summary>
    public static class DropsAtPlayer
    {
        [ThreadStatic] public static Vec3d Target;
    }

    [HarmonyPatch(typeof(Block), nameof(Block.SpawnDropsAndRemoveBlock))]
    public static class DropsAtPlayerMarkPatch
    {
        [HarmonyPrefix]
        public static void Prefix(IWorldAccessor world, IPlayer byPlayer)
        {
            DropsAtPlayer.Target = null;

            if (world == null || world.Side != EnumAppSide.Server) return;
            if (byPlayer?.Entity == null) return;
            if (!ModMenuState.Get(byPlayer.PlayerUID, EnumFeature.DropsAtPlayer)) return;

            // Half a block up, so a drop landing on the player does not start inside the floor.
            DropsAtPlayer.Target = byPlayer.Entity.Pos.XYZ.Add(0, 0.5, 0);
        }

        /// <summary>Runs even if the break threw, so the mark cannot outlive the break.</summary>
        [HarmonyFinalizer]
        public static void Finalizer()
        {
            DropsAtPlayer.Target = null;
        }
    }

    [HarmonyPatch(typeof(ServerMain), nameof(ServerMain.SpawnItemEntity),
        new[] { typeof(ItemStack), typeof(Vec3d), typeof(Vec3d) })]
    public static class DropsAtPlayerSpawnPatch
    {
        [HarmonyPrefix]
        public static void Prefix(ref Vec3d position)
        {
            Vec3d target = DropsAtPlayer.Target;
            if (target != null) position = target.Clone();
        }
    }
}
