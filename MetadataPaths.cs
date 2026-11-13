using System;

namespace MonsterModAlerter;

internal static class MetadataPaths
{
    /// <summary>
    /// Longest first, so Metadata/Critters/ is taken off before the bare Metadata/ that also matches it. The last one
    /// is a catch-all for everything else a Match any entity rule can pick up - chests, doors, shrines - which are all
    /// under Metadata/ too.
    /// </summary>
    private static readonly string[] CommonPrefixes = ["Metadata/Critters/", "Metadata/Monsters/", "Metadata/"];

    /// <summary>
    /// The metadata path with the part that is the same for everything removed, e.g. Metadata/Critters/Ferret/Ferret
    /// becomes Ferret/Ferret. What is left is what distinguishes one thing from another, which is the whole job of a
    /// name in the tally.
    /// </summary>
    /// <summary>
    /// The path without a trailing level tag, e.g. Metadata/Monsters/VaalForgeMan/VaalForgeMan@83 becomes
    /// Metadata/Monsters/VaalForgeMan/VaalForgeMan, so a path copied into a rule matches the monster at every level.
    /// The tag is an @ and digits at the end; a path without one is returned as it is.
    /// </summary>
    public static string WithoutLevelTag(string path) => LevelTagOf(path) is { } tag ? path[..^tag.Length] : path;

    /// <summary>The trailing level tag, e.g. "@83", or null when the path has none.</summary>
    public static string LevelTagOf(string path)
    {
        if (string.IsNullOrEmpty(path))
            return null;

        var at = path.LastIndexOf('@');

        if (at < 0 || at == path.Length - 1)
            return null;

        for (var i = at + 1; i < path.Length; i++)
        {
            if (!char.IsAsciiDigit(path[i]))
                return null;
        }

        return path[at..];
    }

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
