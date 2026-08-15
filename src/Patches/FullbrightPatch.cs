using HarmonyLib;
using Vintagestory.API.MathTools;

namespace ModMenu.Patches
{
    /// <summary>
    /// Fullbright, done where light actually happens.
    ///
    /// Chunk light is not a lamp shining at runtime - it is baked into the mesh when a chunk is
    /// tesselated. ChunkTesselator builds a ColorUtil.LightUtil over the world's light level
    /// tables and calls ToRgba for every vertex, which packs block light into RGB and sun light
    /// into A. Answering "fully lit" there makes unlit rock tesselate exactly like rock in
    /// daylight.
    ///
    /// Post-processing cannot do this, which is why the gamma approach failed: an unlit cave
    /// renders as black, pow(0, anything) is still 0, and no brightness curve turns black into
    /// something you can see.
    ///
    /// Chunks only take this up when they are next tesselated, so toggling it has to be
    /// followed by ClientMain.RedrawAllBlocks.
    /// </summary>
    [HarmonyPatch(typeof(ColorUtil.LightUtil), nameof(ColorUtil.LightUtil.ToRgba))]
    public static class FullbrightPatch
    {
        /// <summary>
        /// A plain field rather than a ModMenuState lookup, which takes a lock: this runs per
        /// vertex, on the tesselation threads, for every chunk coming into view.
        /// </summary>
        public static volatile bool Enabled;

        /// <summary>Full block light in R, G and B, full sun light in A.</summary>
        private const int FullyLit = unchecked((int)0xFFFFFFFF);

        [HarmonyPrefix]
        public static bool Prefix(ref int __result)
        {
            if (!Enabled) return true;

            __result = FullyLit;
            return false;
        }
    }
}
