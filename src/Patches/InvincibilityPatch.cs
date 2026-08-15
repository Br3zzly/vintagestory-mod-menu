using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ModMenu.Patches
{
    /// <summary>
    /// Swallows all incoming damage for players who have invincibility on.
    ///
    /// ReceiveDamage is where every damage source funnels through, so blocking it here
    /// covers mobs, falling, drowning, temperature and hunger alike.
    ///
    /// Where this actually takes effect: singleplayer (client and internal server share a
    /// process) and servers running this mod. On a vanilla remote server the server keeps
    /// its own authoritative health value and syncs it back, so this only stops the client
    /// from drawing the hit - you still take the damage.
    /// </summary>
    [HarmonyPatch(typeof(Entity), nameof(Entity.ReceiveDamage))]
    public static class InvincibilityPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Entity __instance)
        {
            // Returning false skips the original method entirely, so no damage is applied
            // and no damage-received effects fire.
            return !ModMenuState.EntityHas(__instance, EnumFeature.Invincible);
        }
    }

    /// <summary>
    /// Belt and braces: some callers check ShouldReceiveDamage before bothering to call
    /// ReceiveDamage, and a few gameplay systems (fall damage, mob AI target picking) read
    /// it directly. Answering false here keeps those consistent with the prefix above.
    /// </summary>
    [HarmonyPatch(typeof(Entity), nameof(Entity.ShouldReceiveDamage))]
    public static class ShouldReceiveDamagePatch
    {
        [HarmonyPostfix]
        public static void Postfix(Entity __instance, ref bool __result)
        {
            if (ModMenuState.EntityHas(__instance, EnumFeature.Invincible)) __result = false;
        }
    }
}
