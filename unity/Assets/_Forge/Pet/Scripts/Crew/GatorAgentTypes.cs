using UnityEngine;

namespace Forge.Pet
{
    /// <summary>Hat shapes for crew gators (built from primitives by GatorCrewMember).</summary>
    public enum HatStyle { HardHat, Beanie, Antenna, Visor, Bandana, TopHat, CaptainCap, Bow, Helmet }

    /// <summary>One kind of crew gator: what it does (the hub has the prompt and tools) and how it looks.</summary>
    public sealed class GatorAgentType
    {
        public readonly string Id, Name, Role;
        public readonly Color Color;
        /// <summary>Size relative to Bit (1.0): every type is 10-15 % smaller or bigger.</summary>
        public readonly float Scale;
        public readonly HatStyle Hat;

        public GatorAgentType(string id, string name, string role, Color color, float scale, HatStyle hat)
        {
            Id = id; Name = name; Role = role; Color = color; Scale = scale; Hat = hat;
        }
    }

    /// <summary>
    /// The crew catalog. Ids, names and roles must match hub/agent/crew.py TYPES (the hub owns prompts and tools;
    /// pet_id == type id, so there is at most one gator per type). Bit himself is not in the list.
    /// </summary>
    public static class GatorAgentTypes
    {
        public static readonly GatorAgentType[] All =
        {
            new GatorAgentType("scout",    "Scout",    "maps the codebase and finds where things live", new Color(0.20f, 0.75f, 0.80f), 0.88f, HatStyle.Antenna),
            new GatorAgentType("builder",  "Builder",  "builds new features end to end",                new Color(1.00f, 0.62f, 0.12f), 1.12f, HatStyle.HardHat),
            new GatorAgentType("fixer",    "Fixer",    "hunts down bugs and fixes the root cause",      new Color(0.90f, 0.25f, 0.22f), 0.90f, HatStyle.Bandana),
            new GatorAgentType("tester",   "Tester",   "writes and runs tests",                         new Color(1.00f, 0.86f, 0.20f), 0.86f, HatStyle.Visor),
            new GatorAgentType("reviewer", "Reviewer", "reviews branches and diffs for bugs",           new Color(0.62f, 0.42f, 0.88f), 1.10f, HatStyle.Beanie),
            new GatorAgentType("scribe",   "Scribe",   "writes docs, READMEs and comments",             new Color(0.30f, 0.52f, 0.95f), 0.87f, HatStyle.TopHat),
            new GatorAgentType("shipper",  "Shipper",  "handles git, changelogs and pull request write-ups", new Color(0.95f, 0.95f, 0.97f), 1.14f, HatStyle.CaptainCap),
            new GatorAgentType("designer", "Designer", "tweaks the rocket CAD design",                  new Color(1.00f, 0.52f, 0.76f), 0.89f, HatStyle.Bow),
            new GatorAgentType("sentinel", "Sentinel", "audits the code for security problems",         new Color(0.18f, 0.24f, 0.45f), 1.15f, HatStyle.Helmet),
        };

        public static GatorAgentType Find(string idOrName)
        {
            if (string.IsNullOrEmpty(idOrName)) return null;
            for (int i = 0; i < All.Length; i++)
                if (string.Equals(All[i].Id, idOrName, System.StringComparison.OrdinalIgnoreCase)
                    || string.Equals(All[i].Name, idOrName, System.StringComparison.OrdinalIgnoreCase))
                    return All[i];
            return null;
        }

        public static int IndexOf(GatorAgentType t) => System.Array.IndexOf(All, t);
    }
}
