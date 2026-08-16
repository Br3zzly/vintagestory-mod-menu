using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Datastructures;
using Vintagestory.Client.NoObf;

namespace ModMenu
{
    /// <summary>
    /// Stops every block except the tracked ones from being drawn, leaving them standing in
    /// open air.
    ///
    /// Nothing is outlined while this is on: what is left is drawn by the world itself. That
    /// puts the weight on Fullbright, which is what stops the world painting black over the
    /// distance - see ModMenuSystem.ApplyFullbrightAmbient. Without it, everything past about
    /// twenty blocks fades out and there is nothing to see down there.
    ///
    /// Two separate things decide whether a face reaches a chunk mesh, and both have to be
    /// answered or the result is a world that is empty in the wrong way:
    ///
    ///   - the block's own DrawType. ChunkTesselator returns immediately for Empty, which is
    ///     what air is, so a block set to Empty contributes no geometry at all.
    ///   - the *neighbour's* SideOpaque. A face is culled where the block beyond it is opaque,
    ///     so hiding stone without also clearing its opacity would leave buried ore with all
    ///     six of its faces culled: invisible, inside an invisible world.
    ///
    /// Both are plain fields on the client's own block list, read while a chunk is tesselated
    /// and nowhere that decides what the world is. Collision and aiming read the collision and
    /// selection boxes, which are left alone, so the world stays solid to stand on and to mine.
    /// It just stops being drawn.
    ///
    /// The originals are kept per block id and put back on the way out. Either direction needs
    /// the chunk meshes rebuilt, because what they contain is exactly what changed - the same
    /// call the game makes for a graphics setting like smooth shadows.
    /// </summary>
    public class TransparentWorld
    {
        /// <summary>How one block type was drawn before it was hidden.</summary>
        private struct Original
        {
            public EnumDrawType DrawType;
            public SmallBoolArray SideOpaque;
        }

        private readonly ICoreClientAPI capi;

        private readonly Dictionary<int, Original> hidden = new Dictionary<int, Original>();

        /// <summary>
        /// The block ids the world is currently hidden for, or null when it is not. Held so
        /// that being asked for the same thing again - which is every frame - costs a set
        /// comparison and nothing else.
        /// </summary>
        private HashSet<int> shown;

        public TransparentWorld(ICoreClientAPI capi)
        {
            this.capi = capi;
        }

        /// <summary>Whether the world is hidden right now, rather than merely asked to be.</summary>
        public bool Active => shown != null;

        /// <summary>
        /// Hides everything except <paramref name="visible"/>. Null or empty puts the world
        /// back - with nothing to leave visible there would be nothing left to look at.
        /// </summary>
        public void Apply(ICollection<int> visible)
        {
            if (visible != null && visible.Count == 0) visible = null;

            if (visible == null ? shown == null : shown != null && shown.SetEquals(visible)) return;

            PutBack();

            if (visible != null)
            {
                Hide(visible);
                shown = new HashSet<int>(visible);
            }

            // Meshes already built still hold the blocks that just went away, and lack the ones
            // that just came back.
            (capi.World as ClientMain)?.RedrawAllBlocks();
        }

        private void Hide(ICollection<int> visible)
        {
            foreach (Block block in capi.World.Blocks)
            {
                // Air is already empty, and so is anything else that draws nothing.
                if (block == null || block.Id == 0 || block.Code == null) continue;
                if (block.DrawType == EnumDrawType.Empty && !block.SideOpaque.Any) continue;
                if (visible.Contains(block.Id)) continue;

                hidden[block.Id] = new Original
                {
                    DrawType = block.DrawType,
                    SideOpaque = block.SideOpaque
                };

                block.DrawType = EnumDrawType.Empty;

                // Opaque on no side, so the faces of whatever is left keep being drawn.
                block.SideOpaque = new SmallBoolArray(0);
            }
        }

        private void PutBack()
        {
            foreach (KeyValuePair<int, Original> pair in hidden)
            {
                Block block = capi.World.GetBlock(pair.Key);
                if (block == null) continue;

                block.DrawType = pair.Value.DrawType;
                block.SideOpaque = pair.Value.SideOpaque;
            }

            hidden.Clear();
            shown = null;
        }
    }
}
