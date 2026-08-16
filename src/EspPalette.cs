using System.Collections.Generic;
using Vintagestory.API.MathTools;

namespace ModMenu
{
    /// <summary>
    /// The colours ESP hands out, in the order it hands them out.
    ///
    /// The order is the point. Targets take the next colour never used, so picking five things
    /// gets the first five - yellow, blue, green, magenta, orange - which are about as far apart
    /// as colours get. Picking twenty reaches the end of the list, where the colours are still
    /// distinct but no longer obviously so, because there is no way to have it both ways.
    ///
    /// No red anywhere in it. Red belongs to the vein miner's preview, and a colour that means
    /// "this is about to be mined" must not also mean "this is granite".
    ///
    /// Packed with ColorFromRgba rather than ToRgba: a mesh's colours are read by the shader as
    /// four bytes in buffer order, and ToRgba packs them the other way round - it is the ARGB
    /// form, whose lowest byte is blue. Getting that wrong swaps red and blue in everything.
    /// </summary>
    public static class EspPalette
    {
        /// <summary>Fully opaque. The blocks are meant to read as solid colour.</summary>
        private const int Alpha = 255;

        private static readonly int[] Colours =
        {
            Rgb(255, 230,  30),   // yellow
            Rgb( 40, 110, 255),   // blue
            Rgb( 30, 210,  70),   // green
            Rgb(230,  70, 220),   // magenta
            Rgb(255, 150,  20),   // orange
            Rgb( 40, 225, 230),   // cyan
            Rgb(140,  60, 230),   // purple
            Rgb(180, 250,  40),   // lime
            Rgb(255, 150, 210),   // pink
            Rgb( 20, 150, 145),   // teal
            Rgb(130, 200, 255),   // sky
            Rgb(150, 150,  30),   // olive
            Rgb(120, 255, 180),   // mint
            Rgb( 80,  70, 200),   // indigo
            Rgb(215, 180, 120),   // tan
            Rgb( 40, 130,  90),   // sea green
            Rgb(195, 175, 255),   // lavender
            Rgb(140,  95,  45),   // brown
            Rgb(120, 145, 175),   // slate
            Rgb(235, 235, 160)    // pale yellow
        };

        /// <summary>
        /// What the vein miner's preview is drawn in, and the one colour no target can be given.
        /// </summary>
        public static readonly int VeinRed = Rgb(255, 45, 45);

        public static int Count => Colours.Length;

        public static int At(int index)
        {
            return Colours[GameMath.Mod(index, Colours.Length)];
        }

        /// <summary>
        /// The first colour nobody is using, so the earliest and most distinct ones are always
        /// taken first - including after something is removed and its colour comes free again.
        /// Past twenty everything is in use and colours start repeating, which is the honest
        /// outcome of asking for more colours than there are.
        /// </summary>
        public static int FirstUnused(ICollection<int> taken)
        {
            foreach (int colour in Colours)
            {
                if (!taken.Contains(colour)) return colour;
            }

            return Colours[taken.Count % Colours.Length];
        }

        /// <summary>The next one along, for stepping through them by hand.</summary>
        public static int After(int colour)
        {
            for (int i = 0; i < Colours.Length; i++)
            {
                if (Colours[i] == colour) return Colours[(i + 1) % Colours.Length];
            }

            return Colours[0];
        }

        public static bool Contains(int colour)
        {
            foreach (int c in Colours)
            {
                if (c == colour) return true;
            }

            return false;
        }

        private static int Rgb(int r, int g, int b)
        {
            return ColorUtil.ColorFromRgba(r, g, b, Alpha);
        }
    }
}
