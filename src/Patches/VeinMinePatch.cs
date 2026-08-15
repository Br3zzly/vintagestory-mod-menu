using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.Client.NoObf;

namespace ModMenu.Patches
{
    /// <summary>
    /// Spots the player breaking a block, so the vein miner can carry on from there.
    ///
    /// ClientMain.OnPlayerTryDestroyBlock is the one door every player-driven break goes
    /// through: it breaks the block in the client's own world, fires the block-changed event
    /// and sends the break to the server. Reading the block has to happen before that runs,
    /// which is why the id is carried from prefix to postfix rather than looked up after.
    /// </summary>
    [HarmonyPatch(typeof(ClientMain), nameof(ClientMain.OnPlayerTryDestroyBlock))]
    public static class VeinMinePatch
    {
        [HarmonyPrefix]
        public static void Prefix(ClientMain __instance, BlockSelection blockSelection, out int __state)
        {
            __state = 0;

            if (VeinMiner.Instance == null || blockSelection?.Position == null) return;

            Block block = blockSelection.Block ?? __instance.BlockAccessor?.GetBlock(blockSelection.Position);
            __state = block?.Id ?? 0;
        }

        [HarmonyPostfix]
        public static void Postfix(BlockSelection blockSelection, int __state)
        {
            if (__state == 0) return;

            VeinMiner.Instance?.OnPlayerBrokeBlock(blockSelection.Position, __state);
        }
    }
}
