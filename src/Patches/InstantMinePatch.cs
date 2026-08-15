using HarmonyLib;
using Vintagestory.API.Common;

namespace ModMenu.Patches
{
    /// <summary>
    /// Collapses block breaking to a single tick.
    ///
    /// Block.OnGettingBroken is documented as client side only, called every 40ms while
    /// the player holds left click, and returning a remaining resistance of &lt;= 0 is what
    /// triggers the break. That makes mining speed genuinely a client-side decision in
    /// Vintage Story, so forcing the return value to 0 works on remote servers too - the
    /// client simply reports the block as broken a lot sooner than usual.
    /// </summary>
    [HarmonyPatch(typeof(Block), nameof(Block.OnGettingBroken))]
    public static class InstantMinePatch
    {
        [HarmonyPostfix]
        public static void Postfix(IPlayer player, ref float __result)
        {
            if (player == null) return;
            if (!ModMenuState.Get(player.PlayerUID, EnumFeature.InstantMine)) return;

            __result = 0f;
        }
    }
}
