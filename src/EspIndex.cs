using System;
using System.Collections.Generic;
using Vintagestory.API.Config;

namespace ModMenu
{
    /// <summary>
    /// What one chunk contains, as a bitmap plus the list of set bits.
    ///
    /// A chunk is 32x32x32, so a bit per block is 4KB - small enough to keep for every chunk in
    /// range, and it makes "is the block next door also a target" a bit test instead of a hash
    /// lookup on an allocated position object. The mesh builder asks that question six times
    /// per hit, so it is the difference between a few instructions and a few hundred.
    /// </summary>
    public class EspChunk
    {
        public const int Size = GlobalConstants.ChunkSize;
        public const int BlocksPerChunk = Size * Size * Size;

        public readonly int ChunkX, ChunkY, ChunkZ;

        /// <summary>One bit per block position in the chunk.</summary>
        private readonly ulong[] bits = new ulong[BlocksPerChunk / 64];

        /// <summary>The set bits again, to iterate without walking all 32768.</summary>
        public readonly List<int> Hits = new List<int>();

        /// <summary>
        /// Which target each hit belongs to, as a slot in the renderer's colour table, running
        /// alongside <see cref="Hits"/>. A byte rather than the colour itself: there are at most
        /// twenty targets, and a chunk of common stone can hold thirty thousand hits.
        /// </summary>
        public readonly List<byte> HitSlots = new List<byte>();

        /// <summary>Its mesh needs rebuilding - newly scanned, or a neighbour changed.</summary>
        public bool MeshDirty = true;

        /// <summary>
        /// It is already queued to be read again because something in it changed. Kept on the
        /// chunk so a vein miner taking four hundred blocks out of it queues one re-read.
        /// </summary>
        public bool RereadPending;

        /// <summary>The built shape, or null when there is nothing to draw.</summary>
        public Vintagestory.API.Client.MeshRef Mesh;

        public EspChunk(int chunkX, int chunkY, int chunkZ)
        {
            ChunkX = chunkX;
            ChunkY = chunkY;
            ChunkZ = chunkZ;
        }

        public bool IsEmpty => Hits.Count == 0;

        public void Add(int index3d, byte slot)
        {
            bits[index3d >> 6] |= 1UL << (index3d & 63);
            Hits.Add(index3d);
            HitSlots.Add(slot);
        }

        /// <summary>
        /// Takes one block back out - it was mined, or replaced by something else. The list
        /// search is by value, not by position, and only ever runs when a block that was
        /// actually being outlined changed.
        /// </summary>
        public void Remove(int index3d)
        {
            bits[index3d >> 6] &= ~(1UL << (index3d & 63));

            int at = Hits.IndexOf(index3d);
            if (at < 0) return;

            Hits.RemoveAt(at);
            HitSlots.RemoveAt(at);
        }

        public bool Has(int index3d) => (bits[index3d >> 6] & (1UL << (index3d & 63))) != 0;

        /// <summary>
        /// Takes over what a fresh read of the same chunk found, and says whether that differs
        /// from what was held. The comparison is 512 word tests, which is nothing next to
        /// rebuilding a mesh, so an unchanged chunk costs only the read that produced it.
        /// </summary>
        public bool AdoptIfDifferent(EspChunk reread)
        {
            if (Same(reread)) return false;

            Array.Copy(reread.bits, bits, bits.Length);

            Hits.Clear();
            Hits.AddRange(reread.Hits);

            HitSlots.Clear();
            HitSlots.AddRange(reread.HitSlots);

            return true;
        }

        /// <summary>
        /// Whether a fresh read found exactly what is held. The slots are compared as well as
        /// the bitmap: one target can be swapped for another in place, which leaves the same
        /// blocks lit in a different colour.
        /// </summary>
        private bool Same(EspChunk reread)
        {
            for (int i = 0; i < bits.Length; i++)
            {
                if (bits[i] != reread.bits[i]) return false;
            }

            if (HitSlots.Count != reread.HitSlots.Count) return false;

            for (int i = 0; i < HitSlots.Count; i++)
            {
                if (HitSlots[i] != reread.HitSlots[i]) return false;
            }

            return true;
        }

        /// <summary>Block index within a chunk, in the layout the chunk itself uses.</summary>
        public static int Index3d(int x, int y, int z) => (y * Size + z) * Size + x;
    }

    /// <summary>
    /// Every chunk scanned so far, and which ones still need scanning.
    ///
    /// The point of keeping this is that a chunk only ever gets read once. Moving around scans
    /// whatever is newly loaded and nothing else; changing the range just draws more or less of
    /// what is already known. Only changing what you are looking for throws it away.
    /// </summary>
    public class EspIndex
    {
        private readonly Dictionary<long, EspChunk> chunks = new Dictionary<long, EspChunk>();
        private readonly object sync = new object();

        public static long KeyOf(int cx, int cy, int cz)
        {
            // Chunk coordinates are well inside 21 bits either side of zero.
            return ((long)(cx & 0x1FFFFF) << 42) | ((long)(cy & 0x1FFFFF) << 21) | (long)(cz & 0x1FFFFF);
        }

        /// <summary>How many chunks are held, including the ones that turned out to be empty.</summary>
        public int Count
        {
            get { lock (sync) return chunks.Count; }
        }

        public bool IsScanned(int cx, int cy, int cz)
        {
            lock (sync) return chunks.ContainsKey(KeyOf(cx, cy, cz));
        }

        public EspChunk Get(int cx, int cy, int cz)
        {
            lock (sync)
            {
                chunks.TryGetValue(KeyOf(cx, cy, cz), out EspChunk chunk);
                return chunk;
            }
        }

        /// <summary>
        /// Records a scanned chunk and marks its neighbours for a mesh rebuild: a face on the
        /// shared boundary is only correct once both sides are known.
        /// </summary>
        public void Put(EspChunk chunk)
        {
            lock (sync)
            {
                chunks[KeyOf(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ)] = chunk;

                if (!chunk.IsEmpty) MarkNeighboursDirty(chunk);
            }
        }

        /// <summary>
        /// Marks the six chunks around one for a mesh rebuild. Their shells meet along shared
        /// faces, so what they draw depends on what this one holds.
        /// </summary>
        public void MarkNeighboursDirty(EspChunk chunk)
        {
            MarkDirty(chunk.ChunkX - 1, chunk.ChunkY, chunk.ChunkZ);
            MarkDirty(chunk.ChunkX + 1, chunk.ChunkY, chunk.ChunkZ);
            MarkDirty(chunk.ChunkX, chunk.ChunkY - 1, chunk.ChunkZ);
            MarkDirty(chunk.ChunkX, chunk.ChunkY + 1, chunk.ChunkZ);
            MarkDirty(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ - 1);
            MarkDirty(chunk.ChunkX, chunk.ChunkY, chunk.ChunkZ + 1);
        }

        /// <summary>
        /// Marks a chunk for a mesh rebuild. Chunks that are unknown or empty are ignored:
        /// there is either nothing drawn for them, or it will be built when they are scanned.
        /// </summary>
        public void MarkDirty(int cx, int cy, int cz)
        {
            lock (sync)
            {
                if (chunks.TryGetValue(KeyOf(cx, cy, cz), out EspChunk neighbour) && !neighbour.IsEmpty)
                {
                    neighbour.MeshDirty = true;
                }
            }
        }

        /// <summary>Chunks with something in them, for drawing and for mesh building.</summary>
        public List<EspChunk> NonEmpty()
        {
            var list = new List<EspChunk>();

            lock (sync)
            {
                foreach (EspChunk chunk in chunks.Values)
                {
                    if (!chunk.IsEmpty) list.Add(chunk);
                }
            }

            return list;
        }

        /// <summary>Forgets everything, handing back any meshes for the caller to delete.</summary>
        public List<EspChunk> Clear()
        {
            lock (sync)
            {
                var had = new List<EspChunk>(chunks.Values);
                chunks.Clear();
                return had;
            }
        }

        /// <summary>Drops chunks that have wandered out of range, to bound memory.</summary>
        public List<EspChunk> DropBeyond(int centreChunkX, int centreChunkY, int centreChunkZ, int chunkRadius)
        {
            var dropped = new List<EspChunk>();

            lock (sync)
            {
                var stale = new List<long>();

                foreach (var pair in chunks)
                {
                    EspChunk chunk = pair.Value;

                    if (Math.Abs(chunk.ChunkX - centreChunkX) > chunkRadius
                        || Math.Abs(chunk.ChunkY - centreChunkY) > chunkRadius
                        || Math.Abs(chunk.ChunkZ - centreChunkZ) > chunkRadius)
                    {
                        stale.Add(pair.Key);
                        dropped.Add(chunk);
                    }
                }

                foreach (long key in stale) chunks.Remove(key);
            }

            return dropped;
        }
    }
}
