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
/// One kind of monster or modifier to mark, and how to present it. Shown by the HUD as an entry in the Alert rules
/// list, so each rule has its own icon, sizes, count and sound.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class AlertRule
{
    // Made here rather than in an initializer, so it is attached again after JSON deserialization.
    public AlertRule() => IconPicker = new IconPickerNode(this);

    /// <summary>The rule a new install starts with, and the one a reset leaves.</summary>
    public static AlertRule Example() => new()
    {
        SearchTerm = new TextNode("TormentHauntedMonsterThief"),
        Icon = MapIconsIndex.CorpseTypeUndead,
    };

    [Menu("Enabled")]
    public ToggleNode Enabled { get; set; } = new(true);

    /// <summary>
    /// Matched as a case-insensitive substring of the monster's mod and buff names (Scan's Search mods and Search
    /// buffs). Comma-separated terms are ANDed: "mod1,mod2" matches only a monster carrying both, which flags
    /// combinations that are harmless alone; either/or is two rules. Blank leaves the path filter alone to decide,
    /// which marks a kind of monster whatever its modifiers.
    /// </summary>
    [Menu("Search term",
        "Matched against mod and buff names, ignoring case. Comma-separated\n" +
        "terms must all match. Blank: the path filter alone decides.")]
    public TextNode SearchTerm { get; set; } = new("");

    /// <summary>
    /// Each term is a .NET regular expression instead of plain text, so "MonsterMod.*Touched" matches every Touched
    /// variant. Commas still separate terms, so a pattern cannot contain one. Case-insensitive and unanchored, as plain
    /// terms are. A pattern that does not compile disarms the rule rather than matching everything, and the rule's
    /// entry in the list says [BAD REGEX].
    /// </summary>
    [Menu("Use regex", "Each term is a .NET regular expression, unanchored.\nCommas still separate terms.")]
    public ToggleNode UseRegex { get; set; } = new(false);

    /// <summary>
    /// Off: the rule sees only hostile monsters, and its rarity filter applies. On: it is tested against every loaded
    /// entity with a position - critters, chests, doors, shrines - with the hostile check and the rarity filter
    /// skipped, and its terms also search the metadata path, since most such entities carry neither mods nor buffs.
    /// "Metadata/Critters" then marks every critter.
    /// </summary>
    [Menu("Match any entity",
        "Test every loaded entity, not only hostile monsters, and search\n" +
        "its metadata path too. The rarity filter does not apply.")]
    public ToggleNode AnyEntity { get; set; } = new(false);

    [Menu("Path filter")]
    public PathFilterSettings PathFilter { get; set; } = new();

    /// <summary>
    /// The rarities of monster this rule matches. Not applied with Match any entity on: a chest has no rarity.
    /// </summary>
    [Menu("Monster rarity filter")]
    public RarityFilterSettings Rarity { get; set; } = RarityFilterSettings.RareAndUnique();

    /// <summary>
    /// Higher is drawn on top among this plugin's icons. With Draw multiple matching icons off, only the
    /// highest-priority rule matching a monster is shown; either way it owns the remembered position, the count and the
    /// sound.
    /// </summary>
    [Menu("Priority",
        "Higher draws on top. With Draw multiple matching icons off, only the\n" +
        "highest-priority rule matching a monster is shown.")]
    public RangeNode<int> Priority { get; set; } = new(0, -100, 100);

    [JsonIgnore]
    public CustomNode IconGroup { get; set; } = Section.Of("Icon");

    /// <summary>Saved by name, so the settings file stays readable and survives the enum being reordered.</summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public MapIconsIndex Icon = MapIconsIndex.CorpseTypeUndead;

    [Menu("Icon")]
    [JsonIgnore]
    public IconPickerNode IconPicker { get; set; }

    [Menu("Tint")]
    public ColorNode Tint { get; set; } = new(Color.White);

    /// <summary>
    /// On the large map, and on the corner map with Display's Enable drawing on the corner map on.
    /// </summary>
    [Menu("Show on map")]
    public ToggleNode ShowOnMap { get; set; } = new(true);

    [Menu("Map icon size (px)")]
    public RangeNode<int> MapIconSize { get; set; } = new(32, 4, 100);

    /// <summary>Over the monster, with the World section's drawing on.</summary>
    [Menu("Show in world")]
    public ToggleNode ShowInWorld { get; set; } = new(true);

    [Menu("World icon size (px)")]
    public RangeNode<int> WorldIconSize { get; set; } = new(64, 4, 256);

    /// <summary>
    /// Drawn under the world icon when the World section's Draw label is on. Blank shows the matched name.
    /// </summary>
    [Menu("Label", "Drawn under the world icon with World > Draw label on.\nBlank: the matched name.")]
    public TextNode Label { get; set; } = new("");

    /// <summary>
    /// Keeps the map icon, and the count, at the spot the monster was last seen after it leaves range. Drawn as a live
    /// icon is, so it is a last known position rather than proof the monster is still there. Dropped when the monster
    /// is seen dead, when the player comes near and nothing is there (RememberedPositions.ForgetRadius), and on area
    /// change.
    /// </summary>
    [Menu("Remember position",
        "Keep the map icon at the last seen spot after the monster leaves\n" +
        "range, until it is seen dead, found gone, or the area changes.")]
    public ToggleNode RememberPosition { get; set; } = new(false);

    [JsonIgnore]
    public CustomNode ModCountGroup { get; set; } = Section.Of("Mod count");

    /// <summary>
    /// Draws a number on the monster's map icon: by default how many rare modifiers it has - the distinct MonsterMod
    /// entries of ObjectMagicProperties.Mods, since the game repeats some, so the number matches the monster's tooltip.
    /// Buffs are not counted: they come and go with auras and charges and would make it flicker. The Debug section's
    /// overlay lists the entries and which were counted. Drawn as the Mod count section says.
    /// </summary>
    [Menu("Show mod count", "The monster's rare modifiers, each counted once.")]
    public ToggleNode ShowModCount { get; set; } = new(false);

    /// <summary>
    /// Counts the distinct mod and buff names the search term matches instead: with "MonsterMod.*Touched" as a regex, a
    /// monster with Fire Touched and Cold Touched reads 2. Buffs count only with Search buffs on. Ignored without a
    /// search term, which counts rare modifiers.
    /// </summary>
    [Menu("Count search matches", "Count the mods and buffs the search term matches instead.")]
    public ToggleNode CountSearchMatches { get; set; } = new(false);

    /// <summary>
    /// Below this count the rule does not match at all - no icon, number, sound or remembered position - whether or not
    /// the number is shown, so it doubles as a filter. Applies to whichever number the rule counts. 0 is off.
    /// </summary>
    [Menu("Mod count minimum", "Below this the rule does not match at all. 0 = off.")]
    public RangeNode<int> ModCountMinimum { get; set; } = new(0, 0, 50);

    [Menu("Mod count colour")]
    public ColorNode ModCountColour { get; set; } = new(Color.White);

    [JsonIgnore]
    public CustomNode SeenTallyGroup { get; set; } = Section.Of("Seen tally");

    /// <summary>
    /// Adds what this rule matches to the seen tally. Off by default, so a rule written to warn about something does
    /// not also become a list of it. An entity several rules match is counted once.
    /// </summary>
    [Menu("Add to seen tally")]
    public ToggleNode AddToSeenTally { get; set; } = new(false);

    [JsonIgnore]
    public CustomNode SoundGroup { get; set; } = Section.Of("Sound");

    [Menu("Play sound")]
    public ToggleNode PlaySound { get; set; } = new(true);

    /// <summary>
    /// A file in the HUD's Sounds folder, named without its extension; "attention" is the spoken warning.
    /// </summary>
    [Menu("Sound file", "A file in the HUD's Sounds folder, without its extension.")]
    public TextNode SoundFile { get; set; } = new("attention");

    private string _termSource;
    private string[] _terms = [];

    /// <summary>
    /// SearchTerm split on commas. All of these must be present on a monster for the rule to match. Re-split only when
    /// the text changes, since this is read on every scan.
    /// </summary>
    [JsonIgnore]
    public string[] Terms
    {
        get
        {
            var source = SearchTerm?.Value ?? string.Empty;
            if (!string.Equals(source, _termSource, StringComparison.Ordinal))
            {
                _termSource = source;
                _terms = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                // Rebuilt lazily against the new terms.
                _patterns = null;
                _patternSource = null;
            }

            return _terms;
        }
    }

    private string _patternSource;
    private bool _patternsAreRegex;
    private Regex[] _patterns;
    private string _patternError;

    /// <summary>
    /// Terms compiled as regexes, or empty when this rule matches by plain substring. Compiled once per edit rather
    /// than per scan, since a scan tests every term against every mod on every monster.
    /// </summary>
    [JsonIgnore]
    public Regex[] Patterns
    {
        get
        {
            // May invalidate the cache below.
            var terms = Terms;
            var useRegex = UseRegex?.Value == true;

            if (_patterns != null && _patternsAreRegex == useRegex &&
                string.Equals(_patternSource, _termSource, StringComparison.Ordinal))
            {
                return _patterns;
            }

            _patternSource = _termSource;
            _patternsAreRegex = useRegex;
            _patternError = null;

            if (!useRegex)
            {
                _patterns = [];
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
                    // A bad pattern must not throw on the scan, and must not quietly match everything either - the rule
                    // is disarmed and says so in its entry.
                    _patternError = e.Message;
                    built.Clear();
                    break;
                }
            }

            _patterns = built.ToArray();
            return _patterns;
        }
    }

    /// <summary>Why the pattern would not compile, or null when it is fine.</summary>
    [JsonIgnore]
    public string PatternError
    {
        get
        {
            _ = Patterns;
            return _patternError;
        }
    }

    /// <summary>
    /// The rule's entry in the list. The trailing ### keeps ImGui's id stable while the text changes.
    /// </summary>
    public override string ToString()
    {
        // A rule with no term is valid when the path filter carries it, so name that instead of reporting the rule as
        // incomplete.
        var term = string.IsNullOrWhiteSpace(SearchTerm?.Value)
            ? (PathFilter?.IsBlank == false ? PathFilter.Describe() : "<no term>")
            : SearchTerm.Value;

        // A rule that cannot compile its pattern matches nothing, which is indistinguishable from "no such monster
        // here" unless it is said.
        if (PatternError != null)
            term = $"{term}  [BAD REGEX]";

        // A rule not restricted to monsters behaves differently enough - no rarity filter, and it searches the metadata
        // path - that the list says so without opening it.
        if (AnyEntity?.Value == true)
            term = $"[any] {term}";

        return $"{(Enabled?.Value == true ? "" : "(disabled) ")}{term}###";
    }

    /// <summary>
    /// The icon button and picker. Uses ExileCore's own IconButton and IconPickerWindow, the ones MinimapIcons uses, so
    /// the grid, filtering and sizing match the rest of the HUD.
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
