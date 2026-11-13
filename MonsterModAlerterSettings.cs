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
    public MonsterModAlerterSettings()
    {
        RefreshIconsUi = new CustomNode(DrawRefreshIcons);
        ResetAllUi = new CustomNode(DrawResetAll);
        GroupsUi = new CustomNode(() => AlertGroupsEditor.Draw(this));
    }

    /// <summary>
    /// The button at the very top of the menu that opens the Inspect world entities window (EntityInspector): what each
    /// nearby entity can be matched by, ready to copy into a rule.
    /// </summary>
    [JsonIgnore]
    public CustomNode InspectEntitiesUi { get; set; } = new(() =>
    {
        if (ImGui.Button("Inspect world entities###mmaInspectEntities"))
            EntityInspector.Open = true;
    });

    /// <summary>
    /// The button under Inspect world entities that forgets every remembered position and scans again now, so the
    /// icons show what the rules match at this moment. A monster still loaded is checked again at every scan anyway;
    /// this is for one out of range when its rule was edited not to match it, whose position alone is kept and cannot
    /// be checked. Matches already announced do not play their sound again. It raises RefreshIconsRequested, which the
    /// next Tick answers.
    /// </summary>
    [JsonIgnore]
    public CustomNode RefreshIconsUi { get; set; }

    [JsonIgnore]
    [IgnoreMenu]
    public bool RefreshIconsRequested { get; set; }

    /// <summary>
    /// The button at the top of the menu that puts every setting back to its default except Enable, and replaces the
    /// alert groups with the example groups (SettingsReset). Clickable only while Ctrl is held, so a stray click cannot
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
    /// What to mark: named groups of rules, each rule one kind of monster or modifier. Left empty here and seeded with
    /// the example groups by the plugin: ExileCore deserializes settings without ObjectCreationHandling.Replace, so a
    /// list filled here would be appended to on every load.
    /// </summary>
    [IgnoreMenu]
    public ContentNode<AlertGroup> Groups { get; set; } = new() { Content = new List<AlertGroup>() };

    /// <summary>
    /// The Alert rules section - the groups and their rules - drawn by AlertGroupsEditor rather than as ExileCore's
    /// ContentNode list, so groups and rules can be reordered by dragging. Made in the constructor because it needs the
    /// settings it draws.
    /// </summary>
    [JsonIgnore]
    public CustomNode GroupsUi { get; set; }

    /// <summary>Every rule of every enabled group, in list order. The rules' own Enabled is not checked here.</summary>
    public IEnumerable<AlertRule> RulesOfEnabledGroups()
    {
        foreach (var group in Groups?.Content ?? [])
        {
            if (group?.Enabled?.Value != true)
                continue;

            foreach (var rule in group.Rules?.Content ?? [])
                yield return rule;
        }
    }

    [Submenu(CollapsedByDefault = true)]
    public DisplaySettings Display { get; set; } = new();

    [Menu("Seen tally", "Counts what the rules with Add to seen tally on have matched\nsince entering the area.")]
    [Submenu(CollapsedByDefault = true)]
    public SeenTallySettings SeenTally { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public SoundSettings Sound { get; set; } = new();

    [Submenu(CollapsedByDefault = true)]
    public DebugSettings Debug { get; set; } = new();

    private void DrawRefreshIcons()
    {
        if (ImGui.Button("Refresh icons###mmaRefreshIcons"))
            RefreshIconsRequested = true;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Scans again now and forgets every remembered position.");
    }

    private void DrawResetAll()
    {
        var armed = ImGui.GetIO().KeyCtrl;

        ImGui.BeginDisabled(!armed);
        var clicked = ImGui.Button("Reset all settings and rules###mmaResetAll");
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
        {
            ImGui.SetTooltip("Resets every setting to its default, except Enable, and replaces\n" +
                             "the alert groups with the example groups. Hold Ctrl to click it.");
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

/// <summary>Where and when the plugin draws, against the game's panels and other plugins.</summary>
public class DisplaySettings
{
    /// <summary>
    /// Map icons and counts on the corner map, ExileCore's SmallMiniMap, as well as the large map, clipped to the
    /// corner map's square (MatchDrawing.MapAreaOf). Off by default. World drawing is unaffected.
    /// </summary>
    [Menu("Enable drawing on the corner map")]
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
    public ToggleNode DrawMultipleMatchingIcons { get; set; } = new(false);

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

/// <summary>The alert sound, for every rule. Each rule picks its own sound, or none. See AlertSound.</summary>
public class SoundSettings
{
    public SoundSettings() => PlayTestUi = new CustomNode(DrawPlayTest);

    [Menu("Disable all sounds")]
    public ToggleNode DisableAllSounds { get; set; } = new(false);

    /// <summary>
    /// The volume of every rule's sound: 100 plays it as recorded, above 100 louder, clipped where a sample would
    /// overflow, as Autoloot's Sound volume.
    /// </summary>
    [Menu("Volume (%)")]
    public RangeNode<int> Volume { get; set; } = new(100, 0, 300);

    /// <summary>
    /// A dropdown of the HUD's sounds and a button that plays the one picked at Volume, to hear each.
    /// </summary>
    [JsonIgnore]
    public CustomNode PlayTestUi { get; set; }

    /// <summary>
    /// After any sound plays, no other plays for this long, whichever rule matched: one timer for every match. With
    /// Loop while alive on it is also how often the sound repeats.
    /// </summary>
    [Menu("Minimum time between sounds (ms)", "Also how often a sound repeats with Loop while alive.")]
    public RangeNode<int> MinimumTimeBetweenSoundsMs { get; set; } = new(800, 0, 3000);

    /// <summary>
    /// On: the sound plays again after every Minimum time between sounds while a matching monster is alive. Off: each
    /// monster is announced once.
    /// </summary>
    [Menu("Loop while alive",
        "Play again after every minimum time while a matching monster lives,\n" +
        "rather than once per monster.")]
    public ToggleNode LoopWhileAlive { get; set; } = new(false);

    /// <summary>The sound the test button plays. Only for listening, so not saved.</summary>
    private string _testSound = AlertSound.DefaultTestSound;

    private void DrawPlayTest()
    {
        ImGui.SetNextItemWidth(ImGui.GetFontSize() * 12f);

        if (ImGui.BeginCombo("###mmaTestSound", _testSound))
        {
            foreach (var name in AlertSound.SoundNames)
            {
                if (name == AlertSound.None)
                    continue;

                if (ImGui.Selectable(name, name == _testSound))
                    _testSound = name;
            }

            ImGui.EndCombo();
        }

        ImGui.SameLine();

        if (ImGui.Button("Play test sound###mmaPlayTestSound"))
            AlertSound.PlayTest(_testSound, Volume.Value);
    }
}

public class DebugSettings
{
    public DebugSettings() => RefreshUi = new CustomNode(DrawRefresh);

    /// <summary>
    /// The debug overlay, top-left: match and rule counts, the remembered positions, the walk over every entity, map
    /// visibility, the draw region, and each match's icon sizes and map position, so an icon that is not drawn can be
    /// told apart from one drawn wrong. The first match whose rule shows a mod count also lists its mods and buffs. See
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
