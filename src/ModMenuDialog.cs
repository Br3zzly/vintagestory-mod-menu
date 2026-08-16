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

        private static readonly string[] TabNames = { "Player", "Movement", "Mining", "ESP", "Teleport" };

        /// <summary>
        /// Geometry of the two side-by-side lists on the ESP tab. Wide enough for the colour
        /// square on the right of an active target without the name losing room for it - the
        /// dialog sizes itself to its contents, so this widens the window rather than the rows.
        /// </summary>
        private const double EspListWidth = 310;
        private const double EspListHeight = 300;
        private const double EspScrollbarWidth = 20;
        private const double EspListGap = 24;

        /// <summary>
        /// Matches shown at once. Not a search limit - the matching itself is a walk over
        /// precomputed strings and costs nothing. This is a drawing limit: every row is a text
        /// texture composed through Cairo when the list is filled, and a couple of hundred of
        /// those is what made typing stutter. Anything past this is narrowed by another word.
        /// </summary>
        private const int EspMaxResults = 50;

        /// <summary>
        /// How long to wait after the last keystroke before refilling the list. Typing
        /// "copper" is six keystrokes; without this it is six full refills, five of them
        /// thrown away before anyone reads them.
        /// </summary>
        private const int EspSearchDebounceMs = 180;

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

        /// <summary>And again, for the ESP range.</summary>
        private bool espRangeUnsaved;

        /// <summary>Current text in the ESP search box.</summary>
        private string espSearch = "";

        /// <summary>Pending refill from the search box, cancelled by the next keystroke.</summary>
        private long espSearchDebounceId;

        /// <summary>
        /// Bounds of the two lists and their clipped windows, kept because scrolling and
        /// refilling both need to recalculate them and hand the scrollbar new heights.
        /// </summary>
        private ElementBounds espResultsClip, espActiveClip;

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

            // Otherwise it fires into a composer that is no longer on screen.
            if (espSearchDebounceId != 0)
            {
                capi.Event.UnregisterCallback(espSearchDebounceId);
                espSearchDebounceId = 0;
            }

            if (flySpeedUnsaved || veinLimitUnsaved || reachUnsaved || espRangeUnsaved)
            {
                system.SaveConfig();
                flySpeedUnsaved = false;
                veinLimitUnsaved = false;
                reachUnsaved = false;
                espRangeUnsaved = false;
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
                case 3: return EspRows(font);
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

                // Switched off here, fullbright still stays on while the ESP tab is hiding the
                // world - that needs it, and says so regardless of what this switch reads.
                ToggleRow(font, "Fullbright", "swFullbright", on =>
                {
                    Config.Fullbright = on;
                    system.SyncFullbright();
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

        /// <summary>
        /// The ESP tab: a switch, a range slider, a search box, and two scrollable lists -
        /// matches on the left, what is being outlined on the right.
        ///
        /// The rows are shaped after the survival handbook's list - a picture and a name, lit up
        /// under the pointer - which is what GuiElementEspList draws.
        ///
        /// Three details in the plumbing are load bearing and were each wrong in an earlier
        /// attempt: ForkContainingChild makes a child bounds inside the inset (ForkBoundingParent
        /// makes a parent and rewrites the receiver, which recurses until the stack dies); the
        /// scrollbar has to be told both heights afterwards or it does not scroll; and typing
        /// refills the existing list in place rather than rebuilding the dialog, which is what
        /// kept stealing focus from the search box after every letter.
        /// </summary>
        private List<Row> EspRows(CairoFont font)
        {
            return new List<Row>
            {
                ToggleRow(font, "Enable ESP", "swEsp", on =>
                {
                    Config.Esp = on;
                    system.SaveConfig();
                }),

                SliderRow(font, "ESP range", "sldEspRange", v =>
                {
                    Config.EspRange = v;
                    espRangeUnsaved = true;
                    return true;
                }),

                // Takes effect on the next frame: the renderer reads it while deciding what to
                // draw, so nothing needs to be pushed anywhere from here.
                ToggleRow(font, "Transparent world", "swTransparentWorld", on =>
                {
                    Config.TransparentWorld = on;
                    system.SaveConfig();
                }),

                new Row(RowHeight + RowGap, delegate (GuiComposer c, double x, ref double y)
                {
                    c.AddTextInput(ElementBounds.Fixed(x, y, EspListWidth, RowHeight),
                        OnEspSearchChanged, font, "tfEspSearch");

                    c.AddStaticText("Active targets", font.Clone().WithWeight(Cairo.FontWeight.Bold),
                        ElementBounds.Fixed(x + EspListWidth + EspScrollbarWidth + EspListGap, y,
                            EspListWidth, RowHeight),
                        null);

                    y += RowHeight + RowGap;
                }),

                new Row(EspListHeight + RowGap, delegate (GuiComposer c, double x, ref double y)
                {
                    ElementBounds resultsInset = ElementBounds.Fixed(x, y, EspListWidth, EspListHeight);
                    ElementBounds activeInset = ElementBounds.Fixed(
                        x + EspListWidth + EspScrollbarWidth + EspListGap, y, EspListWidth, EspListHeight);

                    AddEspList(c, resultsInset, "espResults", SearchCells(), out espResultsClip);
                    AddEspList(c, activeInset, "espActive", ActiveCells(), out espActiveClip);

                    y += EspListHeight + RowGap;
                })
            };
        }

        /// <summary>An inset frame, a scrollbar beside it, and a clipped list of rows inside.</summary>
        private void AddEspList(GuiComposer composer, ElementBounds inset, string key,
            List<EspListRow> rows, out ElementBounds clip)
        {
            ElementBounds clipBounds = inset.ForkContainingChild(3, 3, 3, 3);
            ElementBounds listBounds = clipBounds.ForkContainingChild(0, 0, 0, -3).WithFixedPadding(5);

            composer
                .AddInset(inset)
                .AddVerticalScrollbar(value => OnEspScroll(key, value),
                    ElementStdBounds.VerticalScrollbar(inset), key + "Scroll")
                .BeginClip(clipBounds)
                .AddEspList(listBounds,
                    (index, onSwatch) => OnEspRowClicked(key, index, onSwatch),
                    index => OnEspRowHovered(key, index),
                    rows, key)
                .EndClip()

                // Outside the clip, or the tooltip would be cut off at the list's own edge. Its
                // bounds are a copy of the inset's rather than the inset's own, since the two
                // elements move them independently while drawing.
                .AddHoverText("", CairoFont.WhiteSmallText(), (int)EspListWidth,
                    inset.FlatCopy(), key + "Hover");

            clip = clipBounds;
        }

        /// <summary>
        /// Runs a row's action. Rows are handed out by index rather than by reference because
        /// that is what the list reports, and it is read back here rather than captured so a
        /// refilled list cannot run the action of a row that is no longer there.
        /// </summary>
        private void OnEspRowClicked(string key, int index, bool onSwatch)
        {
            GuiElementEspList element = SingleComposer?.GetEspList(key);
            if (element == null || index < 0 || index >= element.Rows.Count) return;

            EspListRow row = element.Rows[index];

            if (onSwatch) row.OnSwatchClick?.Invoke();
            else row.OnClick?.Invoke();
        }

        /// <summary>
        /// Offers the whole name of the row the pointer has settled on, but only when the row is
        /// showing a shortened one - a tooltip repeating a name already fully readable is just
        /// something in the way. An empty text is how the hover element stays hidden.
        /// </summary>
        private void OnEspRowHovered(string key, int index)
        {
            GuiElementHoverText hover = SingleComposer?.GetHoverText(key + "Hover");
            if (hover == null) return;

            GuiElementEspList element = SingleComposer.GetEspList(key);
            bool inRange = element != null && index >= 0 && index < element.Rows.Count;

            EspListRow row = inRange ? element.Rows[index] : null;

            hover.SetNewText(row != null && row.Trimmed ? row.Name : "");
        }

        private void SizeEspList(string key, ElementBounds clip)
        {
            GuiElementEspList element = SingleComposer?.GetEspList(key);
            if (element == null || clip == null) return;

            SingleComposer.GetScrollbar(key + "Scroll")
                ?.SetHeights((float)clip.fixedHeight, (float)element.insideBounds.fixedHeight);

            // A tooltip belongs beside the pointer rather than at the corner of the list, which
            // is where it would sit otherwise.
            SingleComposer.GetHoverText(key + "Hover")?.SetFollowMouse(true);
        }

        /// <summary>
        /// Scrolling moves the surface the rows are drawn on, not the element - the element is
        /// the window they are seen through. The three pixels match the inset's own edge.
        /// </summary>
        private void OnEspScroll(string key, float value)
        {
            GuiElementEspList element = SingleComposer?.GetEspList(key);
            if (element == null) return;

            element.insideBounds.fixedY = 3 - value;
            element.insideBounds.CalcWorldBounds();
        }

        /// <summary>
        /// Refills a list in place. Rebuilding the dialog instead is what made the search box
        /// lose focus after each letter, since the element being typed into was destroyed.
        /// </summary>
        private void RefillEspList(string key, List<EspListRow> rows, ElementBounds clip)
        {
            GuiElementEspList element = SingleComposer?.GetEspList(key);
            if (element == null || clip == null) return;

            element.Reload(rows);

            // The list just changed height, so the scrollbar needs to hear about it.
            SingleComposer.GetScrollbar(key + "Scroll")
                ?.SetHeights((float)clip.fixedHeight, (float)element.insideBounds.fixedHeight);
        }

        /// <summary>
        /// Typing only schedules the refill. Every keystroke cancels the one before it, so a
        /// burst of them costs a single refill once the typing pauses.
        /// </summary>
        private void OnEspSearchChanged(string text)
        {
            if (text == espSearch) return;

            espSearch = text;

            if (espSearchDebounceId != 0) capi.Event.UnregisterCallback(espSearchDebounceId);

            espSearchDebounceId = capi.Event.RegisterCallback(_ =>
            {
                espSearchDebounceId = 0;
                RefillEspList("espResults", SearchCells(), espResultsClip);
            }, EspSearchDebounceMs);
        }

        /// <summary>
        /// Both lists change when a target is added or removed, so both are refilled - but not
        /// straight away. This is called from a cell's click handler, which runs while the cell
        /// list is walking its own cells; refilling there pulls the collection out from under
        /// that loop and throws. Queueing it means the click finishes first.
        /// </summary>
        private void RefreshEspLists()
        {
            capi.Event.EnqueueMainThreadTask(() =>
            {
                RefillEspList("espResults", SearchCells(), espResultsClip);
                RefillEspList("espActive", ActiveCells(), espActiveClip);
            }, "modmenu-esp-refresh");
        }

        private List<EspListRow> SearchCells()
        {
            var cells = new List<EspListRow>();
            EspCatalogue catalogue = system.EspCatalogue;

            if (!catalogue.Ready)
            {
                cells.Add(new EspListRow { Name = "Still preparing the block list..." });
                return cells;
            }

            string query = espSearch?.Trim() ?? "";
            if (query.Length < EspCatalogue.MinSearchLength)
            {
                cells.Add(new EspListRow
                {
                    Name = "Type at least " + EspCatalogue.MinSearchLength + " letters"
                });
                return cells;
            }

            // Anything already being outlined is left out of the results rather than marked as
            // added: it is one hash lookup per candidate, and a row you cannot usefully click
            // is just noise in a list you are scanning.
            var active = new HashSet<string>();
            foreach (EspGroup group in Config.EspTargets)
            {
                if (group?.Name != null) active.Add(group.Name);
            }

            foreach (EspGroup group in catalogue.Search(query, EspMaxResults, active))
            {
                EspGroup picked = group;

                cells.Add(RowFor(picked, () =>
                {
                    system.AddEspTarget(picked);
                    RefreshEspLists();
                }));
            }

            if (cells.Count == 0) cells.Add(new EspListRow { Name = "No matches" });

            return cells;
        }

        private List<EspListRow> ActiveCells()
        {
            var cells = new List<EspListRow>();

            foreach (EspGroup group in Config.EspTargets)
            {
                EspGroup stored = group;

                EspListRow row = RowFor(stored, () =>
                {
                    system.RemoveEspTarget(stored.Name);
                    RefreshEspLists();
                });

                // Only the active side carries a colour, and clicking it steps to the next one
                // rather than removing the target the way the rest of the row does.
                row.Swatch = stored.Color;
                row.OnSwatchClick = () =>
                {
                    system.CycleEspTargetColor(stored.Name);
                    RefreshEspLists();
                };

                cells.Add(row);
            }

            return cells;
        }

        private EspListRow RowFor(EspGroup group, Action onClick)
        {
            return new EspListRow
            {
                Name = group.Name,
                Icon = IconFor(group),
                OnClick = onClick
            };
        }

        /// <summary>
        /// What to draw beside the name. A block is a stack of itself; a creature borrows the
        /// "creature-" item the game keeps for spawning it, which is how the survival handbook
        /// comes to show a gazelle rather than a blank square. Null when neither exists, and the
        /// row simply has no picture.
        /// </summary>
        private ItemStack IconFor(EspGroup group)
        {
            if (group?.Codes == null || group.Codes.Length == 0) return null;

            var code = new AssetLocation(group.Codes[0]);

            if (group.IsBlock)
            {
                Block block = capi.World.GetBlock(code);
                return block == null || block.Id == 0 ? null : new ItemStack(block);
            }

            Item item = capi.World.GetItem(new AssetLocation(code.Domain, "creature-" + code.Path));
            return item == null ? null : new ItemStack(item);
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
            SetSwitch("swEsp", Config.Esp);
            SetSwitch("swTransparentWorld", Config.TransparentWorld);

            SetSlider("sldEspRange", Config.EspRange,
                ModMenuConfig.MinEspRange, ModMenuConfig.MaxEspRange,
                v => v + " blocks");

            GuiElementTextInput search = SingleComposer.GetTextInput("tfEspSearch");
            if (search != null) search.SetValue(espSearch);

            // A cell list only knows how tall it is once its bounds have been worked out, and
            // the scrollbar only knows how far it may travel once told both heights.
            SizeEspList("espResults", espResultsClip);
            SizeEspList("espActive", espActiveClip);

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
