using System;
using System.Collections.Generic;
using System.Linq;
using Vintagestory.API.Client;
using Vintagestory.API.Common;
using Vintagestory.API.Common.Entities;
using Vintagestory.API.Config;

namespace ModMenu
{
    /// <summary>
    /// One thing ESP can be pointed at, as the player thinks of it: "Native copper ore", not
    /// the twenty block codes that phrase covers - one per host rock.
    /// </summary>
    public class EspGroup
    {
        public string Name;

        /// <summary>Every block or entity code this name covers.</summary>
        public string[] Codes = new string[0];

        public bool IsBlock;

        /// <summary>
        /// What this one is drawn in, packed the way a mesh wants it - see EspPalette. Handed
        /// out when the target is added and kept in the config afterwards, so a colour someone
        /// chose stays chosen. Zero means one has not been picked yet.
        /// </summary>
        public int Color;
    }

    /// <summary>
    /// Everything ESP can be pointed at, merged by display name and prepared once when the
    /// world finishes loading.
    ///
    /// The shape of this follows BlockOverlay's search cache, after three attempts of my own
    /// went wrong in ways it had already solved:
    ///
    ///   - names are resolved once here, not per visible row. Lang.GetMatching falls back to a
    ///     wildcard scan plus a regex match per entry on a miss, so thousands of those while a
    ///     tab composes is seconds of freeze.
    ///   - entries are grouped by that name, which is why one "Native copper ore" appears
    ///     instead of twenty near-identical rows.
    ///   - searching matches the name, because that is what people type. Codes are matched too,
    ///     but nobody looks for "tallgrass-veryshort" when they mean grass.
    ///   - lowercase forms are precomputed, so a keystroke costs a Contains over a few thousand
    ///     short strings rather than any string building at all.
    /// </summary>
    public class EspCatalogue
    {
        /// <summary>
        /// Below this a search matches most of the catalogue, which is neither useful nor
        /// cheap to render. BlockOverlay draws the same line.
        /// </summary>
        public const int MinSearchLength = 2;

        private List<EspGroup> groups;
        private string[] namesLower;
        private string[] codesLower;

        public bool Ready => groups != null;

        public int Count => groups?.Count ?? 0;

        /// <summary>
        /// Walks every block and entity type the world knows. Runs once, when the world is
        /// finished loading - never while a GUI is composing.
        /// </summary>
        public void Build(ICoreClientAPI capi)
        {
            // name -> the codes that name covers. This is the merge: every host rock variant of
            // native copper resolves to the same display name and lands in the same bucket.
            var blocks = new Dictionary<string, List<string>>();
            var creatures = new Dictionary<string, List<string>>();

            foreach (Block block in capi.World.Blocks)
            {
                if (block?.Code == null || block.Id == 0 || block.IsMissing) continue;
                Bucket(blocks, BlockName(block), block.Code.ToString());
            }

            foreach (EntityProperties type in capi.World.EntityTypes)
            {
                if (type?.Code == null) continue;
                Bucket(creatures,
                    NameOf(type.Code, "item-creature-") ?? type.Code.ToString(),
                    type.Code.ToString());
            }

            var built = new List<EspGroup>(blocks.Count + creatures.Count);
            foreach (var pair in blocks) built.Add(GroupOf(pair, isBlock: true));
            foreach (var pair in creatures) built.Add(GroupOf(pair, isBlock: false));

            built.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // Parallel arrays rather than fields on the group, so a keystroke walks two flat
            // string arrays instead of chasing references. Normalised once here, so a search
            // never has to touch the originals.
            namesLower = built.Select(g => Normalise(g.Name)).ToArray();
            codesLower = built.Select(g => Normalise(string.Join(" ", g.Codes))).ToArray();
            groups = built;
        }

        private static void Bucket(Dictionary<string, List<string>> into, string name, string code)
        {
            if (!into.TryGetValue(name, out List<string> codes))
            {
                codes = new List<string>();
                into[name] = codes;
            }

            codes.Add(code);
        }

        private static EspGroup GroupOf(KeyValuePair<string, List<string>> pair, bool isBlock)
        {
            return new EspGroup { Name = pair.Key, Codes = pair.Value.ToArray(), IsBlock = isBlock };
        }

        /// <summary>
        /// Every word of the query has to appear somewhere in the name, in any order - so
        /// "grass short" finds "Grass (short)", and "copper ore" finds "Native copper ore".
        /// Punctuation is flattened away on both sides, which is what lets a bare "short" match
        /// the "(short)" in the name. Falls back to matching codes for anyone who types one.
        ///
        /// Empty until the search is at least <see cref="MinSearchLength"/> characters: below
        /// that nearly everything matches, which is neither useful nor cheap to draw.
        /// </summary>
        /// <param name="exclude">
        /// Names to leave out - what is already being outlined. Filtered inside the scan rather
        /// than afterwards, so a full page of results still comes back once some are hidden,
        /// and it costs one hash lookup per candidate.
        /// </param>
        public List<EspGroup> Search(string query, int max, HashSet<string> exclude = null)
        {
            var found = new List<EspGroup>();
            if (groups == null) return found;

            string normalised = Normalise(query);
            if (normalised.Length < MinSearchLength) return found;

            string[] words = normalised.Split(' ');

            for (int i = 0; i < groups.Count && found.Count < max; i++)
            {
                if (exclude != null && exclude.Contains(groups[i].Name)) continue;

                if (MatchesAll(namesLower[i], words) || MatchesAll(codesLower[i], words))
                {
                    found.Add(groups[i]);
                }
            }

            return found;
        }

        private static bool MatchesAll(string haystack, string[] words)
        {
            foreach (string word in words)
            {
                if (word.Length > 0 && !haystack.Contains(word)) return false;
            }

            return true;
        }

        /// <summary>
        /// Lowercase, with everything that is not a letter or digit turned into a space. Both
        /// the stored text and the query go through this, so brackets, dashes and colons stop
        /// mattering on either side.
        /// </summary>
        private static string Normalise(string text)
        {
            if (string.IsNullOrWhiteSpace(text)) return "";

            var builder = new System.Text.StringBuilder(text.Length);
            bool lastWasSpace = true;

            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                {
                    builder.Append(char.ToLowerInvariant(c));
                    lastWasSpace = false;
                }
                else if (!lastWasSpace)
                {
                    builder.Append(' ');
                    lastWasSpace = true;
                }
            }

            return builder.ToString().Trim();
        }

        /// <summary>
        /// The name the game shows for a block. The language file answers for nearly all of
        /// them; where it does not, the block is asked directly, which is what the tooltip does
        /// and therefore always right - just far too slow to be the first choice for thousands
        /// of entries. Both only ever run while building.
        /// </summary>
        private static string BlockName(Block block)
        {
            string name = NameOf(block.Code, "block-");
            if (name != null) return name;

            try
            {
                string held = block.GetHeldItemName(new ItemStack(block));
                if (!string.IsNullOrWhiteSpace(held)) return held;
            }
            catch (Exception)
            {
                // Blocks that cannot be held, or want world context to name themselves.
            }

            return block.Code.ToString();
        }

        /// <summary>The localised name, or null when the language files have no entry.</summary>
        private static string NameOf(AssetLocation code, string prefix)
        {
            string key = code.Domain + ":" + prefix + code.Path;

            try
            {
                string name = Lang.GetMatching(key);
                return string.IsNullOrWhiteSpace(name) || name == key ? null : name;
            }
            catch (Exception)
            {
                return null;
            }
        }
    }
}
