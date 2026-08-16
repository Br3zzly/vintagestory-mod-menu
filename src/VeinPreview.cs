using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;

namespace ModMenu
{
    /// <summary>
    /// Paints every block a vein mine would take, while the vein miner is on and the player is
    /// looking at something.
    ///
    /// Drawn the same way ESP draws its targets - solid colour, through whatever is in the way -
    /// because a vein is mostly buried and outlines of what you cannot see are hard to read. In
    /// red, which is why no ESP target is ever given red: this colour means "about to be mined"
    /// and nothing else.
    ///
    /// The vein itself comes from VeinMiner.FindVein, the same search the miner runs, so what is
    /// painted cannot promise something the miner would not break.
    /// </summary>
    public class VeinPreview : IRenderer
    {
        private readonly ICoreClientAPI capi;
        private readonly ModMenuConfig config;

        /// <summary>The vein as one shape, rebuilt whenever the vein itself changes.</summary>
        private MeshRef mesh;

        private List<BlockPos> vein = new List<BlockPos>();

        /// <summary>Where the mesh was built around, since it is drawn relative to that.</summary>
        private BlockPos meshOrigin;

        // What the cached vein was worked out from, to not re-walk it every frame.
        private BlockPos fromPos;
        private int fromBlockId;
        private int fromLimit;

        private readonly Matrixf modelViewMatrix = new Matrixf();

        public double RenderOrder => 0.9;

        /// <summary>Blocks away the preview stays visible. Past a vein's own size is pointless.</summary>
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

            if (mesh == null || mesh.Disposed) return;

            EntityPlayer player = capi.World.Player.Entity;
            Vec3d camera = player.CameraPos;

            IRenderAPI render = capi.Render;

            // Seeing the buried part is the whole point, so the depth buffer is ignored here -
            // and with it depth writing and face culling, exactly as ESP does.
            render.GlToggleBlend(true);
            render.GLDepthMask(false);
            render.GLDisableDepthTest();
            render.GlDisableCullFace();

            try
            {
                IShaderProgram shader = render.GetEngineShader(EnumShaderProgram.Wireframe);
                shader.Use();

                shader.Uniform("origin", Vec3f.Zero);
                shader.Uniform("colorIn", ColorUtil.WhiteArgbVec);
                shader.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);

                modelViewMatrix
                    .Identity()
                    .Set(render.CameraMatrixOrigin)
                    .Translate(
                        meshOrigin.X - camera.X,
                        meshOrigin.InternalY - camera.Y,
                        meshOrigin.Z - camera.Z);

                shader.UniformMatrix("modelViewMatrix", modelViewMatrix.Values);
                render.RenderMesh(mesh);

                shader.Stop();
            }
            finally
            {
                render.GlEnableCullFace();
                render.GLEnableDepthTest();
                render.GLDepthMask(true);
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

            Build();
        }

        /// <summary>
        /// Builds the vein as one mesh, with the faces between two of its own blocks left out -
        /// so it arrives as a single shape rather than a pile of cubes, the same as ESP.
        ///
        /// The block under the crosshair is not painted. The game outlines that one itself, and
        /// it is the one whose breaking cracks you want to see. Faces onto it are left out all
        /// the same: with no depth testing, what shows through that opening is the inside of the
        /// same red mass, so drawing them would only add triangles.
        /// </summary>
        private void Build()
        {
            Release();

            if (vein.Count < 2) return;

            meshOrigin = vein[0].Copy();

            var inVein = new HashSet<BlockPos>(vein);
            var data = new MeshData(24, 36, false, false, true, true);
            var size = new Vec3f(1, 1, 1);
            var neighbour = new BlockPos(meshOrigin.dimension);

            for (int i = 1; i < vein.Count; i++)
            {
                BlockPos pos = vein[i];

                var centre = new Vec3f(
                    pos.X - meshOrigin.X + 0.5f,
                    pos.Y - meshOrigin.Y + 0.5f,
                    pos.Z - meshOrigin.Z + 0.5f);

                foreach (BlockFacing face in BlockFacing.ALLFACES)
                {
                    neighbour.Set(pos.X + face.Normali.X, pos.Y + face.Normali.Y, pos.Z + face.Normali.Z);

                    if (inVein.Contains(neighbour)) continue;

                    ModelCubeUtilExt.AddFaceSkipTex(data, face, centre, size, EspPalette.VeinRed);
                }
            }

            if (data.VerticesCount > 0) mesh = capi.Render.UploadMesh(data);
            data.Dispose();
        }

        private void Forget()
        {
            if (fromPos == null) return;

            fromPos = null;
            vein.Clear();

            Release();
        }

        private void Release()
        {
            if (mesh == null) return;

            capi.Render.DeleteMesh(mesh);
            mesh = null;
        }

        public void Dispose()
        {
            Release();
        }
    }
}
