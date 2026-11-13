using System.Collections.Generic;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Interfaces;
using ExileCore.Shared.Nodes;
using Newtonsoft.Json;

namespace MonsterModAlerter;

/// <summary>
/// A named set of alert rules, switched on and off together, e.g. "Gold farm" holding the rules for one strategy. Shown
/// by the HUD as an entry in the Alert groups list, with its own list of rules inside.
/// </summary>
public class AlertGroup : ISettings
{
    /// <summary>The group a new install starts with, and the one a reset leaves: the example rule.</summary>
    public static AlertGroup Example()
    {
        var group = new AlertGroup { Name = new TextNode("Example") };
        group.Rules.Content.Add(AlertRule.Example());
        return group;
    }

    /// <summary>
    /// Off switches every rule in the group off, whatever its own Enabled says. Drawn on the group's header by
    /// AlertGroupsEditor, not among its settings.
    /// </summary>
    [IgnoreMenu]
    public ToggleNode Enabled { get; set; } = new(true);

    /// <summary>For SettingsParser, which draws the group's settings and wants an ISettings; not saved.</summary>
    ToggleNode ISettings.Enable
    {
        get => Enabled;
        set => Enabled = value;
    }

    [Menu("Name")]
    public TextNode Name { get; set; } = new("New group");

    /// <summary>
    /// The group's rules, drawn by AlertGroupsEditor. Left empty here: ExileCore deserializes settings without
    /// ObjectCreationHandling.Replace, so a list filled here would be appended to on every load.
    /// </summary>
    [IgnoreMenu]
    public ContentNode<AlertRule> Rules { get; set; } = new() { Content = new List<AlertRule>() };

    /// <summary>The group's header: its name, "(disabled)" in front when off.</summary>
    [JsonIgnore]
    [IgnoreMenu]
    public string HeaderText
    {
        get
        {
            var name = string.IsNullOrWhiteSpace(Name?.Value) ? "<no name>" : Name.Value;
            return $"{(Enabled?.Value == true ? "" : "(disabled) ")}{name}";
        }
    }

    public override string ToString() => HeaderText;
}
