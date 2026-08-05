using ExileCore;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using Newtonsoft.Json.Converters;
using System;
using System.Collections.Generic;
using System.Reflection;
using Color = SharpDX.Color;
using Vector2N = System.Numerics.Vector2;
using Vector4 = System.Numerics.Vector4;

namespace MonsterModAlerter;

public class MonsterModAlerterSettings : ISettings
{
    public MonsterModAlerterSettings()
    {
        // Wired here (not in an initializer) so it survives JSON deserialization.
        ResetEverything = new CustomNode { DrawDelegate = DrawResetControl };
    }

    public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Alert Rules", "One rule per mod/buff you want to watch for. Each has its own icon, size and sound.")]
    public ContentNode<AlertRule> Rules { get; set; } = new ContentNode<AlertRule>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new AlertRule(),
        ItemFilter = (rule, search) => rule.SearchTerm.Value.Contains(search, StringComparison.OrdinalIgnoreCase),
        // Deliberately empty: ExileCore deserializes settings without ObjectCreationHandling.Replace,
        // so a pre-populated list would be appended to on every load. Seeded by the plugin instead.
        Content = new List<AlertRule>(),
    };

    [Menu("Refresh Now",
        "Forces an immediate re-scan and clears the 'already alerted' list, so matching monsters " +
        "are re-detected and can announce again. Sizes, icons and colours apply live without this.")]
    [JsonIgnore]
    public ButtonNode ForceRefresh { get; set; } = new ButtonNode();

    [Menu("Match Mods", "Check ObjectMagicProperties.Mods (internal names, e.g. TormentHauntedMonsterThief).")]
    public ToggleNode CheckMods { get; set; } = new ToggleNode(true);

    [Menu("Match Buffs", "Check the Buffs component (e.g. torment_cutthroat). Some monsters expose the mod only as a buff.")]
    public ToggleNode CheckBuffs { get; set; } = new ToggleNode(true);

    [Menu("Global Rarity Filter",
        "Applies to every rule. A monster must pass BOTH this and the rule's own rarity filter, " +
        "so each rule can only narrow this further, never widen it.")]
    public RarityFilterSettings Rarity { get; set; } = RarityFilterSettings.RareAndUnique();

    [Menu("Scan Interval (ms)", "How often the monster list is re-scanned. Drawing stays smooth regardless.")]
    public RangeNode<int> ScanIntervalMs { get; set; } = new RangeNode<int>(250, 50, 2000);

    [Menu("Max Distance", "Ignore monsters further than this from the player. 0 = no limit.")]
    public RangeNode<int> MaxDistance { get; set; } = new RangeNode<int>(0, 0, 5000);

    [Menu("Render Order",
        "Controls draw order against OTHER ExileAPI plugins. Higher = drawn later = on top. " +
        "Read once when the HUD loads the plugin, so changing it needs a HUD restart.")]
    public RangeNode<int> RenderOrder { get; set; } = new RangeNode<int>(5000, -1000, 100000);

    [Menu("Mod Count", "Draws how many modifiers a monster has on the minimap, as a bare number.")]
    public ModCountSettings ModCount { get; set; } = new ModCountSettings();

    [Menu("World Overlay")]
    public WorldDrawSettings World { get; set; } = new WorldDrawSettings();

    [Menu("Sound Alert")]
    public SoundSettings Sound { get; set; } = new SoundSettings();

    [Menu("Draw Only On Large Map", "Restrict minimap icons to the open map (Tab) only.")]
    public ToggleNode DrawOnlyOnLargeMap { get; set; } = new ToggleNode(false);

    [Menu("Ignore Fullscreen Panels", "Keep drawing while a fullscreen panel (Atlas, etc.) is open.")]
    public ToggleNode IgnoreFullscreenPanels { get; set; } = new ToggleNode(false);

    [Menu("Ignore Large Panels", "Keep drawing while a large panel is open.")]
    public ToggleNode IgnoreLargePanels { get; set; } = new ToggleNode(false);

    [Menu("Limit Draw Region",
        "When the inventory or stash is open, hide only the icons that would land on top of that " +
        "panel, instead of hiding everything. Turn off to always draw over open panels.")]
    public ToggleNode LimitDrawRegion { get; set; } = new ToggleNode(true);

    [Menu("Debug Overlay",
        "Draws diagnostics top-left: how many monsters matched, whether the minimap is considered " +
        "visible, and each match's effective sizes. Use this to tell 'icon is the wrong size' apart " +
        "from 'icon is not being drawn at all'.")]
    public ToggleNode DebugOverlay { get; set; } = new ToggleNode(false);

    [Menu("Reset Everything")]
    [JsonIgnore]
    public CustomNode ResetEverything { get; set; }

    /// <summary>
    /// Set by the confirmation dialog and consumed by the plugin on the next Tick. Deferred rather
    /// than reset inline, because clearing the rule list while ImGui is mid-render over it crashes.
    /// </summary>
    [JsonIgnore]
    public bool ResetRequested { get; set; }

    private void DrawResetControl()
    {
        if (ImGui.Button("Reset All Settings & Delete All Rules"))
            ImGui.OpenPopup("##mma_reset_confirm");

        // ImGui.NET has no (name, flags) overload; the plain one already auto-resizes.
        if (!ImGui.BeginPopupModal("##mma_reset_confirm"))
            return;

        var ruleCount = Rules?.Content?.Count ?? 0;

        ImGui.TextColored(new Vector4(1f, 0.35f, 0.35f, 1f), "Delete all custom alerts?");
        ImGui.Separator();
        ImGui.Text($"{ruleCount} alert rule{(ruleCount == 1 ? "" : "s")} will be permanently deleted,");
        ImGui.Text("including their search terms, icons, sizes and sounds.");
        ImGui.Text("Every other setting returns to its default.");
        ImGui.Spacing();
        ImGui.TextDisabled("This cannot be undone. A fresh example rule is created afterwards.");
        ImGui.Separator();

        if (ImGui.Button("Delete everything", new Vector2N(170, 0)))
        {
            ResetRequested = true;
            ImGui.CloseCurrentPopup();
        }

        ImGui.SameLine();

        if (ImGui.Button("Cancel", new Vector2N(110, 0)))
            ImGui.CloseCurrentPopup();

        ImGui.EndPopup();
    }

    /// <summary>
    /// Copies every value from a freshly constructed instance, so new settings are covered
    /// automatically rather than needing to be listed here.
    /// </summary>
    public void ResetToDefaults()
    {
        CopyValues(new MonsterModAlerterSettings(), this);

        Rules.Content.Clear();
        Rules.Content.Add(AlertRule.Default());

        // CopyValues skips ContentNode, so nested rule lists have to be cleared by hand.
        ModCount.Rules.Content.Clear();
        ModCount.Rules.Content.Add(ModCountRule.Default());
    }

    private static void CopyValues(object from, object to)
    {
        if (from == null || to == null)
            return;

        foreach (var prop in to.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (!prop.CanRead)
                continue;

            var target = prop.GetValue(to);
            var source = prop.GetValue(from);

            if (target == null || source == null || target.GetType() != source.GetType())
                continue;

            var targetType = target.GetType();

            // Rules are reset separately; buttons and custom nodes hold no persisted value.
            // (IContentNodeBase is internal to ExileCore, so match the generic type instead.)
            if (target is ButtonNode or CustomNode ||
                (targetType.IsGenericType && targetType.GetGenericTypeDefinition() == typeof(ContentNode<>)))
            {
                continue;
            }

            // Any settings node (Toggle/Range/Text/Color) exposes a writable Value.
            var valueProp = targetType.GetProperty("Value");
            if (valueProp is { CanRead: true, CanWrite: true })
            {
                valueProp.SetValue(target, valueProp.GetValue(source));
                continue;
            }

            // Nested groups of our own (RarityFilterSettings, WorldDrawSettings, SoundSettings).
            if (targetType.Namespace == typeof(MonsterModAlerterSettings).Namespace)
                CopyValues(source, target);
        }
    }
}

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
        IconPicker = new PickerNode(this);
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
    public PickerNode IconPicker { get; set; }

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
            }

            return _terms;
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

        return $"{(Enabled?.Value == true ? "" : "(off) ")}{term}###";
    }

    /// <summary>
    /// Uses ExileCore's own IconButton/IconPickerWindow helpers — the same ones MinimapIcons uses —
    /// so the grid layout, filtering and sizing match the rest of the HUD.
    /// </summary>
    [Submenu(RenderMethod = nameof(Render))]
    public class PickerNode(AlertRule rule)
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

/// <summary>Icon atlas shared with the rest of the HUD; used for in-world and minimap drawing.</summary>
public static class IconAtlas
{
    public const string TextureName = "Icons.png";
}

[Submenu(CollapsedByDefault = false)]
public class RarityFilterSettings
{
    [Menu("Normal")] public ToggleNode Normal { get; set; } = new ToggleNode(true);
    [Menu("Magic")] public ToggleNode Magic { get; set; } = new ToggleNode(true);
    [Menu("Rare")] public ToggleNode Rare { get; set; } = new ToggleNode(true);
    [Menu("Unique")] public ToggleNode Unique { get; set; } = new ToggleNode(true);

    /// <summary>Global default: skip the white/magic noise.</summary>
    public static RarityFilterSettings RareAndUnique() => new RarityFilterSettings
    {
        Normal = new ToggleNode(false),
        Magic = new ToggleNode(false),
    };

    public bool Allows(MonsterRarity rarity) => rarity switch
    {
        MonsterRarity.White => Normal,
        MonsterRarity.Magic => Magic,
        MonsterRarity.Rare => Rare,
        MonsterRarity.Unique => Unique,
        _ => false,
    };
}

[Submenu(CollapsedByDefault = false)]
public class WorldDrawSettings
{
    [Menu("Draw In World")] public ToggleNode Enable { get; set; } = new ToggleNode(true);
    [Menu("Draw Icon In World", "Draw the rule's icon above the monster as well as on the minimap. " +
                                "Size is set per rule, under that rule's World Icon Size.")]
    public ToggleNode DrawIcon { get; set; } = new ToggleNode(true);
    [Menu("Draw Box")] public ToggleNode DrawBox { get; set; } = new ToggleNode(false);
    [Menu("Box Size")] public RangeNode<int> BoxSize { get; set; } = new RangeNode<int>(64, 8, 200);
    [Menu("Frame Thickness")] public RangeNode<int> Thickness { get; set; } = new RangeNode<int>(3, 1, 10);

    [Menu("Box Color", "Colour of the world box. Turn on 'Use Rule Tint For Box' to colour it per rule instead.")]
    public ColorNode BoxColor { get; set; } = new ColorNode(Color.Red);

    [Menu("Use Rule Tint For Box",
        "On (default): each rule's own Tint colours its box, so different rules get different boxes. " +
        "Turn off to use the single Box Color above for every rule.")]
    public ToggleNode UseRuleTintForBox { get; set; } = new ToggleNode(true);
    [Menu("Draw Label")] public ToggleNode DrawLabel { get; set; } = new ToggleNode(false);
    [Menu("Label Color")] public ColorNode LabelColor { get; set; } = new ColorNode(Color.Yellow);
    [Menu("Label Scale")] public RangeNode<float> LabelScale { get; set; } = new RangeNode<float>(1.0f, 0.5f, 3.0f);
}

[Submenu(CollapsedByDefault = false)]
public class SoundSettings
{
    [Menu("Play Sound On Match")] public ToggleNode Enable { get; set; } = new ToggleNode(true);

    [Menu("Cooldown (ms)", "Minimum gap between alert sounds, including successive loop repeats.")]
    public RangeNode<int> CooldownMs { get; set; } = new RangeNode<int>(800, 100, 30000);

    [Menu("Loop Sound While Monster Alive",
        "Off (default): each monster is announced once. On: keeps re-playing on the cooldown for as " +
        "long as a matching monster is alive.")]
    public ToggleNode LoopWhileAlive { get; set; } = new ToggleNode(false);
}

/// <summary>
/// Matches a monster by its metadata path, e.g.
/// Metadata/Monsters/BeehiveBehemoth/BeehiveBehemothBossStandaloneAllFlame.
///
/// Blank always means "no opinion" — it matches every monster whether or not <see cref="Exclude"/>
/// is ticked. Treating a blank exclusion as "everything is excluded" would silently switch a rule
/// off the moment the text was cleared, which is the opposite of what clearing a filter suggests.
/// </summary>
[Submenu(CollapsedByDefault = false)]
public class MetadataFilterSettings
{
    [Menu("Path Contains",
        "Case-insensitive substring of the monster's metadata path, e.g. \"BeehiveBehemoth\" or the " +
        "full Metadata/Monsters/BeehiveBehemoth/BeehiveBehemothBossStandaloneAllFlame. " +
        "Leave blank for any monster. Comma-separated entries are ORed, since these name " +
        "alternatives rather than conditions that must all hold.")]
    public TextNode Filter { get; set; } = new TextNode("");

    [Menu("Invert",
        "Match every monster EXCEPT those above. Use this to flag everything that is not a given " +
        "family, e.g. to clear the trash before starting on the ones you saved.")]
    public ToggleNode Exclude { get; set; } = new ToggleNode(false);

    public static MetadataFilterSettings Excluding(string path) => new MetadataFilterSettings
    {
        Filter = new TextNode(path),
        Exclude = new ToggleNode(true),
    };

    /// <summary>Cached split of <see cref="Filter"/>, rebuilt only when the text changes.</summary>
    [JsonIgnore] private string _cachedSource;
    [JsonIgnore] private string[] _cachedTerms = [];

    [JsonIgnore]
    public string[] Terms
    {
        get
        {
            var source = Filter?.Value ?? "";
            if (source != _cachedSource)
            {
                _cachedSource = source;
                _cachedTerms = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            return _cachedTerms;
        }
    }

    [JsonIgnore] public bool IsBlank => Terms.Length == 0;

    /// <summary>True when this monster passes the filter, honouring <see cref="Exclude"/>.</summary>
    public bool Matches(string path)
    {
        var terms = Terms;
        if (terms.Length == 0)
            return true;

        var hit = false;

        if (!string.IsNullOrEmpty(path))
        {
            foreach (var term in terms)
            {
                if (path.Contains(term, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }
        }

        return Exclude ? !hit : hit;
    }

    public string Describe() =>
        IsBlank ? "any monster" : (Exclude ? "not " : "") + Filter.Value;
}

/// <summary>
/// One "count the mods on this kind of monster" rule.
///
/// A blank metadata filter means "any monster", so the rarity boxes alone decide — that is how a
/// whole rarity gets covered without naming anything.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class ModCountRule
{
    public static ModCountRule Default() => new ModCountRule
    {
        Rarity = RarityFilterSettings.RareAndUnique(),
    };

    [Menu("Enabled")]
    public ToggleNode Enabled { get; set; } = new ToggleNode(true);

    [Menu("Monster Filter")]
    public MetadataFilterSettings Metadata { get; set; } = new MetadataFilterSettings();

    [Menu("Rarity Filter", "Which rarities this rule counts. Independent of the global alert filter.")]
    public RarityFilterSettings Rarity { get; set; } = RarityFilterSettings.RareAndUnique();

    [Menu("Minimum Count", "Draw nothing below this many modifiers. 1 hides unmodified monsters.")]
    public RangeNode<int> MinCount { get; set; } = new RangeNode<int>(1, 0, 50);

    [Menu("Colour")]
    public ColorNode Tint { get; set; } = new ColorNode(Color.White);

    [Menu("Priority", "When two rules match one monster, the higher number wins.")]
    public RangeNode<int> Priority { get; set; } = new RangeNode<int>(0, -100, 100);

    /// <summary>True when this rule counts the given monster.</summary>
    public bool Matches(string path, MonsterRarity rarity) =>
        Enabled && Rarity.Allows(rarity) && Metadata.Matches(path);

    public override string ToString() =>
        $"{(Enabled ? "" : "(off) ")}{Metadata.Describe()}###{GetHashCode()}";
}

/// <summary>
/// Shows a monster's modifier count on the minimap as a bare number, e.g. "12". Separate from the
/// alert rules: those need a search term and answer "is this specific mod here", while this answers
/// "how loaded is this monster" without caring which mods they are.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class ModCountSettings
{
    [Menu("Show Mod Count On Minimap")]
    public ToggleNode Enable { get; set; } = new ToggleNode(false);

    [Menu("Count Rules",
        "Which monsters get a number. Leave a rule's metadata filter blank to cover a whole rarity.")]
    public ContentNode<ModCountRule> Rules { get; set; } = new ContentNode<ModCountRule>
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => ModCountRule.Default(),
        ItemFilter = (rule, search) =>
            (rule.Metadata.Filter.Value ?? "").Contains(search, StringComparison.OrdinalIgnoreCase),
        // Empty for the same reason as the alert rules: ExileCore deserializes without
        // ObjectCreationHandling.Replace, so a seeded list would grow on every load.
        Content = new List<ModCountRule>(),
    };

    [Menu("Text Size")]
    public RangeNode<float> TextScale { get; set; } = new RangeNode<float>(1.0f, 0.5f, 3.0f);

    [Menu("Background", "Drawn behind the number so it stays readable over map terrain.")]
    public ToggleNode DrawBackground { get; set; } = new ToggleNode(true);

    [Menu("Background Colour")]
    public ColorNode BackgroundColor { get; set; } = new ColorNode(new Color(0, 0, 0, 200));

    [Menu("Also Draw In World", "Show the same number over the monster itself, not just on the map.")]
    public ToggleNode DrawInWorld { get; set; } = new ToggleNode(false);
}
