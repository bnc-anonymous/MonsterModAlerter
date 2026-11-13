using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Color = SharpDX.Color;
using Vector2N = System.Numerics.Vector2;

namespace MonsterModAlerter;

/// <summary>
/// One watched mod/buff term plus how it should be presented. Rendered by the HUD as an
/// "Add Item" list, so each term gets its own icon, size, colour and sound.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class AlertRule
{
    public AlertRule()
    {
        // Wired here (not in an initializer) so it is re-attached after JSON deserialization.
        IconPicker = new IconPickerNode(this);
    }

    public static AlertRule Default() => new AlertRule
    {
        SearchTerm = new TextNode("TormentHauntedMonsterThief"),
        Icon = MapIconsIndex.CorpseTypeUndead,
    };

    [Menu("Enabled")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [Menu("Search Term",
        "Case-insensitive substring match against mod and buff names. Comma-separated terms are " +
        "ANDed: \"mod1,mod2\" only matches a monster carrying BOTH, so you can flag dangerous " +
        "combinations that are harmless individually. Use a separate rule for either/or. " +
        "Leave blank to match on the monster filter alone, i.e. flag a kind of monster whatever " +
        "modifiers it happens to have.")]
    public TextNode SearchTerm { get; set; } = new TextNode("");

    [Menu("Wildcards (regex)",
        "Treat each search term as a .NET regular expression instead of plain text, so " +
        "\"MonsterMod.*Touched\" matches any Touched variant. Commas still separate terms and are " +
        "still ANDed, so a comma cannot be used inside a pattern. Matching stays case-insensitive " +
        "and unanchored, i.e. the pattern may match anywhere in the mod name. A pattern that will " +
        "not compile disarms the rule rather than matching everything, and says so in its label.")]
    public ToggleNode UseRegex { get; set; } = new ToggleNode(false);

    [Menu("Match Any Entity",
        "Off (default): this rule only looks at hostile monsters, and both rarity filters apply. " +
        "On: it is tested against every entity the game has loaded - critters, chests, doors, " +
        "shrines, objects - with the hostile and rarity checks skipped, since those are monster " +
        "concepts and would throw all of these away. The entity's metadata path is searched as " +
        "well as any mods and buffs it has, because most non-monster entities carry neither, so " +
        "a search term of \"Metadata/Critters\" flags every critter. The Monster Filter below " +
        "matches the same path, so either will do.")]
    public ToggleNode AnyEntity { get; set; } = new ToggleNode(false);

    [Menu("Monster Filter")]
    public MetadataFilterSettings Metadata { get; set; } = new MetadataFilterSettings();

    [Menu("Label", "Optional text drawn in the world. Leave blank to show the matched mod name.")]
    public TextNode Label { get; set; } = new TextNode("");

    [Menu("Rarity Filter",
        "Narrows the global filter for this rule only — a monster must pass both. New rules start " +
        "at Rare + Unique; tick Normal/Magic here to widen this rule (the global filter still applies).")]
    public RarityFilterSettings Rarity { get; set; } = RarityFilterSettings.RareAndUnique();

    /// <summary>Serialized by name so the settings JSON stays readable and survives enum reordering.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public MapIconsIndex Icon = MapIconsIndex.CorpseTypeUndead;

    [Menu("Icon")]
    [JsonIgnore]
    public IconPickerNode IconPicker { get; set; }

    [Menu("Minimap Icon Size", "Size of this rule's icon on the minimap / large map, in pixels.")]
    public RangeNode<int> IconSize { get; set; } = new RangeNode<int>(32, 4, 100);

    [Menu("World Icon Size", "Size of this rule's icon drawn over the monster in the world, in pixels.")]
    public RangeNode<int> WorldIconSize { get; set; } = new RangeNode<int>(64, 4, 256);

    [Menu("Tint")]
    public ColorNode Tint { get; set; } = new ColorNode(Color.White);

    [Menu("Priority", "Draw order among this plugin's own icons. Higher = on top.")]
    public RangeNode<int> Priority { get; set; } = new RangeNode<int>(0, -100, 100);

    [Menu("Show On Minimap")]
    public ToggleNode ShowOnMinimap { get; set; } = new ToggleNode(true);

    [Menu("Show In World")]
    public ToggleNode ShowInWorld { get; set; } = new ToggleNode(true);

    [Menu("Count In Seen This Map",
        "Include what this rule matches in the Seen This Map counter. Off by default, so a rule " +
        "you wrote to be warned about something does not also become a list of it: tick this only " +
        "on the rules you actually want tallied. The counter itself is switched on under Seen " +
        "This Map Counter.")]
    public ToggleNode CountInSeenPanel { get; set; } = new ToggleNode(false);

    [Menu("Remember Position",
        "Keep this rule's icon on the map after the monster drops out of range, at the spot it was " +
        "last seen, so you can tell at a glance where the ones you have not dealt with are. " +
        "Drawn exactly like a live one, so bear in mind it is a last known position rather than " +
        "proof the monster is still there. Cleared when you change area, and dropped as soon as " +
        "the monster is seen dead.")]
    public ToggleNode RememberPosition { get; set; } = new ToggleNode(false);

    [Menu("Show Rare Modifier Count",
        "Draw how many modifiers this monster has on the minimap, as a bare number like \"12\". " +
        "Counts distinct rolled rare-monster affixes only — the MonsterMod entries in " +
        "ObjectMagicProperties.Mods, ignoring repeats, since the game lists some of them more " +
        "than once — so the number matches what the monster's own tooltip shows. " +
        "Buffs are excluded too: they come and go with auras and charges and would make it " +
        "flicker. Turn on Debug Overlay to see the full list and which entries were counted. " +
        "Appearance is under Mod Count Display.")]
    public ToggleNode ShowModCount { get; set; } = new ToggleNode(false);

    [Menu("Count Search Term Matches Instead",
        "Show how many of the monster's modifiers match this rule's search term, rather than how " +
        "many rare affixes it has. With \"MonsterMod.*Touched\" and wildcards on, a monster with " +
        "Fire Touched and Cold Touched reads 2 rather than its full affix count. Distinct entries " +
        "only, and buffs count too if Match Buffs is on, since those are what the term was tested " +
        "against. Ignored on a rule with no search term, which falls back to the rare count.")]
    public ToggleNode CountSearchMatches { get; set; } = new ToggleNode(false);

    [Menu("Count Minimum",
        "Below this count the rule does not fire at all: no icon, no number, no sound. Applies " +
        "whether or not the number itself is being shown, so it doubles as a filter — \"only flag " +
        "monsters with at least this many\". Counts whichever number this rule is showing, rare " +
        "affixes or search-term matches. 0 disables it.")]
    public RangeNode<int> ModCountMinimum { get; set; } = new RangeNode<int>(0, 0, 50);

    [Menu("Count Colour")]
    public ColorNode ModCountColor { get; set; } = new ColorNode(Color.White);

    [Menu("Play Sound")]
    public ToggleNode PlaySound { get; set; } = new ToggleNode(true);

    [Menu("Sound File", "Name (no extension) of a file in the HUD 'Sounds' folder. 'attention' is the spoken warning clip.")]
    public TextNode SoundFile { get; set; } = new TextNode("attention");

    private string _cachedTermSource;
    private string[] _terms = Array.Empty<string>();

    /// <summary>
    /// SearchTerm split on commas. All of these must be present on a monster for the rule to fire.
    /// Re-split only when the text actually changes, since this is read on every scan.
    /// </summary>
    [JsonIgnore]
    public string[] Terms
    {
        get
        {
            var raw = SearchTerm?.Value ?? string.Empty;
            if (!string.Equals(raw, _cachedTermSource, StringComparison.Ordinal))
            {
                _cachedTermSource = raw;
                _terms = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                _patterns = null;                       // rebuilt lazily against the new terms
                _cachedPatternSource = null;
            }

            return _terms;
        }
    }

    private string _cachedPatternSource;
    private bool _cachedPatternRegex;
    private Regex[] _patterns;
    private string _patternError;

    /// <summary>
    /// <see cref="Terms"/> compiled as regexes, or null when this rule matches by plain substring.
    /// Compiled once per edit rather than per scan, since a scan tests every term against every
    /// mod on every monster.
    /// </summary>
    [JsonIgnore]
    public Regex[] Patterns
    {
        get
        {
            var terms = Terms;                          // may invalidate the cache below
            var useRegex = UseRegex?.Value == true;

            if (_patterns != null && _cachedPatternRegex == useRegex &&
                string.Equals(_cachedPatternSource, _cachedTermSource, StringComparison.Ordinal))
            {
                return _patterns;
            }

            _cachedPatternSource = _cachedTermSource;
            _cachedPatternRegex = useRegex;
            _patternError = null;

            if (!useRegex)
            {
                _patterns = Array.Empty<Regex>();
                return _patterns;
            }

            var built = new List<Regex>(terms.Length);

            foreach (var term in terms)
            {
                try
                {
                    built.Add(new Regex(term, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
                }
                catch (ArgumentException e)
                {
                    // A bad pattern must not throw on the scan thread, and must not quietly match
                    // everything either — the rule is disarmed and says so in its label.
                    _patternError = e.Message;
                    built.Clear();
                    break;
                }
            }

            _patterns = built.ToArray();
            return _patterns;
        }
    }

    /// <summary>Why the pattern would not compile, or null when it is fine. Read after Patterns.</summary>
    [JsonIgnore]
    public string PatternError
    {
        get
        {
            _ = Patterns;
            return _patternError;
        }
    }

    // Trailing "###" keeps ImGui's element id stable while the visible label changes as you type.
    public override string ToString()
    {
        // A rule with no term is valid when the monster filter carries it, so name that instead of
        // reporting the rule as incomplete.
        var term = string.IsNullOrWhiteSpace(SearchTerm?.Value)
            ? (Metadata?.IsBlank == false ? Metadata.Describe() : "<no term>")
            : SearchTerm.Value;

        // A rule that cannot compile its pattern matches nothing, which is indistinguishable from
        // "no such monster here" unless it is said out loud.
        if (PatternError != null)
            term = $"{term}  [BAD REGEX]";

        // A rule that is not restricted to monsters behaves differently enough - no rarity filter,
        // and it searches the metadata path - that the list should say so without opening it.
        if (AnyEntity?.Value == true)
            term = $"[any] {term}";

        return $"{(Enabled?.Value == true ? "" : "(off) ")}{term}###";
    }

    /// <summary>
    /// Uses ExileCore's own IconButton/IconPickerWindow helpers — the same ones MinimapIcons uses —
    /// so the grid layout, filtering and sizing match the rest of the HUD.
    /// </summary>
    [Submenu(RenderMethod = nameof(Render))]
    public class IconPickerNode(AlertRule rule)
    {
        private string _filter = "";
        private bool _shown;

        public void Render()
        {
            var tint = (rule.Tint?.Value ?? Color.White).ToImguiVec4();

            if (ImGuiHelpers.IconButton("##icon", Vector2N.One * 15, rule.Icon, tint))
                _shown = true;

            ImGui.SameLine();
            ImGui.Text(rule.Icon.ToString());

            if (_shown && ImGuiHelpers.IconPickerWindow(
                    rule.SearchTerm?.Value ?? "rule", ref rule.Icon, tint, ref _filter))
            {
                _shown = false;
            }
        }
    }
}
