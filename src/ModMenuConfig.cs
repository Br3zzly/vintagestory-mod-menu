namespace ModMenu
{
    /// <summary>
    /// One of the three user-saveable teleport slots. Coordinates are absolute world
    /// coordinates, not the // relative ones shown on the HUD.
    /// </summary>
    public class SavedLocation
    {
        public string Name = "Unnamed";
        public double X;
        public double Y;
        public double Z;

        /// <summary>False until the player has pressed Save on this slot at least once.</summary>
        public bool HasPosition;
    }

    /// <summary>
    /// Persisted to ModConfig/modmenu.json. Client side only - the server never reads this,
    /// it is told about toggle changes over the network channel instead.
    /// </summary>
    public class ModMenuConfig
    {
        public bool Invincible;
        public bool Flight;
        public bool NoClip;
        public bool InstantMine;
        public bool NoDurabilityLoss;

        /// <summary>
        /// Lifts the whole scene so unlit caves are readable. Nothing but local rendering, so
        /// it needs no cooperation from anything and cannot be seen from outside this client.
        /// </summary>
        public bool Fullbright;

        /// <summary>
        /// The gamma from before fullbright took the setting over, waiting to be handed back.
        /// Kept in this file rather than in memory because the game saves its own graphics
        /// settings: a crash with fullbright on would otherwise leave the cranked value as the
        /// player's brightness for good, with nothing left to say what it used to be. Zero
        /// means nothing is being held.
        /// </summary>
        public float GammaBeforeFullbright;

        /// <summary>Every hit you land kills outright. Server-decided, like invincibility.</summary>
        public bool OneHitKill;

        /// <summary>Saturation stops draining. Server-decided.</summary>
        public bool NoHunger;

        /// <summary>Lifts the server's 23-items-a-second collection rate. Server-decided.</summary>
        public bool FastPickup;

        /// <summary>Breaks the rest of a vein when one block of it is mined.</summary>
        public bool VeinMiner;

        /// <summary>
        /// Holds the vein miner to a rate a server cannot ban for. Off, the whole vein goes at
        /// once - which is what PlayerAntiAbuseMonitor bans for, where a server has it enabled.
        /// Defaults on, so the risky mode is only ever entered deliberately.
        /// </summary>
        public bool VeinMinerBanSafe = true;

        /// <summary>
        /// Spawns everything you break at your feet instead of at the block. Server-decided,
        /// like invincibility: drops are spawned by the server's copy of the world.
        /// </summary>
        public bool DropsAtPlayer;

        /// <summary>
        /// The one damage type a client-only mod can genuinely prevent on a remote server,
        /// because the server derives fall damage from the client's own position stream.
        /// </summary>
        public bool NoFallDamage;

        /// <summary>Lowest accepted <see cref="FlySpeed"/>: normal movement speed.</summary>
        public const double MinFlySpeed = 1.0;

        /// <summary>
        /// Highest accepted <see cref="FlySpeed"/>. Past this the descent gets fast enough that
        /// the fall catch can no longer keep the last blocks before the ground gentle.
        /// </summary>
        public const double MaxFlySpeed = 3.0;

        /// <summary>
        /// Steps per 1.0 of <see cref="FlySpeed"/>. The menu slider carries whole numbers only,
        /// so speeds are held to this grid and it can always show the value actually in force.
        /// </summary>
        public const int FlySpeedSteps = 10;

        /// <summary>Multiplier applied to movement speed while flight is on.</summary>
        public double FlySpeed = 1.0;

        /// <summary>Blocks a single vein mine may break, the one you hit included.</summary>
        public const int MinVeinMinerLimit = 1;
        public const int MaxVeinMinerLimit = 400;

        public int VeinMinerLimit = 10;

        /// <summary>Blocks added to the player's own picking range. Zero leaves it alone.</summary>
        public const int MinReachBonus = 0;
        public const int MaxReachBonus = 100;

        public int ReachBonus;

        public SavedLocation[] Locations =
        {
            new SavedLocation { Name = "Home" },
            new SavedLocation { Name = "Base" },
            new SavedLocation { Name = "Mine" }
        };

        /// <summary>
        /// Guards against a hand-edited or older config file leaving us with a null or
        /// wrong-length array, which would otherwise crash the dialog on open.
        /// </summary>
        public void Sanitize()
        {
            if (Locations == null || Locations.Length != 3)
            {
                var fixedUp = new SavedLocation[3];
                for (int i = 0; i < 3; i++)
                {
                    fixedUp[i] = Locations != null && i < Locations.Length && Locations[i] != null
                        ? Locations[i]
                        : new SavedLocation { Name = "Slot " + (i + 1) };
                }
                Locations = fixedUp;
            }

            for (int i = 0; i < Locations.Length; i++)
            {
                if (Locations[i] == null) Locations[i] = new SavedLocation { Name = "Slot " + (i + 1) };
                if (string.IsNullOrWhiteSpace(Locations[i].Name)) Locations[i].Name = "Slot " + (i + 1);
            }

            // A config written before the cap dropped to 3 keeps its intent by being clamped
            // rather than reset, and anything in between is snapped onto the slider's grid so
            // the menu cannot show a value other than the one in force. The first test is
            // written as a negation because NaN would slide through every ordinary comparison.
            if (!(FlySpeed >= MinFlySpeed)) FlySpeed = MinFlySpeed;
            else if (FlySpeed > MaxFlySpeed) FlySpeed = MaxFlySpeed;
            else FlySpeed = System.Math.Round(FlySpeed * FlySpeedSteps) / FlySpeedSteps;

            if (VeinMinerLimit < MinVeinMinerLimit) VeinMinerLimit = MinVeinMinerLimit;
            else if (VeinMinerLimit > MaxVeinMinerLimit) VeinMinerLimit = MaxVeinMinerLimit;

            if (ReachBonus < MinReachBonus) ReachBonus = MinReachBonus;
            else if (ReachBonus > MaxReachBonus) ReachBonus = MaxReachBonus;
        }
    }
}
