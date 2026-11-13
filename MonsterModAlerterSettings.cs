using System;
using System.Collections.Generic;
using System.Windows.Forms;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using ImGuiNET;
using Newtonsoft.Json;
using Color = SharpDX.Color;
using Vector2N = System.Numerics.Vector2;

namespace MonsterModAlerter;

/// <summary>
/// A heading inside a settings section, drawn as `--- Icon ------------`. Declared as a CustomNode property because
/// that is the only way to put something of a plugin's own into the attribute menu, and marked JsonIgnore because a
/// heading has nothing to save. The menu draws properties in declaration order, so a heading is the property above the
/// first setting it covers. The same pattern as AutoExpedition's and CraftUntilHighlighted's Section.
/// </summary>
internal static class Section
{
    public static CustomNode Of(string title) => new(() => ImGui.SeparatorText(title));
}

public class MonsterModAlerterSettings : ISettings
{
    public MonsterModAlerterSettings() => ResetAllUi = new CustomNode(DrawResetAll);

    /// <summary>
    /// The button at the top of the menu that puts every setting back to its default except Enable, and replaces the
    /// alert rules with the example rule (SettingsReset). Clickable only while Ctrl is held, so a stray click cannot
    /// undo the settings, as AutoExpedition's reset is; its tooltip says both, and shows while the button is disabled
    /// too. Made in the constructor because it needs the settings it resets.
    /// </summary>
    [JsonIgnore]
    public CustomNode ResetAllUi { get; set; }

    /// <summary>
    /// Raised by the reset button and answered by the next Tick. Not reset inline, because clearing the rule list while
    /// ImGui is drawing it crashed.
    /// </summary>
    [JsonIgnore]
    [IgnoreMenu]
    public bool ResetRequested { get; set; }

    public ToggleNode Enable { get; set; } = new(false);

    /// <summary>
    /// Which built-in changes this saved file has had applied. A saved file holds a value for every setting, so a
    /// changed default never reaches it on its own; a change that must reach saved files raises this and adds a step to
    /// MonsterModAlerter.ApplySettingsChanges. The same pattern as AutoExpedition's ConfigVersion.
    /// </summary>
    [IgnoreMenu]
    public int ConfigVersion { get; set; }

    /// <summary>
    /// What to mark, one rule per kind of monster or modifier. Left empty here and seeded with the example rule by the
    /// plugin: ExileCore deserializes settings without ObjectCreationHandling.Replace, so a list filled here would be
    /// appended to on every load.
    /// </summary>
    [Menu("Alert rules")]
    public ContentNode<AlertRule> Rules { get; set; } = new()
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new AlertRule(),
        ItemFilter = (rule, search) => rule.SearchTerm.Value.Contains(search, StringComparison.OrdinalIgnoreCase),
        Content = new List<AlertRule>(),
    };

    [Submenu(CollapsedByDefault = true)]
    public ScanSettings Scan { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public DisplaySettings Display { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public WorldSettings World { get; set; } = new();

    [Menu("Mod count", "Drawn for the rules with Show mod count on.")]
    [Submenu(CollapsedByDefault = true)]
    public ModCountSettings ModCount { get; set; } = new();

    [Menu("Seen tally", "Counts what the rules with Add to seen tally on have matched\nsince entering the area.")]
    [Submenu(CollapsedByDefault = true)]
    public SeenTallySettings SeenTally { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public SoundSettings Sound { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public DebugSettings Debug { get; set; } = new();

    private void DrawResetAll()
    {
        var armed = ImGui.GetIO().KeyCtrl;

        ImGui.BeginDisabled(!armed);
        var clicked = ImGui.Button("Reset all settings and rules###mmaResetAll");
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Resets every setting to its default, except Enable, and replaces\n" +
                             "the alert rules with the example rule. Hold Ctrl to click it.");
        }

        if (!armed)
        {
            ImGui.SameLine();
            ImGui.TextDisabled("hold Ctrl");
        }

        if (clicked && armed)
            ResetRequested = true;
    }
}

/// <summary>What a rule's search term is tested against. See RuleMatcher.</summary>
public class ScanSettings
{
    /// <summary>
    /// Whether search terms are tested against ObjectMagicProperties.Mods, the internal mod names such as
    /// TormentHauntedMonsterThief. Does not affect the rare modifier count, which always reads the mods.
    /// </summary>
    [Menu("Search mods")]
    public ToggleNode SearchMods { get; set; } = new(true);

    /// <summary>
    /// Whether search terms are tested against the Buffs component's names, such as torment_cutthroat. Some monsters
    /// carry a mod only as a buff.
    /// </summary>
    [Menu("Search buffs")]
    public ToggleNode SearchBuffs { get; set; } = new(true);
}

/// <summary>Where and when the plugin draws, against the game's panels and other plugins.</summary>
public class DisplaySettings
{
    /// <summary>
    /// Map icons and counts on the corner map, ExileCore's SmallMiniMap, as well as the large map, clipped to the
    /// corner map's square (MatchDrawing.MapAreaOf). Off by default. World drawing is unaffected.
    /// </summary>
    [Menu("Enable drawing on the corner map", "Map icons and counts on the corner map as well as the large map.")]
    public ToggleNode EnableDrawingOnCornerMap { get; set; } = new(false);

    /// <summary>
    /// Keeps the overlay off the game's panels. A side panel - the inventory, the stash, the character sheet - covers
    /// part of the screen, so drawing skips what would land on it (MatchDrawing.DrawRegionOf). A panel in ExileCore's
    /// FullscreenPanels or LargePanels hides the overlay outright while it is open; which panels those lists hold is
    /// obfuscated in ExileCore, and the inventory is in neither (seen in game, 2026-11-13). Off draws over everything.
    /// Replaces Radar's three: Limit draw region, Ignore fullscreen panels and Ignore large panels.
    /// </summary>
    [Menu("Prevent drawing over panels")]
    public ToggleNode PreventDrawingOverPanels { get; set; } = new(true);

    /// <summary>
    /// Whether every rule matching an entity draws its icon, stacked in priority order so the highest-priority icon
    /// sits on top, or only the highest-priority rule's. Either way the highest-priority match owns the remembered
    /// position and the sound. Also decides whether every matching rule's world label is drawn.
    /// </summary>
    [Menu("Draw multiple matching icons", "Off: only the highest-priority rule's icon.")]
    public ToggleNode DrawMultipleMatchingIcons { get; set; } = new(true);

    /// <summary>
    /// ExileCore's plugin Order (BaseSettingsPlugin.Order). Plugins render in ascending Order, so a higher value draws
    /// on top. A plugin that sets none, as MinimapIcons does not (its poe1 source, read 2026-11-13), draws at 0;
    /// DeepwaterEngagementSuite sets 10000, the highest fixed Order among the plugins installed with this HUD. The
    /// default of 15000 is above both. ExileCore sorts once, after every plugin is initialised, so a change takes
    /// effect at the next HUD start.
    /// </summary>
    [Menu("Render order",
        "Plugins draw in this order, lowest first, so a higher value draws\n" +
        "later, over the others. MinimapIcons, for example, draws at 0.\n" +
        "\n" +
        "Requires restarting ExileAPI to take effect.")]
    public RangeNode<int> RenderOrder { get; set; } = new(15000, -1000, 100000);
}

/// <summary>
/// Drawing over the monster in the world. Each rule turns it on with Show in world. See MatchDrawing.
/// </summary>
public class WorldSettings
{
    [Menu("Enable")]
    public ToggleNode Enable { get; set; } = new(true);

    /// <summary>The rule's icon, at the rule's World icon size.</summary>
    [Menu("Draw icon")]
    public ToggleNode DrawIcon { get; set; } = new(true);

    /// <summary>A frame round the monster, drawn once however many rules match it.</summary>
    [Menu("Draw box")]
    public ToggleNode DrawBox { get; set; } = new(false);

    [Menu("Box size (px)")]
    public RangeNode<int> BoxSize { get; set; } = new(64, 8, 200);

    [Menu("Box thickness (px)")]
    public RangeNode<int> BoxThickness { get; set; } = new(3, 1, 10);

    /// <summary>
    /// Colours the box with the tint of the highest-priority rule matching the monster, rather than Box colour.
    /// </summary>
    [Menu("Use rule tint for box")]
    public ToggleNode UseRuleTintForBox { get; set; } = new(true);

    [Menu("Box colour", "Used when Use rule tint for box is off.")]
    public ColorNode BoxColour { get; set; } = new(Colours.FromArgb(255, 235, 90, 90));

    /// <summary>
    /// The rule's Label, or the matched mod, buff or path when the label is blank, under the icon and box. Labels of
    /// several matching rules stack downwards.
    /// </summary>
    [Menu("Draw label", "A rule's Label, or the matched name when it has none, under the\nicon.")]
    public ToggleNode DrawLabel { get; set; } = new(false);

    [Menu("Label colour")]
    public ColorNode LabelColour { get; set; } = new(Colours.FromArgb(255, 255, 210, 80));

    [Menu("Label scale")]
    public RangeNode<float> LabelScale { get; set; } = new(1.0f, 0.5f, 3.0f);
}

/// <summary>
/// How the modifier count is drawn. Which monsters get one is decided per alert rule, by Show mod count, so a count is
/// always tied to a rule rather than being a second list of monsters to keep in step with the first.
/// </summary>
public class ModCountSettings
{
    private const string CentredOnIcon = "Centred on icon";
    private const string RightOfIcon = "Right of icon";
    private const string LeftOfIcon = "Left of icon";
    private const string AboveIcon = "Above icon";
    private const string BelowIcon = "Below icon";

    /// <summary>
    /// Where the number sits against the icon. Centred is the default: with the outline it leaves no doubt which icon a
    /// number belongs to when monsters are packed together. The values are saved as text, so renaming one resets it.
    /// </summary>
    [Menu("Position")]
    public ListNode Position { get; set; } = new()
    {
        Values = [CentredOnIcon, RightOfIcon, LeftOfIcon, AboveIcon, BelowIcon],
        Value = CentredOnIcon,
    };

    /// <summary>Pixels between the icon's edge and the number, for the positions beside the icon.</summary>
    [Menu("Gap from icon (px)")]
    public RangeNode<int> Gap { get; set; } = new(2, 0, 40);

    [Menu("Text scale")]
    public RangeNode<float> TextScale { get; set; } = new(1.0f, 0.5f, 3.0f);

    /// <summary>
    /// Eight offset copies in the outline colour under the number. Reads over any terrain without covering the map the
    /// way a filled box does.
    /// </summary>
    [Menu("Draw outline")]
    public ToggleNode DrawOutline { get; set; } = new(true);

    [Menu("Outline colour")]
    public ColorNode OutlineColour { get; set; } = new(Color.Black);

    /// <summary>
    /// A filled box behind the number. Off by default: the outline reads better and hides less of the map.
    /// </summary>
    [Menu("Draw background")]
    public ToggleNode DrawBackground { get; set; } = new(false);

    [Menu("Background colour")]
    public ColorNode BackgroundColour { get; set; } = new(new Color(0, 0, 0, 200));

    /// <summary>The same number over the monster in the world, beside its world icon.</summary>
    [Menu("Draw in world", "As well as on the map.")]
    public ToggleNode DrawInWorld { get; set; } = new(false);

    /// <summary>
    /// Offset from the monster's position that puts the number beside an icon of the given size, rather than under it.
    /// </summary>
    public Vector2N OffsetFor(float iconSize, Vector2N textSize)
    {
        var pad = iconSize / 2f + Gap.Value;

        return Position.Value switch
        {
            LeftOfIcon => new Vector2N(-pad - textSize.X / 2f, 0),
            AboveIcon => new Vector2N(0, -pad - textSize.Y / 2f),
            BelowIcon => new Vector2N(0, pad + textSize.Y / 2f),
            CentredOnIcon => Vector2N.Zero,
            _ => new Vector2N(pad + textSize.X / 2f, 0),
        };
    }
}

/// <summary>
/// The seen tally: a list at a fixed spot on the screen of how many of each kind of thing the rules with Add to seen
/// tally on have matched since entering the area, named by metadata path with the common prefix removed. See SeenTally.
/// </summary>
public class SeenTallySettings
{
    public SeenTallySettings() => ClearUi = new CustomNode(DrawClear);

    /// <summary>
    /// Whether the tally is drawn. The toggle key flips this setting, so the menu and the key never disagree.
    /// </summary>
    [Menu("Show")]
    public ToggleNode Show { get; set; } = new(false);

    /// <summary>Flips Show in game. Unbound by default.</summary>
    [Menu("Toggle key", "Shows and hides the tally in game.")]
    public HotkeyNodeV2 ToggleKey { get; set; } = new(Keys.None);

    /// <summary>
    /// Empties the tally now; it also empties on every area change. Raises ClearRequested for the next Tick.
    /// </summary>
    [JsonIgnore]
    public CustomNode ClearUi { get; set; }

    [JsonIgnore]
    [IgnoreMenu]
    public bool ClearRequested { get; set; }

    [Menu("X (px)")]
    public RangeNode<int> X { get; set; } = new(20, 0, 4000);

    [Menu("Y (px)")]
    public RangeNode<int> Y { get; set; } = new(300, 0, 3000);

    /// <summary>The longest list drawn. Rows are ordered by count, so the rows left off are the rarest.</summary>
    [Menu("Max rows", "The rarest rows are left off, with a line saying how many.")]
    public RangeNode<int> MaxRows { get; set; } = new(20, 1, 60);

    [Menu("Text scale")]
    public RangeNode<float> TextScale { get; set; } = new(1.0f, 0.5f, 3.0f);

    [Menu("Draw background")]
    public ToggleNode DrawBackground { get; set; } = new(true);

    [Menu("Background colour")]
    public ColorNode BackgroundColour { get; set; } = new(new Color(0, 0, 0, 180));

    [Menu("Name colour")]
    public ColorNode NameColour { get; set; } = new(Color.White);

    [Menu("Count colour")]
    public ColorNode CountColour { get; set; } = new(Colours.FromArgb(255, 255, 210, 80));

    private void DrawClear()
    {
        if (ImGui.Button("Clear tally###mmaClearTally"))
            ClearRequested = true;
    }
}

/// <summary>The alert sound. Each rule turns it on with Play sound and names its file. See AlertSound.</summary>
public class SoundSettings
{
    [Menu("Enable")]
    public ToggleNode Enable { get; set; } = new(true);

    /// <summary>The shortest time between two sounds, loop repeats included.</summary>
    [Menu("Cooldown (ms)")]
    public RangeNode<int> CooldownMs { get; set; } = new(800, 100, 30000);

    /// <summary>
    /// On: the sound plays again every cooldown while a matching monster is alive. Off: each monster is announced once.
    /// </summary>
    [Menu("Loop while alive",
        "Play again every cooldown while a matching monster lives,\n" +
        "rather than once per monster.")]
    public ToggleNode LoopWhileAlive { get; set; } = new(false);
}

public class DebugSettings
{
    public DebugSettings() => RefreshUi = new CustomNode(DrawRefresh);

    /// <summary>
    /// The debug overlay, top-left: match and rule counts, the remembered positions, the walk over every entity, map
    /// visibility, the draw region, and each match's icon sizes and map position, so an icon that is not drawn can be
    /// told apart from one drawn wrong. The first match whose rule shows a mod count also lists its mod entries. See
    /// DebugOverlay.
    /// </summary>
    [Menu("Show overlay", "Lists matches, map visibility and each match's draw position,\ntop-left.")]
    public ToggleNode ShowOverlay { get; set; } = new(false);

    /// <summary>
    /// How often EntityScan walks ExileCore's entity lists and runs the rules. Drawing reads positions every frame, so
    /// it stays smooth regardless; a longer interval only delays a newly loaded monster being matched.
    /// </summary>
    [Menu("Entity scan interval (ms)")]
    public RangeNode<int> EntityScanIntervalMs { get; set; } = new(250, 50, 2000);

    /// <summary>
    /// Scans now rather than at the next interval, dropping the remembered positions and letting every match play its
    /// sound again. Drawing settings apply live without it. It raises RefreshRequested, which the next Tick answers.
    /// </summary>
    [JsonIgnore]
    public CustomNode RefreshUi { get; set; }

    [JsonIgnore]
    [IgnoreMenu]
    public bool RefreshRequested { get; set; }

    private void DrawRefresh()
    {
        if (ImGui.Button("Refresh now###mmaRefresh"))
            RefreshRequested = true;

        if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Scans now, forgets remembered positions, and lets every match\n" +
                             "play its sound again.");
        }
    }
}

/// <summary>Which monster rarities a rule matches.</summary>
[Submenu(CollapsedByDefault = false)]
public class RarityFilterSettings
{
    [Menu("Normal")]
    public ToggleNode Normal { get; set; } = new(true);

    [Menu("Magic")]
    public ToggleNode Magic { get; set; } = new(true);

    [Menu("Rare")]
    public ToggleNode Rare { get; set; } = new(true);

    [Menu("Unique")]
    public ToggleNode Unique { get; set; } = new(true);

    /// <summary>The default for new rules: rare and unique monsters only.</summary>
    public static RarityFilterSettings RareAndUnique() => new()
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

/// <summary>
/// Matches an entity by its metadata path, e.g.
/// Metadata/Monsters/BeehiveBehemoth/BeehiveBehemothBossStandaloneAllFlame.
///
/// Blank always means no opinion: it matches every entity whether or not Exclude is on. Treating a blank exclusion as
/// "everything is excluded" would switch a rule off the moment the text was cleared.
/// </summary>
[Submenu(CollapsedByDefault = false)]
public class PathFilterSettings
{
    /// <summary>
    /// Comma-separated parts of the metadata path, each matched as a case-insensitive substring. They are ORed, since
    /// they name alternatives rather than conditions that must all hold.
    /// </summary>
    [Menu("Paths",
        "Comma-separated parts of the metadata path; any one matches,\n" +
        "e.g. Metadata/Monsters/BeehiveBehemoth. Blank matches all.")]
    public TextNode Paths { get; set; } = new("");

    /// <summary>
    /// Matches every entity except those the paths name, e.g. to mark everything that is not a given family.
    /// </summary>
    [Menu("Exclude", "Match everything except these paths.")]
    public ToggleNode Exclude { get; set; } = new(false);

    private string _splitSource;
    private string[] _splitPaths = [];

    /// <summary>Paths split on commas, re-split only when the text changes.</summary>
    [JsonIgnore]
    public string[] PathList
    {
        get
        {
            var source = Paths?.Value ?? "";
            if (source != _splitSource)
            {
                _splitSource = source;
                _splitPaths = source.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }

            return _splitPaths;
        }
    }

    [JsonIgnore]
    public bool IsBlank => PathList.Length == 0;

    /// <summary>Whether an entity with this metadata path passes the filter, honouring Exclude.</summary>
    public bool Matches(string path)
    {
        var paths = PathList;
        if (paths.Length == 0)
            return true;

        var hit = false;

        if (!string.IsNullOrEmpty(path))
        {
            foreach (var part in paths)
            {
                if (path.Contains(part, StringComparison.OrdinalIgnoreCase))
                {
                    hit = true;
                    break;
                }
            }
        }

        return Exclude ? !hit : hit;
    }

    public string Describe() => IsBlank ? "any monster" : (Exclude ? "not " : "") + Paths.Value;
}
