using System;
using System.Collections.Generic;
using ExileCore.Shared.Attributes;
using ExileCore.Shared.Nodes;

namespace MonsterModAlerter;

/// <summary>
/// A named set of alert rules, switched on and off together, e.g. "Gold farm" holding the rules for one strategy. Shown
/// by the HUD as an entry in the Alert groups list, with its own list of rules inside.
/// </summary>
[Submenu(CollapsedByDefault = true)]
public class AlertGroup
{
    /// <summary>The group a new install starts with, and the one a reset leaves: the example rule.</summary>
    public static AlertGroup Example()
    {
        var group = new AlertGroup { Name = new TextNode("Example") };
        group.Rules.Content.Add(AlertRule.Example());
        return group;
    }

    /// <summary>Off switches every rule in the group off, whatever its own Enabled says.</summary>
    [Menu("Enabled")]
    public ToggleNode Enabled { get; set; } = new(true);

    [Menu("Name")]
    public TextNode Name { get; set; } = new("New group");

    /// <summary>
    /// The group's rules. Left empty here: ExileCore deserializes settings without ObjectCreationHandling.Replace, so a
    /// list filled here would be appended to on every load.
    /// </summary>
    [Menu("Rules")]
    public ContentNode<AlertRule> Rules { get; set; } = new()
    {
        EnableControls = true,
        EnableItemCollapsing = true,
        ItemFactory = () => new AlertRule(),
        ItemFilter = (rule, search) => rule.SearchTerm.Value.Contains(search, StringComparison.OrdinalIgnoreCase),
        Content = new List<AlertRule>(),
    };

    /// <summary>The group's entry in the list. The trailing ### keeps ImGui's id stable while the text changes.</summary>
    public override string ToString()
    {
        var name = string.IsNullOrWhiteSpace(Name?.Value) ? "<no name>" : Name.Value;
        return $"{(Enabled?.Value == true ? "" : "(disabled) ")}{name}###";
    }
}
