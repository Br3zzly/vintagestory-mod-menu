using System;
using System.Collections.Generic;
using Cairo;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Config;
using Vintagestory.API.MathTools;

namespace ModMenu
{
    /// <summary>
    /// One line of an ESP list: a picture of the thing, its name, and a word on the right saying
    /// which kind it is.
    ///
    /// The picture is drawn the way the survival handbook draws its own rows - an item stack
    /// rendered live into the GUI rather than a flat image, which is why blocks arrive with their
    /// real textures and creatures with their models. Blocks become a stack of themselves;
    /// creatures borrow the "creature-" item the game keeps for spawning them, which is the same
    /// thing the handbook lists them by.
    ///
    /// The textures are built on the first frame the row is drawn and belong to it afterwards,
    /// so a list that is refilled has to dispose the rows it drops.
    /// </summary>
    public class EspListRow
    {
        /// <summary>How big the picture is drawn, and the gap kept to the left of it.</summary>
        private const int IconSize = 25;
        private const int IconPad = 10;

        /// <summary>Gap between the picture and the name.</summary>
        private const int TextGap = 25;

        /// <summary>Kept clear at the right edge, so a trimmed name does not touch the frame.</summary>
        private const int RightPad = 10;

        /// <summary>The colour square on the right, and the space it takes with its margin.</summary>
        private const int SwatchSize = 20;
        private const int SwatchGap = 12;

        /// <summary>Stands in for whatever had to be cut off a name too long for its row.</summary>
        private const string Ellipsis = "...";

        public string Name;

        /// <summary>What to draw on the left, or null when there is nothing to show.</summary>
        public ItemStack Icon;

        /// <summary>
        /// The colour square on the right, packed as the renderer wants it, or zero for a row
        /// that has no colour of its own - the search results and the placeholder lines.
        /// </summary>
        public int Swatch;

        public Action OnClick;

        /// <summary>Run instead of <see cref="OnClick"/> when the colour square is the thing hit.</summary>
        public Action OnSwatchClick;

        /// <summary>
        /// True once the name turned out to be too long for its row and was cut short. The list
        /// offers the whole thing on hover, and only for these.
        /// </summary>
        public bool Trimmed { get; private set; }

        private LoadedTexture nameTexture;
        private LoadedTexture swatchTexture;
        private DummySlot slot;
        private ElementBounds iconBounds;

        public void Render(ICoreClientAPI capi, double x, double y, double width)
        {
            if (nameTexture == null) Compose(capi, width);

            float size = (float)GuiElement.scaled(IconSize);
            float pad = (float)GuiElement.scaled(IconPad);

            if (slot != null)
            {
                // Clipped to its own square: an item stack is drawn by the world renderer, which
                // has no idea it is inside a scrolling list and would otherwise spill out of it.
                iconBounds.fixedX = (pad + x - size / 2) / RuntimeEnv.GUIScale;
                iconBounds.fixedY = (y - size / 2) / RuntimeEnv.GUIScale;
                iconBounds.CalcWorldBounds();

                if (iconBounds.InnerWidth > 0 && iconBounds.InnerHeight > 0)
                {
                    capi.Render.PushScissor(iconBounds, true);
                    capi.Render.RenderItemstackToGui(slot,
                        x + pad + size / 2, y + size / 2, 100, size, ColorUtil.WhiteArgb,
                        true, false, false);
                    capi.Render.PopScissor();
                }
            }

            capi.Render.Render2DTexturePremultipliedAlpha(nameTexture.TextureId,
                x + size + GuiElement.scaled(TextGap),
                y + size / 4 - GuiElement.scaled(3),
                nameTexture.Width, nameTexture.Height);

            if (swatchTexture == null) return;

            double swatch = GuiElement.scaled(SwatchSize);

            capi.Render.Render2DTexturePremultipliedAlpha(swatchTexture.TextureId,
                SwatchLeft(x, width), y + size / 2 - swatch / 2, swatch, swatch);
        }

        /// <summary>
        /// Where the colour square starts. Drawing and hit testing both come through here, so
        /// the square you click is by construction the square you can see.
        /// </summary>
        private static double SwatchLeft(double x, double width)
        {
            return x + width - GuiElement.scaled(SwatchSize) - GuiElement.scaled(RightPad);
        }

        /// <summary>Whether a pointer position is on this row's colour square.</summary>
        public bool HitsSwatch(double pointerX, double x, double width)
        {
            return Swatch != 0 && pointerX >= SwatchLeft(x, width);
        }

        /// <summary>
        /// Builds the row's texture, sized to the space it actually has. A name is one line and
        /// stays one line, so a name too long for the row is cut rather than run past its edge.
        /// </summary>
        private void Compose(ICoreClientAPI capi, double width)
        {
            CairoFont font = CairoFont.WhiteSmallText();

            double available = width
                - GuiElement.scaled(IconSize) - GuiElement.scaled(TextGap)
                - GuiElement.scaled(RightPad);

            if (Swatch != 0)
            {
                available -= GuiElement.scaled(SwatchSize) + GuiElement.scaled(SwatchGap);
                ComposeSwatch(capi);
            }

            string fitted = Fit(Name, text => font.GetTextExtents(text).Width, available);
            Trimmed = fitted != Name;

            nameTexture = new TextTextureUtil(capi).GenTextTexture(fitted, font);

            if (Icon != null)
            {
                slot = new DummySlot(Icon);
                iconBounds = ElementBounds.FixedSize(50, 50);
                iconBounds.ParentBounds = capi.Gui.WindowBounds;
            }
        }

        /// <summary>
        /// The colour square: a filled rounded box with a dark edge so a pale colour still has
        /// an outline against the row. Its channels are pulled apart by hand because the value
        /// is packed for a mesh - red in the lowest byte - and the ColorUtil helpers that take
        /// an int read the other packing.
        /// </summary>
        private void ComposeSwatch(ICoreClientAPI capi)
        {
            int side = (int)GuiElement.scaled(SwatchSize);

            var surface = new ImageSurface(Format.Argb32, side, side);
            var ctx = new Context(surface);

            ctx.SetSourceRGBA(
                (Swatch & 0xFF) / 255.0,
                ((Swatch >> 8) & 0xFF) / 255.0,
                ((Swatch >> 16) & 0xFF) / 255.0,
                1);
            GuiElement.RoundRectangle(ctx, 0, 0, side, side, 2);
            ctx.Fill();

            ctx.SetSourceRGBA(0, 0, 0, 0.6);
            GuiElement.RoundRectangle(ctx, 0, 0, side, side, 2);
            ctx.LineWidth = 2;
            ctx.Stroke();

            swatchTexture = new LoadedTexture(capi);
            capi.Gui.LoadOrUpdateCairoTexture(surface, true, ref swatchTexture);

            ctx.Dispose();
            surface.Dispose();
        }

        /// <summary>
        /// The longest leading part of the text that fits, with an ellipsis where the rest was.
        /// Found by halving rather than by stepping back a letter at a time: names run to thirty
        /// characters and more, and every guess costs a measurement through Cairo.
        /// </summary>
        internal static string Fit(string text, System.Func<string, double> widthOf, double maxWidth)
        {
            if (string.IsNullOrEmpty(text)) return "";
            if (widthOf(text) <= maxWidth) return text;

            int longest = 0;
            int shortest = 0, tooLong = text.Length;

            while (shortest < tooLong)
            {
                int middle = (shortest + tooLong + 1) / 2;

                if (widthOf(Shorten(text, middle)) <= maxWidth)
                {
                    longest = shortest = middle;
                }
                else
                {
                    tooLong = middle - 1;
                }
            }

            return longest == 0 ? Ellipsis : Shorten(text, longest);
        }

        private static string Shorten(string text, int length)
        {
            return text.Substring(0, length).TrimEnd() + Ellipsis;
        }

        public void Dispose()
        {
            nameTexture?.Dispose();
            nameTexture = null;

            swatchTexture?.Dispose();
            swatchTexture = null;
        }
    }

    /// <summary>
    /// A scrolling list of <see cref="EspListRow"/>, shaped after the survival handbook's own
    /// list rather than the menu cells this used to use: a row is a picture and a name, lit up
    /// under the pointer, instead of a full width button.
    ///
    /// It draws its rows itself rather than composing a widget per row, which is what makes the
    /// pictures possible - and what leaves room to give a row more than one thing to click,
    /// since a hit is resolved from the pointer against whatever the row drew.
    /// </summary>
    public class GuiElementEspList : GuiElement
    {
        public const int CellHeight = 40;
        public const int CellSpacing = 5;

        /// <summary>
        /// How far the hit and hover bands sit above the row's drawing position. The rows are
        /// drawn from their text baseline rather than their top corner, so without this the
        /// highlight would sit low by about a fifth of a row.
        /// </summary>
        private const int CellYPad = 8;

        public List<EspListRow> Rows = new List<EspListRow>();

        /// <summary>
        /// The scrolled inner surface. Its height is the full list; moving its Y is what
        /// scrolling does.
        /// </summary>
        public ElementBounds insideBounds;

        /// <summary>
        /// How long the pointer has to rest on one row before it is reported as settled. Sweeping
        /// the list should not fire a tooltip for every row passed over on the way.
        /// </summary>
        private const float HoverDelaySeconds = 0.4f;

        /// <summary>
        /// Told which row was clicked, and whether the click landed on its colour square rather
        /// than anywhere else on it.
        /// </summary>
        private readonly Action<int, bool> onClick;

        /// <summary>
        /// Told which row the pointer has settled on, and -1 when it leaves one. Fired on the
        /// change only, never per frame.
        /// </summary>
        private readonly Action<int> onHover;

        private LoadedTexture hoverTexture;
        private bool pressedInside;

        private int hovered = -1;
        private float hoverHeld;
        private bool hoverReported;

        public GuiElementEspList(ICoreClientAPI capi, ElementBounds bounds, Action<int, bool> onClick,
            Action<int> onHover, List<EspListRow> rows) : base(capi, bounds)
        {
            this.onClick = onClick;
            this.onHover = onHover;
            if (rows != null) Rows = rows;

            hoverTexture = new LoadedTexture(capi);

            insideBounds = new ElementBounds().WithFixedPadding(CellSpacing).WithEmptyParent();
            insideBounds.CalcWorldBounds();

            MeasureRows();
        }

        /// <summary>The height of every row together, which is what the scrollbar needs.</summary>
        public void MeasureRows()
        {
            insideBounds.fixedHeight = Rows.Count * (CellHeight + CellSpacing) + CellSpacing;
        }

        /// <summary>
        /// Swaps in a new set of rows. The old ones are disposed here: each holds textures it
        /// built for itself, and a search that refills this on every keystroke would otherwise
        /// leak one set per letter typed.
        /// </summary>
        public void Reload(List<EspListRow> rows)
        {
            foreach (EspListRow row in Rows) row.Dispose();

            Rows = rows ?? new List<EspListRow>();
            MeasureRows();

            // The row under the pointer is a different row now, whatever its number was.
            ForgetHover();
        }

        public override void ComposeElements(Context ctxStatic, ImageSurface surfaceStatic)
        {
            insideBounds.CalcWorldBounds();
            MeasureRows();
            Bounds.CalcWorldBounds();

            // The band that follows the pointer: plain translucent white, drawn over whichever
            // row it is on.
            var surface = new ImageSurface(Format.Argb32,
                (int)Bounds.InnerWidth, (int)scaled(CellHeight));
            var ctx = new Context(surface);

            ctx.SetSourceRGBA(1, 1, 1, 0.3);
            ctx.Paint();

            generateTexture(surface, ref hoverTexture);

            ctx.Dispose();
            surface.Dispose();
        }

        public override void RenderInteractiveElements(float deltaTime)
        {
            double y = insideBounds.absY;
            double height = scaled(CellHeight);
            double ypad = scaled(CellYPad);

            bool pointerHere = Bounds.ParentBounds.PointInside(api.Input.MouseX, api.Input.MouseY);
            int under = -1;

            for (int i = 0; i < Rows.Count; i++)
            {
                double rowY = Bounds.absY + y + 5;

                // Rows scrolled out of sight are skipped: with a few hundred search results,
                // building a texture for each would cost far more than the handful on screen.
                if (y > -height && y < Bounds.OuterHeight + height)
                {
                    if (pointerHere && Hits(api.Input.MouseX, api.Input.MouseY, rowY))
                    {
                        under = i;

                        api.Render.Render2DLoadedTexture(hoverTexture,
                            (float)Bounds.absX, (float)(rowY - ypad));
                    }

                    Rows[i].Render(api, Bounds.absX, rowY, Bounds.InnerWidth);
                }

                y += scaled(CellHeight + CellSpacing);
            }

            TrackHover(under, deltaTime);
        }

        /// <summary>
        /// Reports the row the pointer has come to rest on, once it has been there long enough.
        /// Moving off a row withdraws it straight away - a tooltip left over a different row is
        /// worse than none.
        /// </summary>
        private void TrackHover(int under, float deltaTime)
        {
            if (under != hovered)
            {
                hovered = under;
                hoverHeld = 0;

                if (hoverReported)
                {
                    hoverReported = false;
                    onHover?.Invoke(-1);
                }

                return;
            }

            if (hoverReported || hovered < 0) return;

            hoverHeld += deltaTime;
            if (hoverHeld < HoverDelaySeconds) return;

            hoverReported = true;
            onHover?.Invoke(hovered);
        }

        private void ForgetHover()
        {
            hovered = -1;
            hoverHeld = 0;

            if (!hoverReported) return;

            hoverReported = false;
            onHover?.Invoke(-1);
        }

        /// <summary>Whether a point is on the row drawn at <paramref name="rowY"/>.</summary>
        private bool Hits(double x, double y, double rowY)
        {
            double ypad = scaled(CellYPad);

            return x > Bounds.absX && x <= Bounds.absX + Bounds.InnerWidth
                && y >= rowY - ypad && y <= rowY + scaled(CellHeight) - ypad;
        }

        public override void OnMouseDownOnElement(ICoreClientAPI api, MouseEvent args)
        {
            if (!Bounds.ParentBounds.PointInside(args.X, args.Y)) return;

            base.OnMouseDownOnElement(api, args);
            pressedInside = true;
        }

        /// <summary>
        /// Acts on release rather than on press, and only when the press started here too, so a
        /// drag of the scrollbar that ends over the list does not count as picking a row.
        /// </summary>
        public override void OnMouseUpOnElement(ICoreClientAPI api, MouseEvent args)
        {
            if (!pressedInside) return;
            pressedInside = false;

            if (!Bounds.ParentBounds.PointInside(args.X, args.Y)) return;

            double y = insideBounds.absY;

            for (int i = 0; i < Rows.Count; i++)
            {
                if (Hits(api.Input.MouseX, api.Input.MouseY, Bounds.absY + y + 5))
                {
                    bool onSwatch = Rows[i].HitsSwatch(
                        api.Input.MouseX, Bounds.absX, Bounds.InnerWidth);

                    if (onSwatch ? Rows[i].OnSwatchClick != null : Rows[i].OnClick != null)
                    {
                        api.Gui.PlaySound("menubutton_press");
                        onClick?.Invoke(i, onSwatch);
                        args.Handled = true;
                    }

                    return;
                }

                y += scaled(CellHeight + CellSpacing);
            }
        }

        public override void Dispose()
        {
            base.Dispose();

            hoverTexture?.Dispose();
            foreach (EspListRow row in Rows) row.Dispose();
        }
    }

    public static class EspListComposer
    {
        public static GuiComposer AddEspList(this GuiComposer composer, ElementBounds bounds,
            Action<int, bool> onClick, Action<int> onHover, List<EspListRow> rows, string key)
        {
            if (!composer.Composed)
            {
                composer.AddInteractiveElement(
                    new GuiElementEspList(composer.Api, bounds, onClick, onHover, rows), key);
            }

            return composer;
        }

        public static GuiElementEspList GetEspList(this GuiComposer composer, string key)
        {
            return composer.GetElement(key) as GuiElementEspList;
        }
    }
}
