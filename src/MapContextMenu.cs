using System;
using System.Collections.Generic;
using Vintagestory.API.Client;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModMenu
{
    /// <summary>
    /// Small popup list shown where the player right-clicked the world map.
    ///
    /// Vintage Story has no context-menu widget of its own, so this is a plain GuiDialog
    /// positioned at the cursor with one button per entry, drawn above the map dialog.
    /// </summary>
    public class MapContextMenu : GuiDialog
    {
        /// <summary>Floor for the row height; the font's own line height wins if it is taller.</summary>
        private const double MinRowHeight = 26;

        private const double MinMenuWidth = 120;

        /// <summary>Space either side of a label inside its button.</summary>
        private const double LabelSidePadding = 24;

        private const double RowGap = 3;

        private readonly List<(string Label, Action OnClick)> entries = new List<(string, Action)>();
        private double cursorX, cursorY;

        public MapContextMenu(ICoreClientAPI capi) : base(capi) { }

        public override string ToggleKeyCombinationCode => null;

        // Must sit above the world map dialog, which is what opened it.
        public override double DrawOrder => 0.9;

        public override bool PrefersUngrabbedMouse => true;

        public void Show(double screenX, double screenY, List<(string, Action)> items)
        {
            entries.Clear();
            entries.AddRange(items);
            cursorX = screenX;
            cursorY = screenY;

            if (IsOpened()) TryClose();
            Compose();
            TryOpen();
        }

        private const double Padding = 6;

        private void Compose()
        {
            // Every bound here is an explicit fixed size. Mixing ElementBounds.Fill with
            // FitToChildren inside a cursor-positioned dialog produces a background rectangle
            // that can sit partly off the composed surface, and GuiElementDialogBackground
            // hands those numbers straight to Cairo's blur without clamping them:
            //
            //   SurfaceTransformBlur.BlurPartial(surface, r, .., (int)Bounds.bgDrawX, ..,
            //                                    (int)Bounds.OuterWidth, ..)
            //
            // which then reads outside the pixel buffer and takes the process down with an
            // AccessViolationException - a native fault no try/catch can rescue.
            //
            // AddDialogBG (rather than AddShadedDialogBG) sets Shade = false, so the blur
            // never runs at all. A popup this small has nothing to gain from it anyway.
            double scale = RuntimeEnv.GUIScale;

            // The default button font is the large decorative one, which overflows a compact
            // popup, so drop to a small font and size the box to the text rather than to a
            // guessed width. GetTextExtents measures at the *scaled* font size while
            // ElementBounds.Fixed expects unscaled units, hence dividing the scale back out.
            CairoFont font = CairoFont.ButtonText().WithFontSize((float)GuiStyle.SmallFontSize);

            double rowHeight = Math.Max(MinRowHeight, font.GetFontExtents().Height / scale + 8);

            double menuWidth = MinMenuWidth;
            foreach (var entry in entries)
            {
                double textWidth = font.GetTextExtents(entry.Label).Width / scale;
                menuWidth = Math.Max(menuWidth, textWidth + LabelSidePadding);
            }

            double innerHeight = entries.Count * rowHeight + (entries.Count - 1) * RowGap;
            double boxWidth = menuWidth + Padding * 2;
            double boxHeight = innerHeight + Padding * 2;

            // Keep the whole menu on screen, so a right-click near an edge still draws fully.
            double maxX = Math.Max(0, capi.Render.FrameWidth - boxWidth * scale);
            double maxY = Math.Max(0, capi.Render.FrameHeight - boxHeight * scale);
            double x = GameMath.Clamp(cursorX, 0, maxX) / scale;
            double y = GameMath.Clamp(cursorY, 0, maxY) / scale;

            ElementBounds dialogBounds = ElementBounds.Fixed(x, y, boxWidth, boxHeight);
            ElementBounds bgBounds = ElementBounds.Fixed(0, 0, boxWidth, boxHeight);

            GuiComposer composer = capi.Gui
                .CreateCompo("modmenu-mapcontext", dialogBounds)
                .AddDialogBG(bgBounds, false)
                .BeginChildElements(bgBounds);

            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                composer.AddButton(entry.Label, () =>
                {
                    TryClose();
                    entry.OnClick();
                    return true;
                }, ElementBounds.Fixed(Padding, Padding + i * (rowHeight + RowGap), menuWidth, rowHeight),
                   font, EnumButtonStyle.Normal, "btn" + i);
            }

            SingleComposer = composer.EndChildElements().Compose();
        }

        /// <summary>Clicking anywhere off the menu dismisses it, as a context menu should.</summary>
        public override void OnMouseDown(MouseEvent args)
        {
            if (SingleComposer != null && !SingleComposer.Bounds.PointInside(args.X, args.Y))
            {
                TryClose();
                return;
            }

            base.OnMouseDown(args);
        }
    }
}
