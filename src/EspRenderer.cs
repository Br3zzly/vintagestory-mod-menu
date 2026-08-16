using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace ModMenu
{
    /// <summary>
    /// Paints the chosen blocks and creatures as solid colour, through anything in the way.
    ///
    /// The work is arranged so that nothing ever scans the same chunk twice:
    ///
    ///   - chunks are read straight out of their own block array rather than through the block
    ///     accessor, which turns a delegate call and a chunk lookup per block into an array
    ///     index. Empty chunks are rejected outright.
    ///   - what is found is kept per chunk in a bitmap, so it survives moving around and
    ///     changing the range. Only changing what you are looking for throws it away.
    ///   - scanning runs on worker threads, one chunk each, nearest first, and every chunk gets
    ///     its own mesh as soon as it is read - so results appear as they are found instead of
    ///     after the whole radius has been walked.
    ///   - the index is kept current by listening for block changes rather than by rescanning,
    ///     so a mined block stops being outlined on the next frame at the cost of one dictionary
    ///     lookup per change, and only the chunk it happened in is ever read again.
    ///
    /// Interior faces are skipped when building a mesh: a face is only added where the block
    /// beyond it is not also a target, so a vein arrives as one shape rather than a pile of
    /// cubes, and the faces nobody could see are never built.
    /// </summary>
    public class EspRenderer : IRenderer
    {
        /// <summary>
        /// Which target a block belongs to, as a slot in <see cref="slotColours"/>. Slots only
        /// mean anything for the selection they were built from, which is exactly as long as the
        /// index lives - changing the targets throws both away together.
        /// </summary>
        private Dictionary<int, byte> slotByBlockId = new Dictionary<int, byte>();
        private int[] slotColours = new int[0];

        /// <summary>How often to look for chunks that have loaded since the last pass.</summary>
        private const long RescanIntervalMs = 750;

        /// <summary>
        /// Meshes uploaded per frame. Uploading is main-thread work, so it is spread out - the
        /// rest arrive over the next few frames rather than in one hitch.
        /// </summary>
        private const int MeshUploadsPerFrame = 8;

        /// <summary>
        /// Chunks re-read per frame, at most. Rationed for the same reason: reading one is a
        /// walk of its whole block array, and a busy moment can queue several at once.
        /// </summary>
        private const int RereadsPerFrame = 2;

        private readonly ICoreClientAPI capi;
        private readonly ModMenuConfig config;

        private readonly EspIndex index = new EspIndex();

        /// <summary>
        /// The other way of showing the same thing: hide the world instead of outlining what is
        /// in it. Driven from here because this is what knows which blocks are being tracked.
        /// </summary>
        private readonly TransparentWorld transparency;

        /// <summary>
        /// Block changes that arrived before their chunk was in the index, held until the scan
        /// that was reading it has finished. See <see cref="DrainPendingChanges"/>.
        /// </summary>
        private readonly ConcurrentQueue<BlockPos> pendingChanges = new ConcurrentQueue<BlockPos>();

        /// <summary>Chunks something changed in, waiting to be read again.</summary>
        private readonly Queue<EspChunk> toReread = new Queue<EspChunk>();

        private WireframeCube cube;
        private readonly Matrixf modelViewMatrix = new Matrixf();

        private HashSet<int> wantedBlockIds = new HashSet<int>();

        /// <summary>Creature code to the colour its target is drawn in.</summary>
        private Dictionary<string, Vec4f> wantedEntityColours = new Dictionary<string, Vec4f>();
        private string builtFrom;

        private long lastScanSweepMs;
        private volatile bool scanning;
        private CancellationTokenSource scanCancel;

        /// <summary>
        /// Whether the world is being hidden. Asked by the mod system, which turns fullbright on
        /// for as long as it lasts: with the world gone there is nothing to see down there but
        /// unlit blocks at the far end of black fog.
        /// </summary>
        public bool HidingWorld => transparency.Active;

        public double RenderOrder => 0.9;

        public int RenderRange => 128;

        public EspRenderer(ICoreClientAPI capi, ModMenuConfig config)
        {
            this.capi = capi;
            this.config = config;

            transparency = new TransparentWorld(capi);
            capi.Event.BlockChanged += OnBlockChanged;
        }

        public void OnRenderFrame(float dt, EnumRenderStage stage)
        {
            if (!config.Esp || config.EspTargets.Length == 0)
            {
                if (builtFrom != null) Forget();
                transparency.Apply(null);
                return;
            }

            EntityPlayer player = capi.World?.Player?.Entity;
            if (player == null) return;

            RebuildTargetsIfChanged();
            transparency.Apply(config.TransparentWorld ? slotByBlockId.Keys : null);

            if (config.TransparentWorld)
            {
                // Nothing is outlined while the world is hidden - the tracked blocks are the
                // only ones still drawn, and fullbright is what makes them readable at depth.
                // The index goes with it rather than being kept current for nothing, and the
                // scanning that fills it is the expensive half.
                DropBlockIndex();
            }
            else
            {
                StartScanIfDue(player.Pos.AsBlockPos);
                DrainPendingChanges();
                VerifyChunks();
                BuildSomeMeshes();
            }

            IRenderAPI render = capi.Render;

            // Seeing what is buried is the whole point, so the depth buffer is ignored here.
            // Depth writing goes off with it, or the overlay would hide the world behind it,
            // and culling goes off because standing inside a highlighted mass means looking at
            // the inside of its shell.
            render.GlToggleBlend(true);
            render.GLDepthMask(false);
            render.GLDisableDepthTest();
            render.GlDisableCullFace();

            try
            {
                DrawChunks(render, player);
                DrawEntities(player);
            }
            finally
            {
                render.GlEnableCullFace();
                render.GLEnableDepthTest();
                render.GLDepthMask(true);
            }
        }

        // ---- drawing ---------------------------------------------------------------

        private void DrawChunks(IRenderAPI render, EntityPlayer player)
        {
            List<EspChunk> chunks = index.NonEmpty();
            if (chunks.Count == 0) return;

            // Its own fragment stage is just "output the vertex colour", which is as true for
            // solid faces as it is for the lines it is named after.
            IShaderProgram shader = render.GetEngineShader(EnumShaderProgram.Wireframe);
            shader.Use();

            shader.Uniform("origin", Vec3f.Zero);
            shader.Uniform("colorIn", ColorUtil.WhiteArgbVec);
            shader.UniformMatrix("projectionMatrix", render.CurrentProjectionMatrix);

            Vec3d camera = player.CameraPos;

            foreach (EspChunk chunk in chunks)
            {
                if (chunk.Mesh == null || chunk.Mesh.Disposed) continue;

                modelViewMatrix
                    .Identity()
                    .Set(render.CameraMatrixOrigin)
                    .Translate(
                        chunk.ChunkX * EspChunk.Size - camera.X,
                        chunk.ChunkY * EspChunk.Size - camera.Y,
                        chunk.ChunkZ * EspChunk.Size - camera.Z);

                shader.UniformMatrix("modelViewMatrix", modelViewMatrix.Values);
                render.RenderMesh(chunk.Mesh);
            }

            shader.Stop();
        }

        private void DrawEntities(EntityPlayer player)
        {
            if (wantedEntityColours.Count == 0) return;
            if (cube == null) cube = WireframeCube.CreateUnitCube(capi);

            Vec3d offset = player.CameraPosOffset;
            float thickness = 1.6f * ClientSettings.Wireframethickness;
            double rangeSq = config.EspRange * (double)config.EspRange;

            foreach (Entity entity in capi.World.LoadedEntities.Values)
            {
                if (entity == null || entity.EntityId == player.EntityId) continue;
                if (entity.Code == null) continue;
                if (!wantedEntityColours.TryGetValue(entity.Code.ToString(), out Vec4f colour)) continue;
                if (entity.Pos.SquareDistanceTo(player.Pos.XYZ) > rangeSq) continue;

                Cuboidf box = entity.SelectionBox ?? new Cuboidf(-0.5f, 0, -0.5f, 0.5f, 1f, 0.5f);

                cube.Render(capi,
                    entity.Pos.X - box.XSize / 2 + offset.X,
                    entity.Pos.InternalY + offset.Y,
                    entity.Pos.Z - box.ZSize / 2 + offset.Z,
                    box.XSize, box.YSize, box.ZSize,
                    thickness, colour);
            }
        }

        // ---- scanning --------------------------------------------------------------

        /// <summary>
        /// Looks for chunks in range that have not been read yet and reads them on worker
        /// threads. Cheap when there is nothing new: the index answers "already done" without
        /// touching the world.
        /// </summary>
        private void StartScanIfDue(BlockPos centre)
        {
            if (scanning || slotByBlockId.Count == 0) return;

            long now = capi.World.ElapsedMilliseconds;
            if (now - lastScanSweepMs < RescanIntervalMs) return;
            lastScanSweepMs = now;

            int chunkRadius = config.EspRange / EspChunk.Size + 1;
            int cx = centre.X / EspChunk.Size;
            int cy = centre.Y / EspChunk.Size;
            int cz = centre.Z / EspChunk.Size;

            foreach (EspChunk gone in index.DropBeyond(cx, cy, cz, chunkRadius + 2)) Delete(gone);

            List<Vec3i> todo = PendingChunks(cx, cy, cz, chunkRadius);
            if (todo.Count == 0) return;

            scanning = true;
            scanCancel = new CancellationTokenSource();
            CancellationToken token = scanCancel.Token;

            IBlockAccessor accessor = capi.World.BlockAccessor;
            Dictionary<int, byte> wanted = slotByBlockId;

            Task.Run(() =>
            {
                try
                {
                    // One chunk per worker, so no two threads ever touch the same chunk's data.
                    // Half the cores, leaving the rest for the frame that is still being drawn.
                    var options = new ParallelOptions
                    {
                        MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount / 2),
                        CancellationToken = token
                    };

                    Parallel.ForEach(todo, options, coord =>
                    {
                        EspChunk scanned = ScanChunk(accessor, coord, wanted);

                        // Chunks already being read when the selection changed describe the old
                        // one, so they are dropped rather than filed.
                        if (scanned != null && !token.IsCancellationRequested) index.Put(scanned);
                    });
                }
                catch (OperationCanceledException)
                {
                    // Targets changed or the world went away mid-scan.
                }
                catch (Exception)
                {
                    // Chunks can unload underneath a scan. Losing a pass is not worth a crash.
                }
                finally
                {
                    scanning = false;
                }
            });
        }

        /// <summary>Chunks in range that have not been read, nearest first.</summary>
        private List<Vec3i> PendingChunks(int cx, int cy, int cz, int chunkRadius)
        {
            var todo = new List<Vec3i>();

            for (int dx = -chunkRadius; dx <= chunkRadius; dx++)
            for (int dy = -chunkRadius; dy <= chunkRadius; dy++)
            for (int dz = -chunkRadius; dz <= chunkRadius; dz++)
            {
                int x = cx + dx, y = cy + dy, z = cz + dz;
                if (y < 0) continue;
                if (index.IsScanned(x, y, z)) continue;

                todo.Add(new Vec3i(x, y, z));
            }

            // Nearest first, so what is around you appears while the far edge is still coming.
            todo.Sort((a, b) =>
                ((a.X - cx) * (a.X - cx) + (a.Y - cy) * (a.Y - cy) + (a.Z - cz) * (a.Z - cz))
                .CompareTo((b.X - cx) * (b.X - cx) + (b.Y - cy) * (b.Y - cy) + (b.Z - cz) * (b.Z - cz)));

            return todo;
        }

        /// <summary>
        /// Reads one chunk's blocks straight out of its own array. Unpack_ReadOnly is the
        /// documented way to do this in bulk without promising to write anything back.
        /// </summary>
        private static EspChunk ScanChunk(IBlockAccessor accessor, Vec3i coord,
            Dictionary<int, byte> wanted)
        {
            IWorldChunk chunk = accessor.GetChunk(coord.X, coord.Y, coord.Z);
            if (chunk == null) return null;

            var found = new EspChunk(coord.X, coord.Y, coord.Z);
            if (chunk.Empty) return found;

            chunk.Unpack_ReadOnly();
            IChunkBlocks blocks = chunk.Data;
            if (blocks == null) return found;

            int length = Math.Min(blocks.Length, EspChunk.BlocksPerChunk);

            for (int i = 0; i < length; i++)
            {
                int id = blocks[i];
                if (id != 0 && wanted.TryGetValue(id, out byte slot)) found.Add(i, slot);
            }

            return found;
        }

        // ---- keeping up with the world ---------------------------------------------

        /// <summary>
        /// A block somewhere changed. Nothing is rescanned and nothing is searched: the chunk
        /// holding that one position is found by key and whether it was being outlined is a bit
        /// test. Blocks that were never outlined - which is nearly every block anyone breaks -
        /// cost one dictionary lookup and one hash lookup, and stop there.
        ///
        /// The game raises this after the world has been updated, and for every route a block
        /// can change by: broken or placed here, and blocks or bulk updates sent by the server.
        /// </summary>
        private void OnBlockChanged(BlockPos pos, Block oldBlock)
        {
            if (pos == null || pos.dimension != 0) return;
            if (!config.Esp || config.TransparentWorld || slotByBlockId.Count == 0) return;

            // A chunk being scanned right now is not in the index yet, and the worker reading it
            // may have passed this position moments before the change landed. Hold it until the
            // scan is done rather than let a stale hit through. The position itself is a shared
            // object the game reuses, so what gets held is a copy.
            if (!ApplyBlockChange(pos) && scanning) pendingChanges.Enqueue(pos.Copy());
        }

        /// <summary>
        /// Replays changes that arrived mid-scan, once that scan has finished. Applying compares
        /// against what the world says now, so replaying one the scan already saw does nothing.
        /// </summary>
        private void DrainPendingChanges()
        {
            if (scanning) return;

            while (pendingChanges.TryDequeue(out BlockPos pos)) ApplyBlockChange(pos);
        }

        /// <summary>
        /// Brings one position in line with the block that is there now, and marks whatever has
        /// to be redrawn. Runs on the main thread, which is the only thread that touches a
        /// chunk's hits once it is in the index.
        /// </summary>
        /// <returns>False if that chunk has not been indexed, so nothing could be decided.</returns>
        private bool ApplyBlockChange(BlockPos pos)
        {
            const int size = EspChunk.Size;

            int cx = pos.X / size, cy = pos.Y / size, cz = pos.Z / size;

            EspChunk chunk = index.Get(cx, cy, cz);
            if (chunk == null) return false;

            int x = pos.X - cx * size, y = pos.Y - cy * size, z = pos.Z - cz * size;
            int local = EspChunk.Index3d(x, y, z);

            // Whatever went with it - the grass that pops when the soil under it goes, the sand
            // that starts falling, the leaves that decay - is removed without any change of its
            // own being announced, because a block the client takes out itself is not reported
            // the way a block you break is. So the chunk is read again shortly after, which
            // catches all of it whatever the cause. Chunks holding nothing tracked are skipped.
            ScheduleReread(chunk);
            ScheduleRereadAcrossBoundary(cx, cy, cz, x, y, z);

            bool was = chunk.Has(local);
            bool wanted = slotByBlockId.TryGetValue(
                capi.World.BlockAccessor.GetBlockId(pos), out byte slot);
            if (was == wanted) return true;

            if (wanted) chunk.Add(local, slot);
            else chunk.Remove(local);

            // An emptied chunk is no longer drawn or rebuilt, so its mesh goes back now.
            if (chunk.IsEmpty) Delete(chunk);
            else chunk.MeshDirty = true;

            // On a chunk boundary the neighbour's shell changes with it: the face it was hiding
            // is now exposed, or the face it was showing is now covered.
            if (x == 0) index.MarkDirty(cx - 1, cy, cz);
            else if (x == size - 1) index.MarkDirty(cx + 1, cy, cz);

            if (y == 0) index.MarkDirty(cx, cy - 1, cz);
            else if (y == size - 1) index.MarkDirty(cx, cy + 1, cz);

            if (z == 0) index.MarkDirty(cx, cy, cz - 1);
            else if (z == size - 1) index.MarkDirty(cx, cy, cz + 1);

            return true;
        }

        /// <summary>
        /// Queues a chunk to be read again. Chunks with nothing tracked in them are ignored:
        /// nothing in them can have gone stale, and a placed block is caught the moment it
        /// happens by the test above.
        /// </summary>
        private void ScheduleReread(EspChunk chunk)
        {
            if (chunk == null || chunk.IsEmpty || chunk.RereadPending) return;

            chunk.RereadPending = true;
            toReread.Enqueue(chunk);
        }

        /// <summary>
        /// Queues the chunk next door as well when the change sat against a shared boundary -
        /// grass at the bottom of one chunk stands on soil in the one below.
        /// </summary>
        private void ScheduleRereadAcrossBoundary(int cx, int cy, int cz, int x, int y, int z)
        {
            const int last = EspChunk.Size - 1;

            if (x == 0) ScheduleReread(index.Get(cx - 1, cy, cz));
            else if (x == last) ScheduleReread(index.Get(cx + 1, cy, cz));

            if (y == 0) ScheduleReread(index.Get(cx, cy - 1, cz));
            else if (y == last) ScheduleReread(index.Get(cx, cy + 1, cz));

            if (z == 0) ScheduleReread(index.Get(cx, cy, cz - 1));
            else if (z == last) ScheduleReread(index.Get(cx, cy, cz + 1));
        }

        /// <summary>
        /// Reads a few queued chunks again and takes on whatever they hold now. A read is one
        /// walk of a chunk's own block array - the same work a first scan does, and far less
        /// than rebuilding the mesh that follows it - but it only happens for chunks something
        /// actually changed in, and a chunk queues once however many blocks changed in it.
        /// </summary>
        private void VerifyChunks()
        {
            IBlockAccessor accessor = capi.World.BlockAccessor;

            for (int done = 0; done < RereadsPerFrame && toReread.Count > 0; done++)
            {
                EspChunk chunk = toReread.Dequeue();
                chunk.RereadPending = false;

                EspChunk fresh = ScanChunk(accessor,
                    new Vec3i(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ), slotByBlockId);

                // Unloaded since it was queued. DropBeyond will get to it.
                if (fresh == null) continue;

                if (!chunk.AdoptIfDifferent(fresh)) continue;

                if (chunk.IsEmpty) Delete(chunk);
                else chunk.MeshDirty = true;

                index.MarkNeighboursDirty(chunk);
            }
        }

        // ---- meshes ----------------------------------------------------------------

        /// <summary>
        /// Builds a few chunk meshes per frame. Uploading has to happen here rather than on a
        /// worker, so it is rationed - the rest follow over the next frames.
        /// </summary>
        private void BuildSomeMeshes()
        {
            int built = 0;

            foreach (EspChunk chunk in index.NonEmpty())
            {
                if (!chunk.MeshDirty) continue;
                if (built++ >= MeshUploadsPerFrame) return;

                BuildMesh(chunk);
            }
        }

        private void BuildMesh(EspChunk chunk)
        {
            chunk.MeshDirty = false;

            if (chunk.Mesh != null)
            {
                capi.Render.DeleteMesh(chunk.Mesh);
                chunk.Mesh = null;
            }

            var mesh = new MeshData(24, 36, false, false, true, true);
            var size = new Vec3f(1, 1, 1);

            for (int h = 0; h < chunk.Hits.Count; h++)
            {
                int i = chunk.Hits[h];

                int x = i % EspChunk.Size;
                int z = (i / EspChunk.Size) % EspChunk.Size;
                int y = i / (EspChunk.Size * EspChunk.Size);

                // One mesh still holds the whole chunk whatever it contains - the colour rides
                // on the vertices, so several targets in one chunk cost no extra draw.
                int colour = ColourOf(chunk.HitSlots[h]);

                var centre = new Vec3f(x + 0.5f, y + 0.5f, z + 0.5f);

                foreach (BlockFacing face in BlockFacing.ALLFACES)
                {
                    if (IsTarget(chunk, x + face.Normali.X, y + face.Normali.Y, z + face.Normali.Z)) continue;

                    ModelCubeUtilExt.AddFaceSkipTex(mesh, face, centre, size, colour);
                }
            }

            if (mesh.VerticesCount > 0) chunk.Mesh = capi.Render.UploadMesh(mesh);
            mesh.Dispose();
        }

        /// <summary>
        /// Whether a position, given in one chunk's local coordinates but possibly just outside
        /// it, is also a target. Inside the chunk this is a bit test; across the boundary it
        /// asks the neighbouring chunk, which is why indexing one marks its neighbours dirty.
        /// </summary>
        private bool IsTarget(EspChunk chunk, int x, int y, int z)
        {
            const int size = EspChunk.Size;

            if (x >= 0 && y >= 0 && z >= 0 && x < size && y < size && z < size)
            {
                return chunk.Has(EspChunk.Index3d(x, y, z));
            }

            int cx = chunk.ChunkX + (x < 0 ? -1 : x >= size ? 1 : 0);
            int cy = chunk.ChunkY + (y < 0 ? -1 : y >= size ? 1 : 0);
            int cz = chunk.ChunkZ + (z < 0 ? -1 : z >= size ? 1 : 0);

            EspChunk neighbour = index.Get(cx, cy, cz);
            if (neighbour == null || neighbour.IsEmpty) return false;

            return neighbour.Has(EspChunk.Index3d(
                (x + size) % size, (y + size) % size, (z + size) % size));
        }

        // ---- lifecycle -------------------------------------------------------------

        /// <summary>
        /// Works out what is being looked for, and in what colour.
        ///
        /// The signature covers the colours as well as the names, so recolouring a target is
        /// noticed - but it costs a full rescan, which recolouring alone does not need. It is a
        /// button nobody presses in a loop, and the alternative is a second path that only
        /// rebuilds meshes, for a case that happens once.
        /// </summary>
        private void RebuildTargetsIfChanged()
        {
            string signature = string.Join("\n",
                Array.ConvertAll(config.EspTargets, g => g?.Name + "\t" + g?.Color));
            if (signature == builtFrom) return;

            Forget();
            builtFrom = signature;

            var slots = new Dictionary<int, byte>();
            var colours = new List<int>();
            var entityColours = new Dictionary<string, Vec4f>();

            foreach (EspGroup group in config.EspTargets)
            {
                if (group?.Codes == null) continue;

                // One slot per target, however many block codes that target covers - every host
                // rock variant of native copper is the same thing in the same colour.
                var slot = (byte)colours.Count;
                colours.Add(group.Color);

                foreach (string code in group.Codes)
                {
                    if (string.IsNullOrEmpty(code)) continue;

                    if (group.IsBlock)
                    {
                        Block block = capi.World.GetBlock(new AssetLocation(code));
                        if (block != null) slots[block.Id] = slot;
                    }
                    else
                    {
                        entityColours[code] = ToVec4f(group.Color);
                    }
                }
            }

            slotByBlockId = slots;
            slotColours = colours.ToArray();
            wantedEntityColours = entityColours;
            lastScanSweepMs = 0;
        }

        private int ColourOf(byte slot)
        {
            return slot < slotColours.Length ? slotColours[slot] : EspPalette.At(slot);
        }

        /// <summary>
        /// The same colour as the wireframe cube wants it. Pulled apart by hand because the
        /// stored form is packed for a mesh - red in the lowest byte - which is the opposite way
        /// round from what the ColorUtil converters assume.
        /// </summary>
        private static Vec4f ToVec4f(int colour)
        {
            return new Vec4f(
                (colour & 0xFF) / 255f,
                ((colour >> 8) & 0xFF) / 255f,
                ((colour >> 16) & 0xFF) / 255f,
                ((colour >> 24) & 0xFF) / 255f);
        }

        /// <summary>Throws the index away - everything in it describes the old selection.</summary>
        private void Forget()
        {
            builtFrom = null;
            DropBlockIndex();
        }

        /// <summary>
        /// Drops everything scanned so far and stops the scan that is filling it, without
        /// forgetting what is being tracked. Costs almost nothing once there is nothing left,
        /// which is what makes it safe to call on every frame.
        /// </summary>
        private void DropBlockIndex()
        {
            scanCancel?.Cancel();

            // Held changes and queued re-reads describe an index that no longer exists.
            pendingChanges.Clear();
            toReread.Clear();

            if (index.Count == 0) return;

            foreach (EspChunk chunk in index.Clear()) Delete(chunk);
        }

        private void Delete(EspChunk chunk)
        {
            if (chunk?.Mesh == null) return;

            capi.Render.DeleteMesh(chunk.Mesh);
            chunk.Mesh = null;
        }

        public void Dispose()
        {
            capi.Event.BlockChanged -= OnBlockChanged;

            // Leaving with the world hidden is not a reason to keep it hidden.
            transparency.Apply(null);

            Forget();

            cube?.Dispose();
            cube = null;
        }
    }
}
