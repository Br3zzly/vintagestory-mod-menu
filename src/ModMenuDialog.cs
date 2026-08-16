using System;
using System.Collections.Generic;
using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModMenu
{
    /// <summary>
    /// The F2 window. One tab per group of features, because as a single column it grew taller
    /// than the screen once the GUI scale passed about 11.
    ///
    /// Each tab is a list of rows that know their own height before anything is composed, which
    /// is what lets the layout decide how many columns it needs: at a large GUI scale even one
    /// tab can outgrow the screen, and then the rows are dealt into a second column rather than
    /// running off the bottom.
    /// </summary>
    public class ModMenuDialog : GuiDialog
    {
        private const double ColumnWidth = 420;
        private const double ColumnGap = 24;
        private const double RowHeight = 28;
        private const double RowGap = 6;
        private const double SwitchSize = 22;
        private const double TitleBarHeight = 32;
        private const double TabBarHeight = 32;

        /// <summary>More than this and the window gets wider than it is tall, which reads worse.</summary>
        private const int MaxColumnCount = 3;

        private static readonly string[] TabNames = { "Player", "Movement", "Mining", "Teleport" };

        /// <summary>Shown on the toggles the server decides, when the server has no idea we exist.</summary>
        private const string ServerOnlyHint = "Only works when the mod is installed on the server";

        /// <summary>Label colour for a toggle that cannot do anything right now.</summary>
        private static readonly double[] DisabledTextColor = { 0.55, 0.55, 0.55, 1.0 };

        /// <summary>Adds an element block at the given column offset and advances y past it.</summary>
        private delegate void RowBuilder(GuiComposer composer, double x, ref double y);

        /// <summary>
        /// One block of the layout. The height is declared up front rather than discovered
        /// while composing, so the column split can be worked out before a single element
        /// exists.
        /// </summary>
        private sealed class Row
        {
            public readonly double Height;
            public readonly RowBuilder Build;

            public Row(double height, RowBuilder build)
            {
                Height = height;
                Build = build;
            }
        }

        private readonly ModMenuSystem system;

        private double tpX, tpY, tpZ;

        private int activeTab;

        /// <summary>
        /// Set while the fly speed slider moves, which fires on every step it passes. The
        /// config file is written once on close rather than on every pixel of a drag; the
        /// speed itself is live either way, since flight reads it straight off the config.
        /// </summary>
        private bool flySpeedUnsaved;

        /// <summary>Same story as <see cref="flySpeedUnsaved"/>, for the vein miner limit.</summary>
        private bool veinLimitUnsaved;

        /// <summary>Same story again, for the reach bonus.</summary>
        private bool reachUnsaved;

        public ModMenuDialog(ICoreClientAPI capi, ModMenuSystem system) : base(capi)
        {
            this.system = system;
        }

        public override string ToggleKeyCombinationCode => null;

        private ModMenuConfig Config => system.Config;

        public override void OnGuiOpened()
        {
            base.OnGuiOpened();

            // Prefill the coordinate boxes with where the player is standing, so "nudge my
            // Y up by 20" does not mean typing all three numbers from scratch. These are the
            // same spawn-relative numbers the HUD and map show, not raw entity coordinates.
            Vec3d here = system.CurrentRelativePos();
            tpX = here.X;
            tpY = here.Y;
            tpZ = here.Z;

            Compose();
        }

        public override void OnGuiClosed()
        {
            base.OnGuiClosed();

            if (flySpeedUnsaved || veinLimitUnsaved || reachUnsaved)
            {
                system.SaveConfig();
                flySpeedUnsaved = false;
                veinLimitUnsaved = false;
                reachUnsaved = false;
            }
        }

        // ---- layout ----------------------------------------------------------------

        /// <summary>
        /// Deals rows into columns, keeping their order and never spilling past
        /// <paramref name="available"/> unless a single row is taller than that or the column
        /// budget runs out. Pure arithmetic, so it can be exercised without a screen.
        /// </summary>
        internal static int[] ColumnAssignment(IList<double> heights, double available, int maxColumns)
        {
            var assignment = new int[heights.Count];
            int column = 0;
            double used = 0;

            for (int i = 0; i < heights.Count; i++)
            {
                // Never open a column with nothing in the previous one, and never open one
                // past the budget - the last column takes the overflow instead.
                if (used > 0 && column < maxColumns - 1 && used + heights[i] > available)
                {
                    column++;
                    used = 0;
                }

                assignment[i] = column;
                used += heights[i];
            }

            return assignment;
        }

        /// <summary>Screen height left for rows once the window's own furniture is paid for.</summary>
        private double AvailableHeight()
        {
            double screen = capi.Render.FrameHeight / RuntimeEnv.GUIScale;
            double furniture = TitleBarHeight + TabBarHeight + RowGap * 2
                             + GuiStyle.ElementToDialogPadding * 2 + 60;

            return Math.Max(160, screen - furniture);
        }

        private int MaxColumns()
        {
            double screen = capi.Render.FrameWidth / RuntimeEnv.GUIScale;
            int fits = (int)((screen - 60) / (ColumnWidth + ColumnGap));

            return GameMath.Clamp(fits, 1, MaxColumnCount);
        }

        private void Compose()
        {
            CairoFont font = CairoFont.WhiteSmallText();

            activeTab = GameMath.Clamp(activeTab, 0, TabNames.Length - 1);

            List<Row> rows = RowsForTab(activeTab, font);

            var heights = new double[rows.Count];
            for (int i = 0; i < rows.Count; i++) heights[i] = rows[i].Height;

            int[] assignment = ColumnAssignment(heights, AvailableHeight(), MaxColumns());
            int columnCount = rows.Count == 0 ? 1 : assignment[rows.Count - 1] + 1;

            double contentWidth = columnCount * ColumnWidth + (columnCount - 1) * ColumnGap;

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            GuiComposer composer = capi.Gui
                .CreateCompo("modmenu-main", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Mod Menu", OnTitleBarClose, font, null, null)
                .BeginChildElements(bgBounds);

            double top = TitleBarHeight;

            var tabs = new GuiTab[TabNames.Length];
            for (int i = 0; i < TabNames.Length; i++)
            {
                tabs[i] = new GuiTab { Name = TabNames[i], DataInt = i };
            }

            composer.AddHorizontalTabs(tabs,
                ElementBounds.Fixed(0, top, contentWidth, TabBarHeight),
                OnTabClicked,
                font,
                font.Clone().WithWeight(Cairo.FontWeight.Bold),
                "tabs");

            top += TabBarHeight + RowGap * 2;

            // Every column starts level with the tab strip; rows carry their own x offset so
            // the same builder works in any column.
            var columnY = new double[columnCount];
            for (int i = 0; i < columnCount; i++) columnY[i] = top;

            for (int i = 0; i < rows.Count; i++)
            {
                int column = assignment[i];
                double x = column * (ColumnWidth + ColumnGap);

                rows[i].Build(composer, x, ref columnY[column]);
            }

            SingleComposer = composer.EndChildElements().Compose();

            GuiElementHorizontalTabs tabElem = SingleComposer.GetHorizontalTabs("tabs");
            if (tabElem != null) tabElem.activeElement = activeTab;

            ApplyCurrentValues();
        }

        private void OnTabClicked(int index)
        {
            activeTab = index;
            Compose();
        }

        // ---- the tabs --------------------------------------------------------------

        private List<Row> RowsForTab(int tab, CairoFont font)
        {
            switch (tab)
            {
                case 0: return PlayerRows(font);
                case 1: return MovementRows(font);
                case 2: return MiningRows(font);
                default: return TeleportRows(font);
            }
        }

        private List<Row> MiningRows(CairoFont font)
        {
            return new List<Row>
            {
                ToggleRow(font, "Instant mine", "swInstantMine", on =>
                {
                    Config.InstantMine = on;
                    system.ApplyFeature(EnumFeature.InstantMine, on);
                    system.SaveConfig();
                }),

                ToggleRow(font, "Vein miner", "swVeinMiner", on =>
                {
                    Config.VeinMiner = on;
                    system.SaveConfig();
                }),

                SliderRow(font, "Blocks per vein", "sldVeinLimit", v =>
                {
                    Config.VeinMinerLimit = v;
                    veinLimitUnsaved = true;
                    return true;
                }),

                ToggleRow(font, "AntiAbuse Safe", "swVeinBanSafe", on =>
                {
                    Config.VeinMinerBanSafe = on;
                    system.SaveConfig();

                    // Worth saying out loud rather than leaving in a switch position: a server
                    // running the anti abuse monitor bans for exactly what this turns off.
                    if (!on)
                    {
                        capi.ShowChatMessage(
                            "Vein miner: AntiAbuse Safe is off. Whole veins now break at once. "
                            + "A server with anti abuse enabled bans for 40 breaks within 2 "
                            + "seconds - by default for 14 days. Safe in singleplayer and on "
                            + "servers that leave anti abuse off, which is the stock setting.");
                    }
                }),

                ToggleRow(font, "No durability loss", "swNoDurability", on =>
                {
                    Config.NoDurabilityLoss = on;
                    system.ApplyFeature(EnumFeature.NoDurability, on);
                    system.SaveConfig();
                }, needsServerMod: true),

                ToggleRow(font, "Drops at player", "swDropsAtPlayer", on =>
                {
                    Config.DropsAtPlayer = on;
                    system.ApplyFeature(EnumFeature.DropsAtPlayer, on);
                    system.SaveConfig();
                }, needsServerMod: true),

                ToggleRow(font, "Faster pickup", "swFastPickup", on =>
                {
                    Config.FastPickup = on;
                    system.ApplyFeature(EnumFeature.FastPickup, on);
                    system.SaveConfig();
                }, needsServerMod: true)
            };
        }

        private List<Row> MovementRows(CairoFont font)
        {
            return new List<Row>
            {
                ToggleRow(font, "Flight", "swFlight", on =>
                {
                    Config.Flight = on;
                    system.SaveConfig();
                }),

                ToggleRow(font, "No clip", "swNoClip", on =>
                {
                    Config.NoClip = on;
                    system.SaveConfig();
                }),

                ToggleRow(font, "No fall damage", "swNoFallDamage", on =>
                {
                    Config.NoFallDamage = on;
                    system.SaveConfig();
                }),

                // Fly speed. A slider rather than a number box, because the ceiling here is a
                // real limit and not a preference - past 3 the fall catch can no longer keep a
                // landing gentle - and a text field can always be typed past its range. The
                // slider itself only carries whole numbers, so it counts tenths.
                SliderRow(font, "Fly speed", "sldFlySpeed", v =>
                {
                    Config.FlySpeed = v / (double)ModMenuConfig.FlySpeedSteps;
                    flySpeedUnsaved = true;
                    return true;
                })
            };
        }

        private List<Row> PlayerRows(CairoFont font)
        {
            return new List<Row>
            {
                ToggleRow(font, "Invincibility", "swInvincible", on =>
                {
                    Config.Invincible = on;
                    system.ApplyFeature(EnumFeature.Invincible, on);
                    system.SaveConfig();
                }, needsServerMod: true),

                ToggleRow(font, "One hit kill", "swOneHitKill", on =>
                {
                    Config.OneHitKill = on;
                    system.ApplyFeature(EnumFeature.OneHitKill, on);
                    system.SaveConfig();
                }, needsServerMod: true),

                ToggleRow(font, "No hunger", "swNoHunger", on =>
                {
                    Config.NoHunger = on;
                    system.ApplyFeature(EnumFeature.NoHunger, on);
                    system.SaveConfig();
                }, needsServerMod: true),

                ToggleRow(font, "Fullbright", "swFullbright", on =>
                {
                    Config.Fullbright = on;
                    system.ApplyFullbright(on);
                    system.SaveConfig();
                }),

                // Reach. Whole blocks on top of whatever the game gives you, so zero is
                // "untouched".
                SliderRow(font, "Reach", "sldReach", v =>
                {
                    bool wasExtended = Config.ReachBonus > 0;
                    Config.ReachBonus = v;
                    reachUnsaved = true;

                    // Attacks reach as far as the crosshair only if the server knows reach is
                    // extended, so tell it - but only when that actually changed, not on every
                    // step of the drag.
                    if (wasExtended != (v > 0)) system.ApplyRangedAttack();

                    return true;
                })
            };
        }

        private List<Row> TeleportRows(CairoFont font)
        {
            var rows = new List<Row>
            {
                HeaderRow(font, "Teleport to coordinates"),

                new Row(RowHeight + RowGap, delegate (GuiComposer c, double x, ref double y)
                {
                    c.AddStaticText("X", font, ElementBounds.Fixed(x, y, 14, RowHeight), null)
                     .AddNumberInput(ElementBounds.Fixed(x + 16, y, 110, RowHeight),
                         t => ParseInto(t, ref tpX), font, "fdX")
                     .AddStaticText("Y", font, ElementBounds.Fixed(x + 140, y, 14, RowHeight), null)
                     .AddNumberInput(ElementBounds.Fixed(x + 156, y, 110, RowHeight),
                         t => ParseInto(t, ref tpY), font, "fdY")
                     .AddStaticText("Z", font, ElementBounds.Fixed(x + 280, y, 14, RowHeight), null)
                     .AddNumberInput(ElementBounds.Fixed(x + 296, y, 110, RowHeight),
                         t => ParseInto(t, ref tpZ), font, "fdZ");

                    y += RowHeight + RowGap;
                }),

                new Row(RowHeight + RowGap * 3, delegate (GuiComposer c, double x, ref double y)
                {
                    c.AddButton("Teleport", () =>
                    {
                        system.TeleportToRelative(tpX, tpY, tpZ);
                        return true;
                    }, ElementBounds.Fixed(x, y, ColumnWidth, RowHeight + 4), EnumButtonStyle.Normal, "btnTeleport");

                    y += RowHeight + RowGap * 3;
                }),

                HeaderRow(font, "Saved locations")
            };

            for (int i = 0; i < 3; i++)
            {
                int slot = i; // capture per iteration, not the shared loop variable

                rows.Add(new Row(RowHeight + RowGap + 20 + RowGap,
                    delegate (GuiComposer c, double x, ref double y)
                    {
                        c.AddTextInput(ElementBounds.Fixed(x, y, 180, RowHeight),
                            text =>
                            {
                                Config.Locations[slot].Name = string.IsNullOrWhiteSpace(text)
                                    ? "Slot " + (slot + 1)
                                    : text;
                                system.SaveConfig();
                            }, font, "tfName" + slot)
                         .AddButton("Save", () =>
                         {
                             system.SaveCurrentPosition(slot);
                             RefreshSlotLabels();
                             return true;
                         }, ElementBounds.Fixed(x + 190, y, 100, RowHeight + 4), EnumButtonStyle.Normal, "btnSave" + slot)
                         .AddButton("Go", () =>
                         {
                             system.TeleportToSaved(slot);
                             return true;
                         }, ElementBounds.Fixed(x + 300, y, 100, RowHeight + 4), EnumButtonStyle.Normal, "btnGo" + slot);

                        y += RowHeight + RowGap;

                        c.AddStaticText(SlotCoordsLabel(slot), CairoFont.WhiteDetailText(),
                            ElementBounds.Fixed(x, y, ColumnWidth, 20), "txtCoords" + slot);

                        y += 20 + RowGap;
                    }));
            }

            return rows;
        }

        // ---- row kinds -------------------------------------------------------------

        /// <summary>
        /// A labelled switch. Rows marked <paramref name="needsServerMod"/> are the ones the
        /// server decides - invincibility, durability, drop placement - so when the server has
        /// never heard of this mod they are greyed out, made unclickable and given a hover
        /// hint, rather than sitting there looking functional and doing nothing.
        /// </summary>
        private Row ToggleRow(CairoFont font, string label, string key, Action<bool> onToggle,
            bool needsServerMod = false)
        {
            bool inert = needsServerMod && !system.ServerHasMod;
            CairoFont labelFont = inert ? font.Clone().WithColor(DisabledTextColor) : font;

            return new Row(RowHeight + RowGap, delegate (GuiComposer c, double x, ref double y)
            {
                c.AddStaticText(label, labelFont, ElementBounds.Fixed(x, y, 300, RowHeight), null)
                 .AddSwitch(onToggle, ElementBounds.Fixed(x + 320, y, SwitchSize, SwitchSize), key, SwitchSize, 4);

                if (inert)
                {
                    // Before the composer runs: the switch bakes its dimmed look during
                    // ComposeElements, and its click handler ignores presses while disabled.
                    GuiElementSwitch element = c.GetSwitch(key);
                    if (element != null) element.Enabled = false;

                    // Covers the label and the switch, so the hint appears anywhere on the row.
                    c.AddHoverText(ServerOnlyHint, font, 260,
                        ElementBounds.Fixed(x, y, 320 + SwitchSize, RowHeight));
                }

                y += RowHeight + RowGap;
            });
        }

        private Row SliderRow(CairoFont font, string label, string key, ActionConsumable<int> onChanged)
        {
            return new Row(RowHeight + RowGap, delegate (GuiComposer c, double x, ref double y)
            {
                c.AddStaticText(label, font, ElementBounds.Fixed(x, y, 200, RowHeight), null)
                 .AddSlider(onChanged, ElementBounds.Fixed(x + 210, y + 4, 190, 20), key);

                y += RowHeight + RowGap;
            });
        }

        private Row HeaderRow(CairoFont font, string title)
        {
            CairoFont bold = font.Clone().WithWeight(Cairo.FontWeight.Bold);

            return new Row(RowHeight, delegate (GuiComposer c, double x, ref double y)
            {
                c.AddStaticText(title, bold, ElementBounds.Fixed(x, y, ColumnWidth, RowHeight), null);
                y += RowHeight;
            });
        }

        // ---- state -----------------------------------------------------------------

        /// <summary>
        /// Pushes config state into the freshly composed widgets. Everything is looked up
        /// defensively, because only the active tab's elements exist.
        /// </summary>
        private void ApplyCurrentValues()
        {
            SetSwitch("swInvincible", Config.Invincible);
            SetSwitch("swNoDurability", Config.NoDurabilityLoss);
            SetSwitch("swDropsAtPlayer", Config.DropsAtPlayer);
            SetSwitch("swInstantMine", Config.InstantMine);
            SetSwitch("swVeinMiner", Config.VeinMiner);
            SetSwitch("swVeinBanSafe", Config.VeinMinerBanSafe);
            SetSwitch("swFlight", Config.Flight);
            SetSwitch("swNoClip", Config.NoClip);
            SetSwitch("swNoFallDamage", Config.NoFallDamage);
            SetSwitch("swFullbright", Config.Fullbright);
            SetSwitch("swOneHitKill", Config.OneHitKill);
            SetSwitch("swNoHunger", Config.NoHunger);
            SetSwitch("swFastPickup", Config.FastPickup);

            // The tooltip has to be in place before SetValues, which is what bakes the value
            // label into a texture. Leaving ShowTextWhenResting off keeps the value out of the
            // slider track itself - it only appears in the bubble above the handle while that
            // is being dragged or hovered.
            SetSlider("sldFlySpeed", Tenths(Config.FlySpeed),
                Tenths(ModMenuConfig.MinFlySpeed), Tenths(ModMenuConfig.MaxFlySpeed),
                v => Fmt(v / (double)ModMenuConfig.FlySpeedSteps) + "x");

            SetSlider("sldVeinLimit", Config.VeinMinerLimit,
                ModMenuConfig.MinVeinMinerLimit, ModMenuConfig.MaxVeinMinerLimit,
                v => v + (v == 1 ? " block" : " blocks"));

            SetSlider("sldReach", Config.ReachBonus,
                ModMenuConfig.MinReachBonus, ModMenuConfig.MaxReachBonus,
                v => v == 0 ? "normal" : "+" + v + " blocks");

            SetNumberInput("fdX", tpX);
            SetNumberInput("fdY", tpY);
            SetNumberInput("fdZ", tpZ);

            for (int i = 0; i < 3; i++)
            {
                GuiElementTextInput name = SingleComposer.GetTextInput("tfName" + i);
                if (name != null) name.SetValue(Config.Locations[i].Name);
            }
        }

        private void SetSwitch(string key, bool on)
        {
            GuiElementSwitch element = SingleComposer.GetSwitch(key);
            if (element != null) element.On = on;
        }

        private void SetSlider(string key, int value, int min, int max, SliderTooltipDelegate tooltip)
        {
            GuiElementSlider slider = SingleComposer.GetSlider(key);
            if (slider == null) return;

            slider.OnSliderTooltip = tooltip;
            slider.SetValues(value, min, max, 1);
        }

        private void SetNumberInput(string key, double value)
        {
            GuiElementNumberInput input = SingleComposer.GetNumberInput(key);
            if (input != null) input.SetValue(Fmt(value));
        }

        /// <summary>
        /// Static text elements have no setter, so the cheapest correct way to show the
        /// newly saved coordinates is to rebuild the dialog.
        /// </summary>
        private void RefreshSlotLabels() => Compose();

        private string SlotCoordsLabel(int slot)
        {
            SavedLocation loc = Config.Locations[slot];
            if (!loc.HasPosition) return "    (empty - stand somewhere and press Save)";

            // Show where Go will actually land, not the raw recorded position.
            Vec3d rel = system.SnappedRelativePosOf(loc);
            return $"    {rel.X:0.#}, {rel.Y:0.#}, {rel.Z:0.#}";
        }

        private static void ParseInto(string text, ref double target)
        {
            if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double v))
            {
                target = v;
            }
        }

        private static string Fmt(double v) => v.ToString("0.##", CultureInfo.InvariantCulture);

        /// <summary>A fly speed as the whole number the slider carries it in.</summary>
        private static int Tenths(double flySpeed)
            => (int)Math.Round(flySpeed * ModMenuConfig.FlySpeedSteps);

        private void OnTitleBarClose() => TryClose();

        // The menu is click-driven, so the cursor has to be free while it is open.
        public override bool PrefersUngrabbedMouse => true;
    }
}
