using System;
using HarmonyLib;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.MathTools;
using Vintagestory.API.Server;
using Vintagestory.Client.NoObf;

namespace ModMenu
{
    public class ModMenuSystem : ModSystem
    {
        /// <summary>
        /// Versioned on purpose. The client and server only pair up a channel when the names
        /// match exactly - HandleChannelsPacket compares nothing else, not the registered
        /// message types - so bumping this is how the mod refuses to talk to a build it does
        /// not share a protocol with.
        ///
        /// Without that, a newer client sends feature ids an older server has no case for, the
        /// server throws inside its packet handler, and it disconnects the sender mid-join -
        /// which lands on the client as a crash in the texture atlas, nowhere near the cause.
        ///
        /// Mismatched builds now simply find no channel: the server-decided toggles grey out,
        /// which is the truth, and everything client-side carries on working. Bump this
        /// whenever the packet contract changes, which includes adding EnumFeature values.
        /// </summary>
        public const string ChannelName = "modmenu.v2";
        private const string HotkeyCode = "modmenu.toggle";
        private const string ConfigFile = "modmenu.json";
        private const string HarmonyId = "com.br3zzly.modmenu";

        private Harmony harmony;

        private ICoreClientAPI capi;
        private IClientNetworkChannel clientChannel;
        private ModMenuDialog dialog;
        private MapContextMenu mapMenu;
        private VeinMiner veinMiner;
        private VeinPreview veinPreview;

        public ModMenuConfig Config { get; private set; }

        /// <summary>
        /// Loads on both sides. Client-only installs are the normal case; if the same jar is
        /// dropped on a server the server half starts too and the toggles become authoritative.
        /// </summary>
        public override bool ShouldLoad(EnumAppSide side) => true;

        public override void Start(ICoreAPI api)
        {
            base.Start(api);

            // Harmony patches are process-wide. In singleplayer that is exactly what we want:
            // one patch pass covers the client and the internal server at the same time.
            if (!Harmony.HasAnyPatches(HarmonyId))
            {
                harmony = new Harmony(HarmonyId);
                harmony.PatchAll(System.Reflection.Assembly.GetExecutingAssembly());
            }

            api.Network
                .RegisterChannel(ChannelName)
                .RegisterMessageType<FeatureTogglePacket>();
        }

        public override void StartClientSide(ICoreClientAPI api)
        {
            capi = api;

            LoadConfig();

            // Undo the gamma an older build may still be holding. Fullbright itself is applied
            // on player join, once the world has sent its light tables down.
            RestoreGamma();

            clientChannel = api.Network.GetChannel(ChannelName) as IClientNetworkChannel;

            dialog = new ModMenuDialog(api, this);

            api.Input.RegisterHotKey(
                HotkeyCode,
                "Open Mod Menu",
                GlKeys.F2,
                HotkeyType.GUIOrOtherControls,
                altPressed: false,
                ctrlPressed: false,
                shiftPressed: false);

            api.Input.SetHotKeyHandler(HotkeyCode, _ =>
            {
                dialog.Toggle();
                return true;
            });

            // Re-assert movement flags every tick. The server pushes the player's real
            // gamemode flags down periodically and would otherwise clear flight mid-air.
            api.Event.RegisterGameTickListener(OnClientTick, 20);

            veinMiner = new VeinMiner(api, Config);
            VeinMiner.Instance = veinMiner;
            api.Event.RegisterGameTickListener(veinMiner.OnTick, 20);

            // Same stage the game outlines the aimed-at block at, so the preview sits in the
            // world the way the selection box does.
            veinPreview = new VeinPreview(api, Config);
            api.Event.RegisterRenderer(veinPreview, EnumRenderStage.AfterFinalComposition, "modmenu-vein");

            // The local player does not exist yet at StartClientSide, so push the saved
            // toggles into the shared state once the world is actually running.
            api.Event.PlayerJoin += OnLocalPlayerJoin;

            mapMenu = new MapContextMenu(api);
            Patches.MapRightClickPatch.Apply(
                harmony ?? new Harmony(HarmonyId), api, mapMenu, TeleportToAbsolute, Mod.Logger);
        }

        public override void StartServerSide(ICoreServerAPI api)
        {
            api.Network.GetChannel(ChannelName)
                .SetMessageHandler<FeatureTogglePacket>(OnTogglePacket);

            // Otherwise a player's toggles would stay active for whoever reuses the UID slot.
            api.Event.PlayerDisconnect += player => ModMenuState.Clear(player.PlayerUID);
        }

        private void OnTogglePacket(IServerPlayer fromPlayer, FeatureTogglePacket packet)
        {
            // Nothing a client sends may take this handler down. An unhandled exception in here
            // is not logged and shrugged off - the server disconnects the sender with "an action
            // you (or your client) did caused an unhandled exception", which lands as a crash on
            // a client still mid-join. Learned the hard way from a newer client sending feature
            // ids an older server had no case for.
            try
            {
                ModMenuState.Set(fromPlayer?.PlayerUID, packet.Feature, packet.Enabled);
            }
            catch (Exception e)
            {
                Mod.Logger.Warning("Ignoring a toggle from {0} that could not be applied: {1}",
                    fromPlayer?.PlayerName ?? "?", e.Message);
            }
        }

        private void OnLocalPlayerJoin(IClientPlayer player)
        {
            if (player.PlayerUID != capi.World.Player.PlayerUID) return;

            // Only what is actually on. The server starts a joining player with everything off
            // and clears them again on disconnect, so telling it about a disabled feature says
            // nothing it does not already assume - and every packet not sent is one that cannot
            // upset a server on a different build.
            SyncIfOn(EnumFeature.Invincible, Config.Invincible);
            SyncIfOn(EnumFeature.InstantMine, Config.InstantMine);
            SyncIfOn(EnumFeature.NoDurability, Config.NoDurabilityLoss);
            SyncIfOn(EnumFeature.DropsAtPlayer, Config.DropsAtPlayer);
            SyncIfOn(EnumFeature.OneHitKill, Config.OneHitKill);
            SyncIfOn(EnumFeature.NoHunger, Config.NoHunger);
            SyncIfOn(EnumFeature.FastPickup, Config.FastPickup);
            ApplyRangedAttack();

            // Only now do the world's light tables exist to be flattened.
            if (Config.Fullbright) ApplyFullbright(true);
        }

        /// <summary>Records a feature locally, and tells the server only when it is on.</summary>
        private void SyncIfOn(EnumFeature feature, bool enabled)
        {
            if (enabled) ApplyFeature(feature, true);
            else ModMenuState.Set(capi.World.Player?.PlayerUID, feature, false);
        }

        // ---- reach -----------------------------------------------------------------
        //
        // PickingRayUtil builds the aim ray exactly as long as player.WorldData.PickingRange,
        // so raising that is all the client needs to put distant blocks and entities under the
        // crosshair. Writing it sends nothing - the client's setter is a plain field write -
        // but the server pushes its own value back down on every mode change, so it has to be
        // re-asserted, the same as the flight flags.
        //
        // What the server then accepts is a separate question, and the answer differs by
        // action. Interacting with and breaking blocks is only range-checked when a server
        // turns anti abuse on, which is not the default. Attacking an entity is capped at
        // twice the weapon's attack range whatever the setting - see the README.

        /// <summary>
        /// The picking range as the game last set it, so the bonus is added to that rather than
        /// compounding on what we wrote last tick, and so it can be handed back.
        /// </summary>
        private float baseReach;

        /// <summary>What we last wrote, to tell our own value apart from the game's.</summary>
        private float? assertedReach;

        private void ApplyReach(IClientPlayer player)
        {
            if (Config.ReachBonus <= 0)
            {
                // Only put it back if we are the ones who moved it.
                if (assertedReach == null) return;

                player.WorldData.PickingRange = baseReach;
                assertedReach = null;
                return;
            }

            float current = player.WorldData.PickingRange;

            // A value we did not write is the game's own, and therefore the real base.
            if (assertedReach == null || Math.Abs(current - assertedReach.Value) > 0.0001f)
            {
                baseReach = current;
            }

            assertedReach = baseReach + Config.ReachBonus;
            player.WorldData.PickingRange = assertedReach.Value;
        }

        // ---- fullbright ------------------------------------------------------------
        //
        // Two halves, because the game lights terrain and everything else by different routes.
        //
        // Terrain light is baked into chunk meshes at tesselation time, through
        // ColorUtil.LightUtil.ToRgba - that is what FullbrightPatch answers. Meshes already
        // built keep the light they were built with, so the toggle has to ask for a redraw.
        //
        // Entities, items and held things are lit from the world's light level tables instead
        // (BlockAccessorReadLockfree.GetLightRGBs reads SunLightLevels and BlockLightLevels
        // directly), so those get flattened to full brightness as well. They arrive with the
        // world metadata rather than at startup, which is why this runs on player join.

        /// <summary>The light tables as the server sent them, to put back on the way out.</summary>
        private float[] savedBlockLightLevels;
        private float[] savedSunLightLevels;

        public void ApplyFullbright(bool enabled)
        {
            Patches.FullbrightPatch.Enabled = enabled;

            ApplyFullbrightEntityLight(enabled);

            // Light is baked into the chunk meshes, so nothing on screen changes until they are
            // rebuilt. Null before a world is up, where there is nothing to redraw anyway.
            (capi?.World as ClientMain)?.RedrawAllBlocks();
        }

        private void ApplyFullbrightEntityLight(bool enabled)
        {
            float[] blockLight = capi?.World?.BlockLightLevels;
            float[] sunLight = capi?.World?.SunLightLevels;
            if (blockLight == null || sunLight == null) return;

            if (enabled)
            {
                // Only copy once: toggling twice must not save the flattened tables over the
                // real ones.
                if (savedBlockLightLevels == null)
                {
                    savedBlockLightLevels = (float[])blockLight.Clone();
                    savedSunLightLevels = (float[])sunLight.Clone();
                }

                for (int i = 0; i < blockLight.Length; i++) blockLight[i] = 1f;
                for (int i = 0; i < sunLight.Length; i++) sunLight[i] = 1f;
                return;
            }

            if (savedBlockLightLevels == null) return;

            // Length can differ if the world changed under us; only put back what still fits.
            Array.Copy(savedBlockLightLevels, blockLight,
                Math.Min(savedBlockLightLevels.Length, blockLight.Length));
            Array.Copy(savedSunLightLevels, sunLight,
                Math.Min(savedSunLightLevels.Length, sunLight.Length));

            savedBlockLightLevels = null;
            savedSunLightLevels = null;
        }

        /// <summary>
        /// Hands back a gamma an older build of this mod took over for its first, failed run at
        /// fullbright. That version drove ClientSettings.GammaLevel, and the game saves its own
        /// graphics settings, so a crash while it was on left the cranked value as the player's
        /// brightness with only this config naming the real one. Nothing takes it any more.
        /// </summary>
        private void RestoreGamma()
        {
            if (Config.GammaBeforeFullbright <= 0) return;

            ClientSettings.GammaLevel = GameMath.Clamp(Config.GammaBeforeFullbright, 0.3f, 3.0f);
            Config.GammaBeforeFullbright = 0;
            SaveConfig();
        }

        // ---- feature toggles -------------------------------------------------------

        /// <summary>
        /// True when the server also runs this mod, i.e. when the toggles below can actually
        /// take effect rather than only being reflected locally.
        /// </summary>
        public bool ServerHasMod => clientChannel != null && clientChannel.Connected;

        /// <summary>
        /// Invincibility and durability are decided entirely on the server -
        /// EntityBehaviorHealth.OnEntityReceiveDamage returns immediately when
        /// entity.World.Side is Client, and item durability is applied to the server's own
        /// copy of the inventory. So there is nothing a client-only install can do for these
        /// two beyond telling a server that also has the mod.
        /// </summary>
        private static bool IsServerAuthoritative(EnumFeature feature)
        {
            return feature == EnumFeature.Invincible
                || feature == EnumFeature.NoDurability
                || feature == EnumFeature.DropsAtPlayer
                || feature == EnumFeature.OneHitKill
                || feature == EnumFeature.NoHunger
                || feature == EnumFeature.FastPickup;
        }

        /// <summary>
        /// Mirrors the reach setting to the server so it can let attacks reach as far as the
        /// crosshair. Sent quietly: this follows the reach slider rather than a switch the
        /// player pressed, and a chat line on every drag step would be noise.
        /// </summary>
        public void ApplyRangedAttack()
        {
            bool extended = Config.ReachBonus > 0;

            // The client refuses to send a swing past the held weapon's range, so it has to
            // reach further too - but only when the server would honour it. Otherwise the
            // client plays the hit locally against a swing the server drops, which looks like
            // a landed blow that did nothing.
            Patches.RangedAttack.ClientEnabled = extended && ServerHasMod;

            ApplyFeature(EnumFeature.RangedAttack, extended, announce: false);
        }

        /// <summary>
        /// Records a toggle locally and, when the server also runs this mod, tells it too.
        /// Stays client-only without throwing if the server does not have the channel - that
        /// is the normal case for a client-side install.
        /// </summary>
        public void ApplyFeature(EnumFeature feature, bool enabled, bool announce = true)
        {
            string uid = capi.World.Player?.PlayerUID;
            if (uid == null) return;

            ModMenuState.Set(uid, feature, enabled);

            if (ServerHasMod)
            {
                clientChannel.SendPacket(new FeatureTogglePacket { Feature = feature, Enabled = enabled });
            }
            else if (announce && enabled && IsServerAuthoritative(feature))
            {
                // Say so rather than letting the switch sit on while nothing happens.
                capi.ShowChatMessage(
                    $"{FeatureName(feature)} needs this mod on the server as well - "
                    + "this server does not have it, so the setting will not take effect here.");
            }
        }

        private static string FeatureName(EnumFeature feature)
        {
            switch (feature)
            {
                case EnumFeature.Invincible: return "Invincibility";
                case EnumFeature.NoDurability: return "No durability loss";
                case EnumFeature.DropsAtPlayer: return "Drops at player";
                case EnumFeature.OneHitKill: return "One hit kill";
                case EnumFeature.NoHunger: return "No hunger";
                case EnumFeature.FastPickup: return "Faster pickup";
                default: return "Instant mine";
            }
        }

        private void OnClientTick(float dt)
        {
            IClientPlayer player = capi.World?.Player;
            if (player?.Entity == null) return;

            EntityControls controls = player.Entity.Controls;
            if (controls == null) return;

            // A teleport in progress owns the position and the no-clip flag until it lands.
            if (glideActive)
            {
                OnGlideTick();
                return;
            }

            // No clip carries free move with it. On its own it would only turn collision off
            // and leave the player sinking through the floor with no way back up, because the
            // game hangs both halves of the escape on free move alone: PModuleGravity stands
            // down while IsFlying, and SystemPlayerControl only maps Up/Down to a rise and a
            // descent once DetachedMode is set. The switches stay independent - either one is
            // enough on its own.
            bool freeMove = Config.Flight || Config.NoClip;

            if (freeMove)
            {
                controls.IsFlying = true;
                controls.NoClip = Config.NoClip;
                controls.MovespeedMultiplier = (float)Config.FlySpeed;
                player.WorldData.FreeMove = true;
                player.WorldData.NoClip = Config.NoClip;
                player.WorldData.MoveSpeedMultiplier = (float)Config.FlySpeed;
            }
            else if (controls.IsFlying && player.WorldData.CurrentGameMode != EnumGameMode.Creative
                                       && player.WorldData.CurrentGameMode != EnumGameMode.Spectator)
            {
                // Only clear what we set, so we do not fight creative mode's own flight.
                controls.IsFlying = false;
                controls.NoClip = false;
                controls.MovespeedMultiplier = 1f;
                player.WorldData.FreeMove = false;
                player.WorldData.NoClip = false;
                player.WorldData.MoveSpeedMultiplier = 1f;
            }

            ApplyReach(player);

            // Flying down is a fall as far as a remote server is concerned - it sees the same
            // stream of dropping positions and charges for it on touchdown - so flight always
            // gets the catch. The toggle only decides whether ordinary falls get it too.
            //
            // No clip stands the catch down entirely: sinking through terrain, there is always
            // ground right below the feet, so the catch would hold every descent to its landing
            // speed and turn no clip into a crawl. Nothing lands while it is on either - and
            // the moment it goes off, the two rules above take over again.
            if (!Config.NoClip && (Config.NoFallDamage || Config.Flight))
            {
                CatchFall(player.Entity);
            }
        }

        // ---- no fall damage (the one client-side slice of invincibility) -----------
        //
        // Fall damage is the only damage a remote server does not compute purely on its own:
        // the server has no physics for a client-controlled player, so it *reconstructs* the
        // landing velocity from the position packets the client sends
        // (EntityBehaviorPlayerPhysics.HandleRemotePhysics:
        //   lPos.Motion.Y = (nPos.Y - lPos.Y) / dtFactor) and feeds that into the fall damage
        // check. That check bails out completely when the landing velocity is gentler than
        // roughly -0.19 (EntityBehaviorHealth.OnFallToGround: `if (!(withYMotion > num3))`
        // with num3 = -0.19 * fallDamageThreshold, default 1.0), no matter how far the fall.
        //
        // So the trick is simply to make the *reported* descent gentle in the final stretch
        // before the ground. We do not touch anything until the player is close to landing,
        // so a normal fall looks and feels normal right up to the catch.

        /// <summary>
        /// Descent speed in blocks per physics tick that the server still reads as a soft
        /// landing. Its cutoff sits at 0.19; the margin absorbs a late position packet, which
        /// stretches the interval the server divides the travelled distance by.
        /// </summary>
        private const double SafeLandingSpeed = 0.12;

        /// <summary>
        /// How many blocks of gentle descent the server has to see before touchdown. It builds
        /// one speed value per position packet - sent every 4th physics tick, so every 1/15s -
        /// and lands with the harsher of the last two, which means the final 8 physics ticks
        /// have to be gentle. That is 8 * <see cref="SafeLandingSpeed"/>, rounded up for the
        /// tick the catch starts part-way through.
        /// </summary>
        private const double GentleDistance = 1.25;

        /// <summary>
        /// Physics ticks that may pass between two runs of this catch. Physics steps at 1/60s
        /// and the tick listener runs every 20ms, so 3 leaves room for a ~50ms frame hitch.
        /// Raising it is safer but starts the braking higher up, where it is more visible.
        /// </summary>
        private const double CheckLookaheadTicks = 3.0;

        private void CatchFall(Entity entity)
        {
            EntityPos pos = entity.Pos;

            double speed = -pos.Motion.Y; // blocks per physics tick, positive while descending
            if (speed <= SafeLandingSpeed) return; // rising or already gentle

            // Only look as far down as we could travel before the next run, plus the stretch
            // that has to be gentle - anything past that is not our problem yet.
            double window = GentleDistance + speed * CheckLookaheadTicks;

            double ground = HeightAboveGround(entity, window);
            if (double.IsNaN(ground)) return;

            // Bleed the speed off over the blocks above the gentle stretch instead of dropping
            // straight to a float: the cap is whatever still reaches that stretch by the next
            // run, so a fast fall stays fast and only the last GentleDistance blocks are slow.
            // Horizontal motion is left untouched.
            double allowed = Math.Max(SafeLandingSpeed, (ground - GentleDistance) / CheckLookaheadTicks);
            if (speed > allowed) pos.Motion.Y = -allowed;
        }

        /// <summary>
        /// Distance in blocks from the entity's feet to the first solid ground below, or NaN
        /// if none within <paramref name="maxDistance"/>. Uses the real collision box so slabs,
        /// fences and partial blocks are measured the same way the game would land on them.
        /// </summary>
        private double HeightAboveGround(Entity entity, double maxDistance)
        {
            IBlockAccessor ba = capi.World?.BlockAccessor;
            if (ba == null) return double.NaN;

            Cuboidf box = entity.CollisionBox;
            if (box == null) return double.NaN;

            float half = box.XSize / 2f;
            var probe = new Cuboidf(-half, 0, -half, half, box.YSize, half);

            Vec3d p = entity.Pos.XYZ;
            const double step = 0.25;
            for (double d = 0; d <= maxDistance + step; d += step)
            {
                if (capi.World.CollisionTester.IsColliding(ba, probe, new Vec3d(p.X, p.Y - d, p.Z), false))
                {
                    return d;
                }
            }
            return double.NaN;
        }

        // ---- coordinate spaces -----------------------------------------------------
        //
        // Entity positions are absolute world coordinates, but the HUD, the map and the
        // /tp command all speak coordinates relative to the world spawn point (the map
        // middle, typically 512000-ish on X and Z). Y is the same in both spaces.
        //
        // Everything is stored absolute and shown relative, so what the menu displays
        // matches what the game shows you.

        /// <summary>X/Z offset between absolute and displayed coordinates. Y is never shifted.</summary>
        private void SpawnOffset(out double ox, out double oz)
        {
            EntityPos spawn = capi.World?.DefaultSpawnPosition;
            ox = spawn?.X ?? 0;
            oz = spawn?.Z ?? 0;
        }

        public Vec3d ToRelative(double absX, double absY, double absZ)
        {
            SpawnOffset(out double ox, out double oz);
            return new Vec3d(absX - ox, absY, absZ - oz);
        }

        public Vec3d ToAbsolute(double relX, double relY, double relZ)
        {
            SpawnOffset(out double ox, out double oz);
            return new Vec3d(relX + ox, relY, relZ + oz);
        }

        /// <summary>Where the player is standing, in the coordinates the HUD shows.</summary>
        public Vec3d CurrentRelativePos()
        {
            var entity = capi.World?.Player?.Entity;
            if (entity == null) return new Vec3d();

            Vec3d p = entity.Pos.XYZ;
            return ToRelative(p.X, p.Y, p.Z);
        }

        public Vec3d RelativePosOf(SavedLocation loc) => ToRelative(loc.X, loc.Y, loc.Z);

        /// <summary>
        /// Where a saved slot will actually drop you, in map coordinates. Slots keep the raw
        /// position they were recorded at, but the teleport snaps to the block grid, so the
        /// menu shows the snapped figures rather than the stored ones - otherwise the label
        /// disagrees with where you end up.
        /// </summary>
        public Vec3d SnappedRelativePosOf(SavedLocation loc)
        {
            return ToRelative(
                SnapToBlockCenter(loc.X),
                SnapStandingHeight(loc.Y),
                SnapToBlockCenter(loc.Z));
        }

        // ---- teleporting -----------------------------------------------------------

        /// <summary>
        /// Moves the local player, taking coordinates in the same relative space the HUD and
        /// map use. Vintage Story lets the client own its own position and simply reports it
        /// upward, so this works on remote servers without their help.
        /// </summary>
        public void TeleportToRelative(double relX, double relY, double relZ)
        {
            Vec3d abs = ToAbsolute(relX, relY, relZ);
            TeleportToAbsolute(abs.X, abs.Y, abs.Z);
        }

        /// <summary>How many blocks upward to search for headroom before giving up.</summary>
        private const int MaxClearanceSearch = 256;

        /// <summary>Centre of the block column, so the player never lands straddling an edge.</summary>
        private static double SnapToBlockCenter(double v) => Math.Floor(v) + 0.5;

        /// <summary>
        /// Round the standing height up to whole blocks. Landing at 115.9 puts the player's
        /// feet just inside the block below, which the physics resolves by shoving them
        /// somewhere unhelpful; 116.0 sits cleanly on top of it.
        /// </summary>
        private static double SnapStandingHeight(double v) => Math.Ceiling(v);

        // The server refuses any position packet that moves a player more than 128 blocks on
        // any single axis - see EntityPosExtensions.SetFromPacket, which returns false and
        // makes the server answer with a correction packet. That correction is exactly the
        // "flash and snap straight back" you get from a naive long-distance teleport, and it
        // applies per axis, not to the straight-line distance.
        //
        // So anything further than that is walked across in steps, one per client tick, each
        // one small enough to be accepted. Roughly 96 blocks every 20ms is about 4800 blocks
        // a second, so even a cross-map jump lands in a couple of seconds.
        private const double ServerMaxMovePerPacket = 128.0;

        /// <summary>
        /// Step size. Deliberately half the server's limit rather than just under it: position
        /// updates go over UDP, so a packet can be lost, and after a loss the next step is two
        /// steps away from the server's copy. At 64 that is exactly 128, which still passes;
        /// at anything larger it trips the limit, gets corrected back, and the teleport
        /// live-locks instead of arriving.
        /// </summary>
        private const double TeleportStepBlocks = 64.0;

        /// <summary>Ticks (20ms each) before a stalled glide gives up - 20 seconds.</summary>
        private const int TeleportTimeoutTicks = 1000;

        private Vec3d glideTarget;
        private int glideTicks;
        private bool glideActive;
        private bool glideSavedNoClip;

        public void TeleportToAbsolute(double x, double y, double z)
        {
            var entity = capi.World?.Player?.Entity;
            if (entity == null) return;

            double tx = SnapToBlockCenter(x);
            double tz = SnapToBlockCenter(z);
            double ty = SnapStandingHeight(y);

            Vec3d from = entity.Pos.XYZ;
            bool tooFar = Math.Abs(tx - from.X) > TeleportStepBlocks
                       || Math.Abs(ty - from.Y) > TeleportStepBlocks
                       || Math.Abs(tz - from.Z) > TeleportStepBlocks;

            if (!tooFar)
            {
                ArriveAt(entity, tx, ty, tz);
                return;
            }

            BeginGlide(entity, tx, ty, tz);
        }

        private void BeginGlide(EntityAgent entity, double tx, double ty, double tz)
        {
            glideTarget = new Vec3d(tx, ty, tz);
            glideTicks = 0;
            glideActive = true;

            // Skim over the terrain on the way rather than ploughing through it, otherwise
            // collision resolution fights every step.
            glideSavedNoClip = entity.Controls.NoClip;
            entity.Controls.NoClip = true;

            Vec3d rel = ToRelative(tx, ty, tz);
            capi.ShowChatMessage($"Teleporting to {rel.X:0.#} {rel.Y:0.#} {rel.Z:0.#}...");
        }

        private void EndGlide(EntityAgent entity)
        {
            glideActive = false;
            glideTarget = null;
            if (entity?.Controls != null) entity.Controls.NoClip = glideSavedNoClip;
        }

        /// <summary>
        /// Advances one step toward the pending target. Reading the current position fresh
        /// every tick makes this self-correcting: if the server does bounce us back, the next
        /// step simply sets off again from wherever we actually ended up, so progress slows
        /// instead of failing outright.
        /// </summary>
        private void OnGlideTick()
        {
            var entity = capi.World?.Player?.Entity;
            if (entity == null)
            {
                glideActive = false;
                return;
            }

            if (++glideTicks > TeleportTimeoutTicks)
            {
                EndGlide(entity);
                capi.ShowChatMessage("Teleport gave up - the server kept rejecting the move.");
                return;
            }

            Vec3d cur = entity.Pos.XYZ;
            double nx = StepToward(cur.X, glideTarget.X);
            double ny = StepToward(cur.Y, glideTarget.Y);
            double nz = StepToward(cur.Z, glideTarget.Z);

            bool arrived = nx == glideTarget.X && ny == glideTarget.Y && nz == glideTarget.Z;

            entity.Pos.SetPos(nx, ny, nz);
            entity.Pos.Motion.Set(0, 0, 0);
            // Push each step out immediately instead of waiting on the client's own cadence,
            // so the server's copy keeps pace and never falls more than one step behind.
            capi.Network.SendPlayerPositionPacket();

            if (arrived)
            {
                Vec3d target = glideTarget;
                EndGlide(entity);
                ArriveAt(entity, target.X, target.Y, target.Z);
            }
        }

        private static double StepToward(double from, double to)
        {
            double delta = to - from;
            if (Math.Abs(delta) <= TeleportStepBlocks) return to;
            return from + Math.Sign(delta) * TeleportStepBlocks;
        }

        /// <summary>Final placement: clearance check, then settle and report.</summary>
        private void ArriveAt(EntityAgent entity, double tx, double ty, double tz)
        {
            ty = FindClearHeight(entity, tx, ty, tz, out bool verified, out bool gaveUp);

            if (gaveUp)
            {
                capi.ShowChatMessage("Could not find a free spot above that position - teleport cancelled.");
                return;
            }

            // As of 1.22 Pos is the single authoritative position on both sides; the old
            // separate ServerPos is deprecated in favour of it.
            entity.Pos.SetPos(tx, ty, tz);
            // Leftover momentum would immediately drag the player back off the target.
            entity.Pos.Motion.Set(0, 0, 0);
            capi.Network.SendPlayerPositionPacket();

            Vec3d rel = ToRelative(tx, ty, tz);
            string note = verified ? "" : " (destination still loading, clearance unchecked)";
            capi.ShowChatMessage($"Teleported to {rel.X:0.#} {rel.Y:0.#} {rel.Z:0.#}{note}");
        }

        /// <summary>
        /// Walks upward from <paramref name="startY"/> until the player's box fits, and
        /// returns that height.
        ///
        /// <paramref name="verified"/> is false when the destination chunk is not loaded on
        /// the client - block lookups there report air whether or not there is really
        /// terrain, so the answer would be meaningless and we say so instead of pretending.
        /// </summary>
        private double FindClearHeight(Entity entity, double x, double startY, double z,
            out bool verified, out bool gaveUp)
        {
            gaveUp = false;
            verified = false;

            IBlockAccessor ba = capi.World?.BlockAccessor;
            if (ba == null) return startY;

            if (ba.GetChunkAtBlockPos(new BlockPos((int)x, (int)startY, (int)z)) == null)
            {
                return startY;
            }

            verified = true;

            // Derive only the dimensions from the entity and rebuild a box we know is
            // centred on the feet, rather than depending on how Entity.CollisionBox stores
            // its origin.
            Cuboidf src = entity.CollisionBox;
            float halfWidth = src.XSize / 2f;
            float height = src.YSize;
            var probe = new Cuboidf(-halfWidth, 0, -halfWidth, halfWidth, height, halfWidth);

            double maxY = Math.Min(startY + MaxClearanceSearch, ba.MapSizeY - 1);

            for (double y = startY; y <= maxY; y++)
            {
                // alsoCheckTouch: false - resting on the floor is touching, not colliding.
                if (!capi.World.CollisionTester.IsColliding(ba, probe, new Vec3d(x, y, z), false))
                {
                    return y;
                }
            }

            gaveUp = true;
            return startY;
        }

        public void TeleportToSaved(int slot)
        {
            SavedLocation loc = Config.Locations[slot];
            if (!loc.HasPosition)
            {
                capi.ShowChatMessage($"'{loc.Name}' has no saved position yet - stand somewhere and press Save.");
                return;
            }

            TeleportToAbsolute(loc.X, loc.Y, loc.Z);
        }

        public void SaveCurrentPosition(int slot)
        {
            var entity = capi.World?.Player?.Entity;
            if (entity == null) return;

            // Stored absolute so the slot keeps pointing at the same place even if the
            // world's spawn point is later moved.
            Vec3d pos = entity.Pos.XYZ;
            SavedLocation loc = Config.Locations[slot];
            loc.X = pos.X;
            loc.Y = pos.Y;
            loc.Z = pos.Z;
            loc.HasPosition = true;

            SaveConfig();

            Vec3d rel = ToRelative(pos.X, pos.Y, pos.Z);
            capi.ShowChatMessage($"Saved '{loc.Name}' at {rel.X:0.#} {rel.Y:0.#} {rel.Z:0.#}");
        }

        // ---- config ----------------------------------------------------------------

        private void LoadConfig()
        {
            try
            {
                Config = capi.LoadModConfig<ModMenuConfig>(ConfigFile);
            }
            catch (Exception e)
            {
                Mod.Logger.Warning("Could not read {0}, falling back to defaults: {1}", ConfigFile, e.Message);
            }

            if (Config == null) Config = new ModMenuConfig();
            Config.Sanitize();
            SaveConfig();
        }

        public void SaveConfig()
        {
            capi.StoreModConfig(Config, ConfigFile);
        }

        public override void Dispose()
        {
            harmony?.UnpatchAll(HarmonyId);
            harmony = null;

            // Leaving the world is not a reason to leave somebody's brightness cranked. The
            // toggle itself stays saved, so it comes back on with the next world.
            if (capi != null && Config?.GammaBeforeFullbright > 0)
            {
                RestoreGamma();
                SaveConfig();
            }

            // The preview holds an uploaded mesh, so it has to go back before the world does.
            if (veinPreview != null)
            {
                capi?.Event.UnregisterRenderer(veinPreview, EnumRenderStage.AfterFinalComposition);
                veinPreview.Dispose();
                veinPreview = null;
            }

            // Static, so it would otherwise keep the old world's client API alive across a
            // disconnect and reconnect.
            VeinMiner.Instance = null;
            veinMiner = null;

            base.Dispose();
        }
    }
}
