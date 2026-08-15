using System.Globalization;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModMenu
{
    /// <summary>
    /// The Ctrl+Shift+M window. Kept deliberately plain: a column of switches for the
    /// always-on toggles, a coordinate entry row, and three rename/save/go slots.
    /// </summary>
    public class ModMenuDialog : GuiDialog
    {
        private const double DialogWidth = 420;
        private const double RowHeight = 28;
        private const double RowGap = 6;
        private const double SwitchSize = 22;

        private readonly ModMenuSystem system;

        private double tpX, tpY, tpZ;

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

        private void Compose()
        {
            CairoFont font = CairoFont.WhiteSmallText();

            ElementBounds dialogBounds = ElementStdBounds.AutosizedMainDialog
                .WithAlignment(EnumDialogArea.CenterMiddle);

            ElementBounds bgBounds = ElementBounds.Fill.WithFixedPadding(GuiStyle.ElementToDialogPadding);
            bgBounds.BothSizing = ElementSizing.FitToChildren;

            double y = 0;

            GuiComposer composer = capi.Gui
                .CreateCompo("modmenu-main", dialogBounds)
                .AddShadedDialogBG(bgBounds)
                .AddDialogTitleBar("Mod Menu", OnTitleBarClose, font, null, null)
                .BeginChildElements(bgBounds);

            y += 32;

            // ---- toggles ----------------------------------------------------------

            composer = AddSectionHeader(composer, font, "Features", ref y);

            // Invincibility and durability are server-decided, so on a server without this mod
            // those two switches cannot do anything. Say so up front rather than letting them
            // look broken.
            if (!system.ServerHasMod)
            {
                const string notice = "Server does not have this mod - invincibility, durability "
                                    + "and drops at player are inactive.";
                CairoFont noticeFont = CairoFont.WhiteDetailText().WithColor(GuiStyle.ErrorTextColor);

                // The notice wraps to more than one line, so measure its wrapped height at the
                // current scale rather than reserving a single row and letting it clip into
                // the toggle below. GetMultilineTextHeight works in scaled pixels; bounds want
                // unscaled units, hence dividing the scale back out.
                double noticeHeight = capi.Gui.Text.GetMultilineTextHeight(
                    noticeFont, notice, GuiElement.scaled(DialogWidth)) / RuntimeEnv.GUIScale;

                composer.AddStaticText(notice, noticeFont,
                    ElementBounds.Fixed(0, y, DialogWidth, noticeHeight), null);
                y += noticeHeight + RowGap * 2;
            }

            // Server-authoritative toggles first: they only do anything when the server also
            // runs this mod. See the notice above when it does not.
            composer = AddToggleRow(composer, font, "Invincibility", "swInvincible", ref y,
                on =>
                {
                    Config.Invincible = on;
                    system.ApplyFeature(EnumFeature.Invincible, on);
                    system.SaveConfig();
                });

            composer = AddToggleRow(composer, font, "No durability loss", "swNoDurability", ref y,
                on =>
                {
                    Config.NoDurabilityLoss = on;
                    system.ApplyFeature(EnumFeature.NoDurability, on);
                    system.SaveConfig();
                });

            composer = AddToggleRow(composer, font, "Drops at player", "swDropsAtPlayer", ref y,
                on =>
                {
                    Config.DropsAtPlayer = on;
                    system.ApplyFeature(EnumFeature.DropsAtPlayer, on);
                    system.SaveConfig();
                });

            // Divider between the server-dependent toggles above and the ones below that work
            // on any server, mod or not.
            composer = AddDivider(composer, ref y);

            composer = AddToggleRow(composer, font, "Instant mine", "swInstantMine", ref y,
                on =>
                {
                    Config.InstantMine = on;
                    system.ApplyFeature(EnumFeature.InstantMine, on);
                    system.SaveConfig();
                });

            composer = AddToggleRow(composer, font, "Vein miner", "swVeinMiner", ref y,
                on =>
                {
                    Config.VeinMiner = on;
                    system.SaveConfig();
                });

            // Vein miner limit. Whole blocks, so the slider carries the value as it is.
            composer
                .AddStaticText("Blocks per vein", font, ElementBounds.Fixed(0, y, 200, RowHeight), null)
                .AddSlider(v =>
                {
                    Config.VeinMinerLimit = v;
                    veinLimitUnsaved = true;
                    return true;
                }, ElementBounds.Fixed(210, y + 4, 190, 20), "sldVeinLimit");

            y += RowHeight + RowGap;

            composer = AddToggleRow(composer, font, "AntiAbuse Safe", "swVeinBanSafe", ref y,
                on =>
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
                });

            // Divider between the mining group above and the movement group below.
            composer = AddDivider(composer, ref y);

            composer = AddToggleRow(composer, font, "Flight", "swFlight", ref y,
                on =>
                {
                    Config.Flight = on;
                    system.SaveConfig();
                });

            composer = AddToggleRow(composer, font, "No clip", "swNoClip", ref y,
                on =>
                {
                    Config.NoClip = on;
                    system.SaveConfig();
                });

            composer = AddToggleRow(composer, font, "No fall damage", "swNoFallDamage", ref y,
                on =>
                {
                    Config.NoFallDamage = on;
                    system.SaveConfig();
                });

            // Fly speed. A slider rather than a number box, because the ceiling here is a real
            // limit and not a preference - past 3 the fall catch can no longer keep a landing
            // gentle - and a text field can always be typed past whatever range it advertises.
            // The slider itself only carries whole numbers, so it counts tenths.
            composer
                .AddStaticText("Fly speed", font, ElementBounds.Fixed(0, y, 200, RowHeight), null)
                .AddSlider(v =>
                {
                    Config.FlySpeed = v / (double)ModMenuConfig.FlySpeedSteps;
                    flySpeedUnsaved = true;
                    return true;
                }, ElementBounds.Fixed(210, y + 4, 190, 20), "sldFlySpeed");

            y += RowHeight + RowGap;

            // Divider between the movement toggles above and fullbright below.
            composer = AddDivider(composer, ref y);

            composer = AddToggleRow(composer, font, "Fullbright", "swFullbright", ref y,
                on =>
                {
                    Config.Fullbright = on;
                    system.ApplyFullbright(on);
                    system.SaveConfig();
                });

            // Reach. Whole blocks on top of whatever the game gives you, so zero is "untouched".
            composer
                .AddStaticText("Reach", font, ElementBounds.Fixed(0, y, 200, RowHeight), null)
                .AddSlider(v =>
                {
                    Config.ReachBonus = v;
                    reachUnsaved = true;
                    return true;
                }, ElementBounds.Fixed(210, y + 4, 190, 20), "sldReach");

            y += RowHeight + RowGap;

            // Divider between fullbright above and the teleport controls below.
            composer = AddDivider(composer, ref y);

            // ---- teleport to coordinates -------------------------------------------

            composer = AddSectionHeader(composer, font, "Teleport to coordinates", ref y);

            composer
                .AddStaticText("X", font, ElementBounds.Fixed(0, y, 14, RowHeight), null)
                .AddNumberInput(ElementBounds.Fixed(16, y, 110, RowHeight),
                    t => ParseInto(t, ref tpX), font, "fdX")
                .AddStaticText("Y", font, ElementBounds.Fixed(140, y, 14, RowHeight), null)
                .AddNumberInput(ElementBounds.Fixed(156, y, 110, RowHeight),
                    t => ParseInto(t, ref tpY), font, "fdY")
                .AddStaticText("Z", font, ElementBounds.Fixed(280, y, 14, RowHeight), null)
                .AddNumberInput(ElementBounds.Fixed(296, y, 110, RowHeight),
                    t => ParseInto(t, ref tpZ), font, "fdZ");

            y += RowHeight + RowGap;

            composer.AddButton("Teleport", () =>
            {
                system.TeleportToRelative(tpX, tpY, tpZ);
                return true;
            }, ElementBounds.Fixed(0, y, DialogWidth, RowHeight + 4), EnumButtonStyle.Normal, "btnTeleport");

            y += RowHeight + RowGap * 3;

            // ---- saved locations ----------------------------------------------------

            composer = AddSectionHeader(composer, font, "Saved locations", ref y);

            for (int i = 0; i < 3; i++)
            {
                int slot = i; // capture per iteration, not the shared loop variable

                composer
                    .AddTextInput(ElementBounds.Fixed(0, y, 180, RowHeight),
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
                    }, ElementBounds.Fixed(190, y, 100, RowHeight + 4), EnumButtonStyle.Normal, "btnSave" + slot)
                    .AddButton("Go", () =>
                    {
                        system.TeleportToSaved(slot);
                        return true;
                    }, ElementBounds.Fixed(300, y, 100, RowHeight + 4), EnumButtonStyle.Normal, "btnGo" + slot);

                y += RowHeight + RowGap;

                composer.AddStaticText(SlotCoordsLabel(slot), CairoFont.WhiteDetailText(),
                    ElementBounds.Fixed(0, y, DialogWidth, 20), "txtCoords" + slot);

                y += 20 + RowGap;
            }

            SingleComposer = composer.EndChildElements().Compose();

            ApplyCurrentValues();
        }

        /// <summary>
        /// Pushes config state into the freshly composed widgets. Has to run after Compose,
        /// since the elements do not exist before that.
        /// </summary>
        private void ApplyCurrentValues()
        {
            SingleComposer.GetSwitch("swInvincible").On = Config.Invincible;
            SingleComposer.GetSwitch("swInstantMine").On = Config.InstantMine;
            SingleComposer.GetSwitch("swNoDurability").On = Config.NoDurabilityLoss;
            SingleComposer.GetSwitch("swFlight").On = Config.Flight;
            SingleComposer.GetSwitch("swNoClip").On = Config.NoClip;
            SingleComposer.GetSwitch("swNoFallDamage").On = Config.NoFallDamage;
            SingleComposer.GetSwitch("swFullbright").On = Config.Fullbright;
            SingleComposer.GetSwitch("swVeinMiner").On = Config.VeinMiner;
            SingleComposer.GetSwitch("swVeinBanSafe").On = Config.VeinMinerBanSafe;
            SingleComposer.GetSwitch("swDropsAtPlayer").On = Config.DropsAtPlayer;

            // The tooltip has to be in place before SetValues, which is what bakes the value
            // label into a texture. Leaving ShowTextWhenResting off keeps the speed out of the
            // slider track itself - it only appears in the bubble above the handle while that
            // is being dragged or hovered.
            GuiElementSlider flySpeed = SingleComposer.GetSlider("sldFlySpeed");
            flySpeed.OnSliderTooltip = v => Fmt(v / (double)ModMenuConfig.FlySpeedSteps) + "x";
            flySpeed.SetValues(
                Tenths(Config.FlySpeed),
                Tenths(ModMenuConfig.MinFlySpeed),
                Tenths(ModMenuConfig.MaxFlySpeed),
                1);

            GuiElementSlider veinLimit = SingleComposer.GetSlider("sldVeinLimit");
            veinLimit.OnSliderTooltip = v => v + (v == 1 ? " block" : " blocks");
            veinLimit.SetValues(
                Config.VeinMinerLimit,
                ModMenuConfig.MinVeinMinerLimit,
                ModMenuConfig.MaxVeinMinerLimit,
                1);

            GuiElementSlider reach = SingleComposer.GetSlider("sldReach");
            reach.OnSliderTooltip = v => v == 0 ? "normal" : "+" + v + " blocks";
            reach.SetValues(
                Config.ReachBonus,
                ModMenuConfig.MinReachBonus,
                ModMenuConfig.MaxReachBonus,
                1);

            SingleComposer.GetNumberInput("fdX").SetValue(Fmt(tpX));
            SingleComposer.GetNumberInput("fdY").SetValue(Fmt(tpY));
            SingleComposer.GetNumberInput("fdZ").SetValue(Fmt(tpZ));

            for (int i = 0; i < 3; i++)
            {
                SingleComposer.GetTextInput("tfName" + i).SetValue(Config.Locations[i].Name);
            }
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

        private GuiComposer AddSectionHeader(GuiComposer composer, CairoFont font, string title, ref double y)
        {
            composer.AddStaticText(title, font.Clone().WithWeight(Cairo.FontWeight.Bold),
                ElementBounds.Fixed(0, y, DialogWidth, RowHeight), null);
            y += RowHeight;
            return composer;
        }

        /// <summary>A thin engraved inset used as a horizontal separator between toggle groups.</summary>
        private GuiComposer AddDivider(GuiComposer composer, ref double y)
        {
            y += RowGap;
            composer.AddInset(ElementBounds.Fixed(0, y, DialogWidth, 4), 3);
            y += 4 + RowGap * 2;
            return composer;
        }

        private GuiComposer AddToggleRow(GuiComposer composer, CairoFont font, string label, string key,
            ref double y, System.Action<bool> onToggle)
        {
            composer
                .AddStaticText(label, font, ElementBounds.Fixed(0, y, 300, RowHeight), null)
                .AddSwitch(onToggle, ElementBounds.Fixed(320, y, SwitchSize, SwitchSize), key, SwitchSize, 4);

            y += RowHeight + RowGap;
            return composer;
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
            => (int)System.Math.Round(flySpeed * ModMenuConfig.FlySpeedSteps);

        private void OnTitleBarClose() => TryClose();

        // The menu is click-driven, so the cursor has to be free while it is open.
        public override bool PrefersUngrabbedMouse => true;
    }
}
