using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace ModMenu
{
    /// <summary>
    /// Outlines every block a vein mine would take, while the vein miner is on and the player
    /// is looking at something.
    ///
    /// Drawn with WireframeCube, the same class the game outlines the aimed-at block with
    /// (SystemSelectedBlockOutline), at the same render stage - so the preview sits in the
    /// world exactly like the selection box does, in white rather than black.
    ///
    /// The vein itself comes from VeinMiner.FindVein, the same search the miner runs, so the
    /// outlines cannot promise something the miner would not break.
    /// </summary>
    public class VeinPreview : IRenderer
    {
        /// <summary>Slightly see-through, so a vein several blocks deep stays readable.</summary>
        private static readonly Vec4f Colour = new Vec4f(1f, 1f, 1f, 0.85f);

        private readonly ICoreClientAPI capi;
        private readonly ModMenuConfig config;

        /// <summary>Built on the first frame: uploading a mesh needs the render thread.</summary>
        private WireframeCube cube;

        private List<BlockPos> vein = new List<BlockPos>();

        // What the cached vein was worked out from, to not re-walk it every frame.
        private BlockPos fromPos;
        private int fromBlockId;
        private int fromLimit;

        public double RenderOrder => 0.9;

        /// <summary>Blocks away the outlines stay visible. Past a vein's own size is pointless.</summary>
        public int RenderRange => 24;

        public VeinPreview(ICoreClientAPI capi, ModMenuConfig config)
        {
            this.capi = capi;
            this.config = config;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            BlockSelection aimed = capi.World?.Player?.CurrentBlockSelection;

            if (!config.VeinMiner || config.VeinMinerLimit <= 1 || aimed?.Position == null)
            {
                Forget();
                return;
            }

            Block block = capi.World.BlockAccessor.GetBlock(aimed.Position);
            if (block == null || block.Id <= 0)
            {
                Forget();
                return;
            }

            Refresh(aimed.Position, block.Id);

            // Only the extra blocks are worth outlining: the first is the one already under the
            // crosshair, which the game is outlining itself.
            if (vein.Count < 2) return;

            if (cube == null) cube = WireframeCube.CreateUnitCube(capi);

            EntityPlayer player = capi.World.Player.Entity;
            Vec3d offset = player.CameraPosOffset;

            for (int i = 1; i < vein.Count; i++)
            {
                BlockPos pos = vein[i];
                cube.Render(capi,
                    pos.X + offset.X,
                    pos.InternalY + offset.Y,
                    pos.Z + offset.Z,
                    1f, 1f, 1f,
                    1.6f * ClientSettings.Wireframethickness,
                    Colour);
            }
        }

        /// <summary>Re-walks the vein only when the answer could have changed.</summary>
        private void Refresh(BlockPos pos, int blockId)
        {
            if (fromPos != null && fromPos.Equals(pos)
                && fromBlockId == blockId && fromLimit == config.VeinMinerLimit)
            {
                return;
            }

            fromPos = pos.Copy();
            fromBlockId = blockId;
            fromLimit = config.VeinMinerLimit;

            vein = VeinMiner.FindVein(capi.World.BlockAccessor, fromPos, blockId, fromLimit);
        }

        private void Forget()
        {
            if (fromPos == null) return;

            fromPos = null;
            vein.Clear();
        }

        public void Dispose()
        {
            cube?.Dispose();
            cube = null;
        }
    }
}
