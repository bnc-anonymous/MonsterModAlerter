using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;

namespace MonsterModAlerter;

/// <summary>
/// Tests one entity at a time against the enabled alert rules. Made once per scan, with that scan's rules and switches,
/// and holds the scratch lists the scan refills for every entity.
/// </summary>
internal sealed class RuleMatcher
{
    /// <summary>
    /// Prefix ObjectMagicProperties.Mods uses for a rolled rare-monster affix, e.g. MonsterModEnrage. Only these are
    /// counted - see BestMatchOf.
    /// </summary>
    public const string RareModPrefix = "MonsterMod";

    private readonly AlertRule[] _rules;
    private readonly bool _drawMultipleMatchingIcons;

    /// <summary>Whether any rule searches buffs, so whether the Buffs component is read at all.</summary>
    private readonly bool _readBuffs;

    /// <summary>The entity's mod names and buff names, gathered once and tested against every rule that asks.</summary>
    private readonly List<string> _modNames = new(16);

    private readonly List<string> _buffNames = new(16);

    /// <summary>Case-insensitive, since the same affix has been seen differing only in case.</summary>
    private readonly HashSet<string> _countedNames = new(StringComparer.OrdinalIgnoreCase);

    private readonly List<RuleMatch> _matchesOfEntity = new(4);

    public RuleMatcher(AlertRule[] rules, bool drawMultipleMatchingIcons)
    {
        _rules = rules;
        _drawMultipleMatchingIcons = drawMultipleMatchingIcons;

        foreach (var rule in rules)
        {
            if (rule.SearchBuffs && rule.Terms.Length > 0)
            {
                _readBuffs = true;
                break;
            }
        }
    }

    /// <summary>
    /// Every rule that matched the entity last passed to BestMatchOf, in rule order. Filled only when Draw multiple
    /// matching icons is on.
    /// </summary>
    public List<RuleMatch> MatchesOfEntity => _matchesOfEntity;

    /// <summary>
    /// Returns the highest-priority rule matching this entity, so one entity yields one icon rather than a stack of
    /// overlapping ones. A rule matches only when every one of its comma-separated terms is found somewhere on the
    /// entity.
    /// </summary>
    /// <param name="monsterOk">
    /// Whether this entity qualifies as a monster this scan: hostile, alive and past the global rarity filter. Rules
    /// that were not asked to look further than monsters are skipped when it is false.
    /// </param>
    /// <param name="anyOk">Whether Match any entity rules should be offered this entity.</param>
    public RuleMatch BestMatchOf(Entity entity, bool monsterOk, bool anyOk, bool checkAlive)
    {
        _matchesOfEntity.Clear();

        // Gather the entity's mod and buff names once, then test each rule against the lists it searches - cheaper than
        // re-reading components per rule.
        _modNames.Clear();
        _buffNames.Clear();
        _countedNames.Clear();

        // Counted here rather than in a second pass, so the number comes from the same read as the matching. Modifiers
        // only: buffs come and go with auras and charges, and folding them in would make the number flicker as you
        // moved.
        var modCount = 0;

        if (entity.TryGetComponent<ObjectMagicProperties>(out var magicProps) && magicProps?.Mods is { } mods)
        {
            foreach (var mod in mods)
            {
                if (string.IsNullOrEmpty(mod))
                    continue;

                // Only the rare-monster modifiers proper, and only once each. The list carries entries that are not
                // rolled affixes at all, and repeats some of the ones that are - both inflate the number past what the
                // monster's own tooltip shows.
                if (mod.StartsWith(RareModPrefix, StringComparison.OrdinalIgnoreCase) && _countedNames.Add(mod))
                    modCount++;

                // Every entry is searchable, not only the counted ones, since a term may well name one of the others.
                _modNames.Add(mod);
            }
        }

        if (_readBuffs && entity.TryGetComponent<Buffs>(out var buffs) && buffs?.BuffsList is { } buffList)
        {
            foreach (var buff in buffList)
            {
                if (!string.IsNullOrEmpty(buff?.Name))
                    _buffNames.Add(buff.Name);
            }
        }

        // No early-out on an empty name list: a rule can match on the path filter alone, and such a rule must still
        // fire for a monster carrying no mods or buffs at all.
        RuleMatch best = null;
        var entityId = entity.Id;
        var path = entity.Path;

        foreach (var rule in _rules)
        {
            var anyEntity = rule.AnyEntity?.Value == true;

            // Each rule sees the entities its own target covers: monster rules only the things that passed the monster
            // gates, Match any entity rules everything else as well.
            if (anyEntity ? !anyOk : !monsterOk)
                continue;

            // Per-rule rarity intersects with the global filter, so a rule can only narrow it. Skipped alongside the
            // global one for a rule that is not asking for monsters, for the same reason: a chest has no rarity to
            // pass.
            if (!anyEntity && !rule.Rarity.Allows(entity.Rarity))
                continue;

            // Already holding something this rule cannot outrank. Skipped when collecting every match, where losing on
            // priority is not a reason to be left out.
            if (!_drawMultipleMatchingIcons && best != null && rule.Priority.Value <= best.Rule.Priority.Value)
                continue;

            if (rule.PathFilter?.Matches(path) == false)
                continue;

            var terms = rule.Terms;

            // No term means the path filter alone decides, so what to label it with is the filter itself rather than a
            // mod name.
            if (terms.Length == 0)
            {
                if (rule.PathFilter?.IsBlank != false)
                    continue;

                // With no term there is nothing to count matches of, so the minimum applies to the rare-affix count -
                // the same number this rule would be showing.
                if (modCount < rule.ModCountMinimum.Value)
                    continue;

                best = Keep(new RuleMatch(entity, entityId, rule, rule.PathFilter.Describe(), modCount, checkAlive));
                continue;
            }

            var patterns = rule.Patterns;

            // A pattern that would not compile disarms the rule. Matching everything instead would paint the whole
            // screen; matching by the raw text would silently mean something else.
            if (rule.UseRegex && patterns.Length != terms.Length)
                continue;

            string display = null;
            var allPresent = true;

            for (var i = 0; i < terms.Length; i++)
            {
                // Mods before buffs, as the rule ticks them; the first name found labels the match.
                string hit = null;

                if (rule.SearchMods)
                    hit = FirstNameMatching(_modNames, rule, terms[i], patterns, i);

                if (hit == null && rule.SearchBuffs)
                    hit = FirstNameMatching(_buffNames, rule, terms[i], patterns, i);

                // Most things that are not monsters carry no mods and no buffs, so their metadata path is the only
                // thing a term can match. Offered to these rules only: adding it for monster rules would quietly widen
                // every rule already written.
                if (hit == null && anyEntity && PathMatches(path, rule, terms[i], patterns, i))
                    hit = MetadataPaths.ShortNameOf(path);

                if (hit == null)
                {
                    allPresent = false;
                    break;
                }

                display = display == null ? hit : $"{display} + {hit}";
            }

            if (allPresent)
            {
                var shown = rule.CountSearchMatches
                    ? CountSearchMatches(rule, terms, patterns, anyEntity ? path : null)
                    : modCount;

                // Applied here rather than at draw time so it gates the whole rule - icon, number, sound and remembered
                // mark together - instead of leaving an unexplained icon with no number beside it.
                if (shown < rule.ModCountMinimum.Value)
                    continue;

                best = Keep(new RuleMatch(entity, entityId, rule, display, shown, checkAlive));
            }
        }

        return best;

        // Every match is collected when asked for; `best` still tracks the highest priority one, which is what decides
        // the remembered icon and the alert sound.
        RuleMatch Keep(RuleMatch match)
        {
            if (_drawMultipleMatchingIcons)
                _matchesOfEntity.Add(match);

            return best == null || match.Rule.Priority.Value > best.Rule.Priority.Value ? match : best;
        }
    }

    /// <summary>
    /// How many of the entity's distinct mod and buff names this rule's terms match, in the lists the rule searches.
    ///
    /// Any term counts, not all of them: the terms are ANDed to decide whether the rule fires at all, and by this point
    /// it has. Asking "how many match" then means how many entries any of them picked out, which for the usual
    /// single-term rule is exactly the number of hits.
    /// </summary>
    /// <param name="path">
    /// The entity's metadata path when this rule searches it too (Match any entity), otherwise null. Counted like any
    /// other value it matched, so a critter rule reads 1 rather than 0.
    /// </param>
    private int CountSearchMatches(AlertRule rule, string[] terms, Regex[] patterns, string path)
    {
        // Safe to borrow: the rare-affix count that used this is already final by now. Cleared per call because several
        // rules may each ask, and the game repeats entries in the mod list.
        _countedNames.Clear();

        var count = 0;

        if (rule.SearchMods)
            count += CountNamesMatching(_modNames, rule, terms, patterns);

        if (rule.SearchBuffs)
            count += CountNamesMatching(_buffNames, rule, terms, patterns);

        if (path != null && _countedNames.Add(path))
        {
            for (var i = 0; i < terms.Length; i++)
            {
                if (PathMatches(path, rule, terms[i], patterns, i))
                {
                    count++;
                    break;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// Whether one of a rule's terms matches the entity's metadata path, by the same rules the mod and buff names are
    /// matched by - substring or regex, case-insensitive either way.
    /// </summary>
    private static bool PathMatches(string path, AlertRule rule, string term, Regex[] patterns, int index)
    {
        if (string.IsNullOrEmpty(path))
            return false;

        return rule.UseRegex
            ? index < patterns.Length && patterns[index].IsMatch(path)
            : path.Contains(term, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The distinct names in the list any of the rule's terms match, not yet counted for this rule (_countedNames).
    /// </summary>
    private int CountNamesMatching(List<string> names, AlertRule rule, string[] terms, Regex[] patterns)
    {
        var count = 0;

        foreach (var name in names)
        {
            for (var i = 0; i < terms.Length; i++)
            {
                if (NameMatches(name, rule, terms[i], patterns, i))
                {
                    if (_countedNames.Add(name))
                        count++;

                    break;
                }
            }
        }

        return count;
    }

    /// <summary>First name in the list that the term matches, or null.</summary>
    private static string FirstNameMatching(
        List<string> names, AlertRule rule, string term, Regex[] patterns, int index)
    {
        foreach (var name in names)
        {
            if (NameMatches(name, rule, term, patterns, index))
                return name;
        }

        return null;
    }

    /// <summary>
    /// Whether a term matches a mod or buff name: as a regex with Use regex on, unanchored, so it may match anywhere in
    /// the name - the same reach a plain term has as a substring. OrdinalIgnoreCase avoids allocating a lowercased copy
    /// of every name each scan.
    /// </summary>
    private static bool NameMatches(string name, AlertRule rule, string term, Regex[] patterns, int index) =>
        rule.UseRegex
            ? index < patterns.Length && patterns[index].IsMatch(name)
            : name.Contains(term, StringComparison.OrdinalIgnoreCase);
}
