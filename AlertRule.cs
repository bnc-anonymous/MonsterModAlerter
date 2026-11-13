using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using ExileCore;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using Color = SharpDX.Color;
using Vector2N = System.Numerics.Vector2;

namespace MonsterModAlerter;

/// <summary>
/// One kind of monster or modifier to mark, and how to present it: its icon on the map and in the world, a box and a
/// label in the world, a modifier count and a sound. Settings that only matter when a switch is on are shown only then
/// (ConditionalDisplay).
/// </summary>
public class AlertRule : ISettings
{
    // Made here rather than in an initializer, so it is attached again after JSON deserialization.
    public AlertRule() => IconPicker = new IconPickerNode(this);

    /// <summary>The rule a new install starts with, and the one a reset leaves.</summary>
    public static AlertRule Example() => new()
    {
        SearchTerm = new TextNode("TormentHauntedMonsterThief"),
        Icon = MapIconsIndex.CorpseTypeUndead,
    };

    /// <summary>Drawn on the rule's header by AlertGroupsEditor, not among its settings.</summary>
    [IgnoreMenu]
    public ToggleNode Enabled { get; set; } = new(true);

    /// <summary>For SettingsParser, which draws the rule's settings and wants an ISettings; not saved.</summary>
    ToggleNode ISettings.Enable
    {
        get => Enabled;
        set => Enabled = value;
    }

    /// <summary>
    /// Matched as a case-insensitive substring of the monster's mod and buff names (Search mods and Search buffs,
    /// below). Comma-separated terms are ANDed: "mod1,mod2" matches only a monster carrying both, which flags
    /// combinations that are harmless alone; either/or is two rules. Blank leaves Metadata paths alone to decide, which
    /// marks a kind of monster whatever its modifiers.
    /// </summary>
    [Menu("Search term",
        "Matched against mod and buff names, ignoring case. Comma-separated\n" +
        "terms must all match. Blank: Metadata paths alone decide.")]
    public TextNode SearchTerm { get; set; } = new("");

    /// <summary>
    /// Whether the search term is tested against ObjectMagicProperties.Mods, the internal mod names such as
    /// TormentHauntedMonsterThief. Every entry is searched, not only the rare modifiers ShowModCount counts. Does not
    /// affect that count, which always reads the mods.
    /// </summary>
    [Menu("Search mods (ObjectMagicProperties.Mods)")]
    public ToggleNode SearchMods { get; set; } = new(true);

    /// <summary>
    /// Whether the search term is tested against the names in the Buffs component's BuffsList, such as
    /// torment_cutthroat. Some monsters carry a mod only as a buff. The debug overlay lists a monster's buffs beside
    /// its mods, which tells which of the two a name is.
    /// </summary>
    [Menu("Search buffs (Buffs.BuffsList)")]
    public ToggleNode SearchBuffs { get; set; } = new(true);

    /// <summary>
    /// Each term is a .NET regular expression instead of plain text, so "MonsterMod.*Touched" matches every Touched
    /// variant. Commas still separate terms, so a pattern cannot contain one. Case-insensitive and unanchored, as plain
    /// terms are. A pattern that does not compile disarms the rule rather than matching everything, and the rule's
    /// entry in the list says [BAD REGEX].
    /// </summary>
    [Menu("Use regex", "Each term is a .NET regular expression, unanchored.\nCommas still separate terms.")]
    public ToggleNode UseRegex { get; set; } = new(false);

    /// <summary>
    /// Comma-separated parts of the entity's metadata path, e.g. Metadata/Monsters/BeehiveBehemoth, each matched as a
    /// case-insensitive substring. They are ORed, since they name alternatives rather than conditions that must all
    /// hold. Blank matches every entity, with or without Exclude metadata paths: treating a blank exclusion as
    /// "everything excluded" would switch a rule off the moment the text was cleared.
    /// </summary>
    [Menu("Metadata paths",
        "Comma-separated parts of the metadata path; any one matches,\n" +
        "e.g. Metadata/Monsters/BeehiveBehemoth. Blank matches all.")]
    public TextNode MetadataPaths { get; set; } = new("");

    /// <summary>Matches every entity except those Metadata paths name, e.g. everything but a given family.</summary>
    [Menu("Exclude metadata paths")]
    [ConditionalDisplay(nameof(HasMetadataPaths))]
    public ToggleNode ExcludeMetadataPaths { get; set; } = new(false);

    /// <summary>
    /// Off: the rule sees only hostile monsters, and Monster rarity applies. On: it is tested against every loaded
    /// entity with a position - critters, chests, doors, shrines - with the hostile check and Monster rarity skipped,
    /// and its terms also search the metadata path, since most such entities carry neither mods nor buffs.
    /// "Metadata/Critters" then marks every critter.
    /// </summary>
    [Menu("Match any entity",
        "Test every loaded entity, not only hostile monsters, and search\n" +
        "its metadata path too. Monster rarity does not apply.")]
    public ToggleNode AnyEntity { get; set; } = new(false);

    /// <summary>
    /// Higher is drawn on top among this plugin's icons. With Draw multiple matching icons off, only the
    /// highest-priority rule matching a monster is shown; either way it owns the remembered position, the count and the
    /// sound.
    /// </summary>
    [Menu("Priority",
        "Higher draws on top. With Draw multiple matching icons off, only\n" +
        "the highest-priority rule matching a monster is shown.")]
    public RangeNode<int> Priority { get; set; } = new(0, -100, 100);

    /// <summary>
    /// The rarities of monster this rule matches; rare and unique for a new rule. Not applied, nor shown, with Match
    /// any entity on: a chest has no rarity.
    /// </summary>
    [JsonIgnore]
    [ConditionalDisplay(nameof(AnyEntity), false)]
    public CustomNode MonsterRarityGroup { get; set; } = Section.Of("Monster rarity");

    [Menu("Normal")]
    [ConditionalDisplay(nameof(AnyEntity), false)]
    public ToggleNode NormalMonsters { get; set; } = new(false);

    [Menu("Magic")]
    [ConditionalDisplay(nameof(AnyEntity), false)]
    public ToggleNode MagicMonsters { get; set; } = new(false);

    [Menu("Rare")]
    [ConditionalDisplay(nameof(AnyEntity), false)]
    public ToggleNode RareMonsters { get; set; } = new(true);

    [Menu("Unique")]
    [ConditionalDisplay(nameof(AnyEntity), false)]
    public ToggleNode UniqueMonsters { get; set; } = new(true);

    [JsonIgnore]
    public CustomNode IconGroup { get; set; } = Section.Of("Icon");

    /// <summary>
    /// Saved by name, so the settings file stays readable and survives the enum being reordered. A rule added from the
    /// menu starts with the large pink pentagon; the example rule sets its own.
    /// </summary>
    [JsonConverter(typeof(StringEnumConverter))]
    public MapIconsIndex Icon = MapIconsIndex.LootFilterLargePinkPentagon;

    /// <summary>The icon, drawn on the map and, with Draw icon under World, in the world.</summary>
    [Menu("Icon")]
    [JsonIgnore]
    public IconPickerNode IconPicker { get; set; }

    /// <summary>
    /// Multiplies each colour channel of the icon by the tint's, so it can only take colour away: a white icon takes
    /// the tint's colour, while the red flag tinted yellow stays red (seen in game, 2026-11-13). Changing a coloured
    /// icon's hue would need recoloured copies of the icon drawn from files, which was considered and dropped.
    /// </summary>
    [Menu("Tint", "Only really noticeable on lighter coloured icons.")]
    public ColorNode Tint { get; set; } = new(Color.White);

    /// <summary>
    /// On the large map, and on the corner map with Display's Enable drawing on the corner map on.
    /// </summary>
    [Menu("Show on map")]
    public ToggleNode ShowOnMap { get; set; } = new(true);

    [Menu("Map icon size (px)")]
    [ConditionalDisplay(nameof(ShowOnMap))]
    public RangeNode<int> MapIconSize { get; set; } = new(32, 4, 100);

    /// <summary>
    /// Keeps the map icon, and the count, at the spot the monster was last seen after it leaves range. Drawn as a live
    /// icon is, so it is a last known position rather than proof the monster is still there. Dropped when the monster
    /// is seen dead, when the player comes near and nothing is there (RememberedPositions.ForgetRadius), when it is
    /// seen again and no rule matches it, on area change, and by Refresh icons. Hidden while its rule or the rule's
    /// group is switched off, and gone with the rule when it is deleted. Shown only with Show on map on, since only map
    /// icons are remembered.
    /// </summary>
    [Menu("Remember position",
        "Keep the map icon at the last seen spot after the monster leaves\n" +
        "range, until it is seen dead, found gone, or the area changes.")]
    [ConditionalDisplay(nameof(ShowOnMap))]
    public ToggleNode RememberPosition { get; set; } = new(true);

    [JsonIgnore]
    public CustomNode WorldGroup { get; set; } = Section.Of("World");

    /// <summary>Whether anything is drawn over the monster in the world: the icon, box and label below.</summary>
    [Menu("Show in world")]
    public ToggleNode ShowInWorld { get; set; } = new(true);

    [Menu("Draw icon")]
    [ConditionalDisplay(nameof(ShowInWorld))]
    public ToggleNode DrawWorldIcon { get; set; } = new(false);

    [Menu("World icon size (px)")]
    [ConditionalDisplay(nameof(ShowsWorldIcon))]
    public RangeNode<int> WorldIconSize { get; set; } = new(64, 4, 256);

    /// <summary>
    /// A frame round the monster. Several matching rules' boxes are drawn in priority order, highest on top.
    /// </summary>
    [Menu("Draw box")]
    [ConditionalDisplay(nameof(ShowInWorld))]
    public ToggleNode DrawBox { get; set; } = new(false);

    [Menu("Box size (px)")]
    [ConditionalDisplay(nameof(ShowsBox))]
    public RangeNode<int> BoxSize { get; set; } = new(64, 8, 200);

    [Menu("Box thickness (px)")]
    [ConditionalDisplay(nameof(ShowsBox))]
    public RangeNode<int> BoxThickness { get; set; } = new(3, 1, 10);

    [Menu("Box colour")]
    [ConditionalDisplay(nameof(ShowsBox))]
    public ColorNode BoxColour { get; set; } = new(Color.White);

    /// <summary>
    /// Text under the world icon and box: Label, or the matched mod, buff or path when Label is blank. Several matching
    /// rules' labels stack downwards.
    /// </summary>
    [Menu("Draw label")]
    [ConditionalDisplay(nameof(ShowInWorld))]
    public ToggleNode DrawLabel { get; set; } = new(true);

    [Menu("Label", "Blank: the matched mod, buff or path.")]
    [ConditionalDisplay(nameof(ShowsLabel))]
    public TextNode Label { get; set; } = new("");

    [Menu("Label colour")]
    [ConditionalDisplay(nameof(ShowsLabel))]
    public ColorNode LabelColour { get; set; } = new(Colours.FromArgb(255, 255, 210, 80));

    [Menu("Label scale")]
    [ConditionalDisplay(nameof(ShowsLabel))]
    public RangeNode<float> LabelScale { get; set; } = new(1.0f, 0.5f, 3.0f);

    [JsonIgnore]
    public CustomNode ModCountGroup { get; set; } = Section.Of("Mod count");

    /// <summary>
    /// Draws a number on the monster's map icon: by default how many rare modifiers it has - the distinct MonsterMod
    /// entries of ObjectMagicProperties.Mods, since the game repeats some, so the number matches the monster's tooltip.
    /// Buffs are not counted: they come and go with auras and charges and would make it flicker. The Debug section's
    /// overlay lists the entries and which were counted. Drawn centred on the icon in Mod count colour with a black
    /// outline, then moved by Mod count X offset and Y offset.
    /// </summary>
    [Menu("Show the number of rare modifiers the monster has")]
    public ToggleNode ShowModCount { get; set; } = new(false);

    /// <summary>
    /// Counts the distinct mod and buff names the search term matches instead: with "MonsterMod.*Touched" as a regex, a
    /// monster with Fire Touched and Cold Touched reads 2. Buffs count only with Search buffs on. Ignored without a
    /// search term, which counts rare modifiers. Shown while the count is shown or gates the rule (Mod count minimum).
    /// </summary>
    [Menu("Show the number of search matches instead of rare modifiers")]
    [ConditionalDisplay(nameof(UsesModCount))]
    public ToggleNode CountSearchMatches { get; set; } = new(false);

    /// <summary>
    /// Below this count the rule does not match at all - no icon, number, sound or remembered position - whether or not
    /// the number is shown, so it doubles as a filter. Applies to whichever number the rule counts. 0 is off.
    /// </summary>
    [Menu("Mod count minimum", "Below this the rule does not match at all. 0 = off.")]
    public RangeNode<int> ModCountMinimum { get; set; } = new(0, 0, 50);

    [Menu("Mod count colour")]
    [ConditionalDisplay(nameof(ShowModCount))]
    public ColorNode ModCountColour { get; set; } = new(Color.White);

    /// <summary>
    /// Moves this rule's number right (X) and down (Y) by this many pixels from the icon's centre, on the map and in
    /// the world, to line it up with this rule's icon by eye or to put it beside the icon. Per rule, since where a
    /// number looks centred depends on the icon. The number is centred by its text box, which is the font's whole line
    /// height, and the HUD's font sits its digits low in that box (unifont at 16: digits at 4 to 14 of 16 pixels, read
    /// from the font file).
    /// </summary>
    [Menu("Mod count X offset (px)")]
    [ConditionalDisplay(nameof(ShowModCount))]
    public RangeNode<int> ModCountOffsetX { get; set; } = new(0, -25, 25);

    [Menu("Mod count Y offset (px)")]
    [ConditionalDisplay(nameof(ShowModCount))]
    public RangeNode<int> ModCountOffsetY { get; set; } = new(0, -25, 25);

    /// <summary>The same number over the monster in the world, beside its world icon, as well as on the map.</summary>
    [Menu("Draw mod count in world")]
    [ConditionalDisplay(nameof(ShowModCount))]
    public ToggleNode DrawModCountInWorld { get; set; } = new(false);

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

    /// <summary>
    /// The .wav from the HUD's Sounds folder played when this rule matches, or None, the default. The choices are
    /// filled in by AlertGroupsEditor from AlertSound.SoundNames, since a ListNode's choices are not saved.
    /// </summary>
    [Menu("Play sound")]
    public ListNode PlaySound { get; set; } = new() { Value = AlertSound.None };

    /// <summary>Whether this rule matches a monster of this rarity (Monster rarity).</summary>
    public bool AllowsRarity(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.White => NormalMonsters,
        MonsterRarity.Magic => MagicMonsters,
        MonsterRarity.Rare => RareMonsters,
        MonsterRarity.Unique => UniqueMonsters,
        _ => false,
    };

    /// <summary>Whether Play sound names a sound rather than None.</summary>
    [JsonIgnore]
    [IgnoreMenu]
    public bool HasSound => !string.IsNullOrWhiteSpace(PlaySound?.Value) && PlaySound.Value != AlertSound.None;

    // Conditions for ConditionalDisplay above. Not settings: neither saved nor drawn.

    [JsonIgnore]
    [IgnoreMenu]
    public bool HasMetadataPaths => MetadataPathList.Length > 0;

    [JsonIgnore]
    [IgnoreMenu]
    public bool ShowsWorldIcon => ShowInWorld && DrawWorldIcon;

    [JsonIgnore]
    [IgnoreMenu]
    public bool ShowsBox => ShowInWorld && DrawBox;

    [JsonIgnore]
    [IgnoreMenu]
    public bool ShowsLabel => ShowInWorld && DrawLabel;

    [JsonIgnore]
    [IgnoreMenu]
    public bool UsesModCount => ShowModCount || ModCountMinimum.Value > 0;

    private string _metadataPathSource;
    private string[] _metadataPathList = [];

    /// <summary>MetadataPaths split on commas, re-split only when the text changes.</summary>
    [JsonIgnore]
    public string[] MetadataPathList
    {
        get
        {
            var source = MetadataPaths?.Value ?? "";
            if (source != _metadataPathSource)
            {
                _metadataPathSource = source;
                _metadataPathList =
                    source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            return _metadataPathList;
        }
    }

    /// <summary>
    /// Whether an entity with this metadata path passes Metadata paths, honouring Exclude metadata paths.
    /// </summary>
    public bool MatchesMetadataPath(string path)
    {
        var parts = MetadataPathList;
        if (parts.Length == 0)
            return true;

        var hit = false;

        if (!string.IsNullOrEmpty(path))
        {
            foreach (var part in parts)
            {
                if (path.Contains(part, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }
        }

        return ExcludeMetadataPaths ? !hit : hit;
    }

    /// <summary>Metadata paths as the rule's entry shows them, with "not " in front when they are excluded.</summary>
    public string DescribeMetadataPaths() =>
        !HasMetadataPaths ? "any monster" : (ExcludeMetadataPaths ? "not " : "") + MetadataPaths.Value;

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
    /// The rule's name, as its header and its delete button show it: its search term, or its metadata paths when it
    /// has none, with "[any]" in front for Match any entity.
    /// </summary>
    [JsonIgnore]
    [IgnoreMenu]
    public string DisplayName
    {
        get
        {
            // A rule with no term is valid when Metadata paths carry it, so name those instead of reporting the rule as
            // incomplete.
            var term = string.IsNullOrWhiteSpace(SearchTerm?.Value)
                ? (HasMetadataPaths ? DescribeMetadataPaths() : "<no term>")
                : SearchTerm.Value;

            // A rule not restricted to monsters behaves differently enough - no Monster rarity, and it searches the
            // metadata path - that its name says so without opening it.
            return AnyEntity?.Value == true ? $"[any] {term}" : term;
        }
    }

    /// <summary>
    /// The rule's header: its display name, "(disabled)" in front when off and "[BAD REGEX]" after when its pattern
    /// does not compile.
    /// </summary>
    [JsonIgnore]
    [IgnoreMenu]
    public string HeaderText
    {
        get
        {
            // A rule that cannot compile its pattern matches nothing, which is indistinguishable from "no such monster
            // here" unless it is said.
            var badRegex = PatternError != null ? "  [BAD REGEX]" : "";

            return $"{(Enabled?.Value == true ? "" : "(disabled) ")}{DisplayName}{badRegex}";
        }
    }

    public override string ToString() => HeaderText;

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
