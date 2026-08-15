using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.MathTools;
using Vintagestory.Client.NoObf;

namespace ModMenu
{
    /// <summary>
    /// Breaks the rest of a vein once the player breaks one block of it.
    ///
    /// Client side, and it works on any server, because breaking a block is a client-driven
    /// action: the client decides a block gave way, breaks it in its own copy of the world and
    /// reports that upward (ClientMain.OnPlayerTryDestroyBlock). Every extra block here goes
    /// through that same method, so the server sees an ordinary run of mining.
    ///
    /// Which is exactly why it has to be paced - see <see cref="MinIntervalMs"/>.
    /// </summary>
    public class VeinMiner
    {
        /// <summary>
        /// The Harmony patch that spots a block break is static and process-wide, so it reaches
        /// the live instance through here. Null until a world is running.
        /// </summary>
        public static VeinMiner Instance;

        // ---- pacing ----------------------------------------------------------------
        //
        // A server can ban for breaking blocks too quickly, and it is not a throttle - it is a
        // ban. PlayerAntiAbuseMonitor keeps a ring buffer of every block a player breaks and
        // scans it once a second: if any AntiAbuseTriggerOnBlockBreakCount consecutive breaks
        // fall inside AntiAbuseTriggerOnDurationMs it calls BanPlayer, no warning and no
        // second chance. The defaults below are 40 breaks in 2000ms, banned for 14 days.
        //
        // It is off in the default server config (AntiAbuse = EnumProtectionLevel.Off) and the
        // setting is never sent to clients, so there is no way to look before leaping: with the
        // ban-safe toggle on, the queue paces itself as though every server had it switched on.
        //
        // The same AntiAbuse setting also gates the server's reach check on a break
        // (TryModifyBlockInWorld: IsInInteractionRangeOf(pos, 0.7f)), which is worth knowing
        // before trying to beat that check. Nothing on the client limits reach - breaks go
        // straight to OnPlayerTryDestroyBlock, no aiming involved - so a server either has
        // AntiAbuse off, and there is no reach limit to get around, or has it on, in which case
        // the unpaced mode is a ban regardless of where the blocks are.

        /// <summary>Server default for AntiAbuseTriggerOnBlockBreakCount.</summary>
        private const int BurstCount = 40;

        /// <summary>Server default for AntiAbuseTriggerOnDurationMs.</summary>
        private const int BurstWindowMs = 2000;

        /// <summary>
        /// How much wider than the server's own window we hold ourselves to. The server stamps
        /// its arrival time, not ours, so a bunch of packets delivered together lands closer
        /// than it left; this is the room that gives.
        /// </summary>
        private const double BurstSafety = 1.25;

        /// <summary>
        /// Shortest gap between two breaks. Spacing <see cref="BurstCount"/> of them this far
        /// apart spans the stretched window, so a vein on its own can never trip the check.
        /// </summary>
        private static readonly long MinIntervalMs =
            (long)Math.Ceiling(BurstWindowMs * BurstSafety / (BurstCount - 1));

        private readonly ICoreClientAPI capi;
        private readonly ModMenuConfig config;

        /// <summary>Positions still to break, and the block they were when the vein was found.</summary>
        private readonly Queue<BlockPos> queue = new Queue<BlockPos>();
        private int queuedBlockId;

        /// <summary>
        /// When the last few breaks went out, longest ago first, capped at one short of the
        /// count that trips the server. Manual mining is in here too - the patch that feeds
        /// this sees every block the player breaks, not only the ones we break ourselves.
        /// </summary>
        private readonly Queue<long> recentBreaks = new Queue<long>();
        private long lastBreakMs = long.MinValue;

        /// <summary>Set while we are the ones calling into the break method, to not re-enter.</summary>
        private bool breaking;

        public VeinMiner(ICoreClientAPI capi, ModMenuConfig config)
        {
            this.capi = capi;
            this.config = config;
        }

        /// <summary>
        /// Every block the player breaks arrives here, ours and theirs alike. Theirs may start
        /// a vein; ours only ever count towards the pacing.
        /// </summary>
        public void OnPlayerBrokeBlock(BlockPos pos, int blockId)
        {
            RecordBreak(capi.World.ElapsedMilliseconds);

            if (breaking) return;
            if (!config.VeinMiner || blockId <= 0 || pos == null) return;

            // A vein already draining owns the queue. Starting another one here would break
            // more than the limit allows per mine, so the block just mined stays a single one.
            if (queue.Count > 0) return;

            int limit = config.VeinMinerLimit;
            if (limit <= 1) return; // the block the player hit is the whole allowance

            // Skipping the first, which is the block the player already broke.
            List<BlockPos> vein = FindVein(capi.World.BlockAccessor, pos, blockId, limit);
            for (int i = 1; i < vein.Count; i++) queue.Enqueue(vein[i]);

            queuedBlockId = blockId;
        }

        /// <summary>
        /// Walks outwards from a block, collecting connected blocks of the same kind, up to
        /// <paramref name="limit"/> of them counting the one started from - which comes back
        /// first in the list. All 26 neighbours count as connected, since ore veins run
        /// diagonally as often as not and a 6-way search leaves half a vein in the wall.
        ///
        /// The preview outlines draw whatever this returns, so what is highlighted and what
        /// gets broken cannot drift apart.
        /// </summary>
        public static List<BlockPos> FindVein(IBlockAccessor ba, BlockPos origin, int blockId, int limit)
        {
            if (ba == null) return new List<BlockPos> { origin };

            return FindVein(pos => ba.GetBlock(pos)?.Id ?? 0, origin, blockId, limit);
        }

        /// <summary>
        /// The search itself, over a plain "what is at this position" lookup rather than a
        /// world, so it can be exercised without one.
        /// </summary>
        public static List<BlockPos> FindVein(System.Func<BlockPos, int> blockIdAt, BlockPos origin, int blockId, int limit)
        {
            var found = new List<BlockPos> { origin };
            if (blockIdAt == null || origin == null || blockId <= 0 || limit <= 1) return found;

            var seen = new HashSet<BlockPos> { origin };
            var frontier = new Queue<BlockPos>();
            frontier.Enqueue(origin);

            while (frontier.Count > 0 && found.Count < limit)
            {
                BlockPos at = frontier.Dequeue();

                for (int dx = -1; dx <= 1 && found.Count < limit; dx++)
                for (int dy = -1; dy <= 1 && found.Count < limit; dy++)
                for (int dz = -1; dz <= 1 && found.Count < limit; dz++)
                {
                    if (dx == 0 && dy == 0 && dz == 0) continue;

                    BlockPos next = at.AddCopy(dx, dy, dz);
                    if (!seen.Add(next)) continue;
                    if (blockIdAt(next) != blockId) continue;

                    found.Add(next);
                    frontier.Enqueue(next);
                }
            }

            return found;
        }

        /// <summary>Releases queued blocks, paced or not depending on the ban-safe toggle.</summary>
        public void OnTick(float dt)
        {
            if (queue.Count == 0) return;

            // Switching the toggle off mid-vein stops it there rather than finishing the job.
            if (!config.VeinMiner)
            {
                queue.Clear();
                return;
            }

            // Pacing off: the whole vein goes in a single pass, however many blocks that is.
            // On a server running the anti abuse monitor this is the shape it bans for.
            if (!config.VeinMinerBanSafe)
            {
                while (queue.Count > 0) Break(queue.Dequeue());
                return;
            }

            long now = capi.World.ElapsedMilliseconds;
            if (!MayBreakNow(now)) return;

            Break(queue.Dequeue());
        }

        private bool MayBreakNow(long now)
        {
            if (now - lastBreakMs < MinIntervalMs) return false;

            // The server's own test, run one break ahead of it: if the breaks we already know
            // about plus this one would put BurstCount of them inside the window, hold off.
            return recentBreaks.Count < BurstCount - 1
                || now - recentBreaks.Peek() > BurstWindowMs * BurstSafety;
        }

        private void RecordBreak(long now)
        {
            recentBreaks.Enqueue(now);
            while (recentBreaks.Count > BurstCount - 1) recentBreaks.Dequeue();
            lastBreakMs = now;
        }

        private void Break(BlockPos pos)
        {
            // The world moved on while this sat in the queue: somebody else mined it, it fell,
            // or the server never agreed it was gone in the first place.
            Block block = capi.World.BlockAccessor.GetBlock(pos);
            if (block == null || block.Id != queuedBlockId) return;

            var client = capi.World as ClientMain;
            if (client == null) return;

            var selection = new BlockSelection(pos.Copy(), BlockFacing.UP, block);

            // The block being aimed at has to be swapped for the one being broken, and not just
            // for tidiness: ClientMain.tryAccess takes a BlockSelection parameter and then
            // ignores it, testing the ambient ClientMain.BlockSelection instead. Mining by hand
            // never notices, since the two are the same block. Here they are not - and once the
            // ore that was under the crosshair is gone, the aim hits nothing at all and that
            // field is null, which is a NullReferenceException inside the land claim check.
            //
            // Setting it also points the claim check at the block actually being broken, which
            // is the block that ought to be checked, and gives OnBlockBrokenWith the same
            // selection through both routes it can read one from.
            BlockSelection aimedAt = client.BlockSelection;
            client.BlockSelection = selection;

            // The setter quietly does nothing while the player entity is missing, which would
            // leave the same null in place that this is here to avoid.
            if (client.BlockSelection != selection)
            {
                client.BlockSelection = aimedAt;
                return;
            }

            breaking = true;
            try
            {
                client.OnPlayerTryDestroyBlock(selection);
            }
            finally
            {
                client.BlockSelection = aimedAt;
                breaking = false;
            }
        }
    }
}
