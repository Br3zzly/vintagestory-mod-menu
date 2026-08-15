using HarmonyLib;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;

namespace ModMenu.Patches
{
    /// <summary>
    /// Stops tools, weapons and armour losing durability.
    ///
    /// DamageItem is the single choke point every wear-and-tear path goes through, so
    /// skipping it covers mining, chopping, combat and shield blocking in one patch.
    ///
    /// Same caveat as invincibility: durability lives on the server's authoritative copy of
    /// the inventory. This is fully effective in singleplayer and on servers running this
    /// mod; against a vanilla remote server the client just renders a fuller bar until the
    /// next inventory sync corrects it.
    /// </summary>
    [HarmonyPatch(typeof(CollectibleObject), nameof(CollectibleObject.DamageItem))]
    public static class DurabilityPatch
    {
        [HarmonyPrefix]
        public static bool Prefix(Entity byEntity)
        {
            return !ModMenuState.EntityHas(byEntity, EnumFeature.NoDurability);
        }
    }
}
