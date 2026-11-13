using System;

namespace MonsterModAlerter;

internal static class MetadataPaths
{
    /// <summary>
    /// Longest first, so Metadata/Critters/ is taken off before the bare Metadata/ that also matches it. The last one is
    /// a catch-all for everything else a Match Any Entity rule can pick up - chests, doors, shrines - which are all under
    /// Metadata/ too.
    /// </summary>
    private static readonly string[] CommonPrefixes = ["Metadata/Critters/", "Metadata/Monsters/", "Metadata/"];

    /// <summary>
    /// The metadata path with the part that is the same for everything removed, e.g. Metadata/Critters/Ferret/Ferret
    /// becomes Ferret/Ferret. What is left is what distinguishes one thing from another, which is the whole job of a name
    /// in the tally.
    /// </summary>
    public static string ShortNameOf(string path)
    {
        if (string.IsNullOrEmpty(path))
            return "<no path>";

        foreach (var prefix in CommonPrefixes)
        {
            if (path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                return path[prefix.Length..];
        }

        return path;
    }
}
