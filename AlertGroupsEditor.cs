using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ExileCore;
using ExileCore.Shared.Interfaces;
using ImGuiNET;

namespace MonsterModAlerter;

/// <summary>
/// Draws the Alert rules section: the groups and each group's rules, so that both can be reordered by dragging and a
/// rule moved into another group - which ExileCore's ContentNode list cannot do. The look follows ReAgent's group and
/// rule lists: a drag handle, the group or rule's Enabled on its header, and its settings below when open. Those
/// settings are still drawn by ExileCore: each group and rule is parsed once by SettingsParser, as the root settings
/// are, and its drawers called here.
///
/// A whole row - handle, checkbox and title - is a drop target, not the title alone: it is drawn as an ImGui group,
/// which a drop target can be attached to. Dropping a group on a group's row moves it there; a rule on a rule's row
/// moves it there; a rule on a group's row moves it to the end of that group, open or not.
/// </summary>
internal static class AlertGroupsEditor
{
    private const string GroupPayload = "MmaGroupIndex";
    private const string RulePayload = "MmaRuleIndex";

    /// <summary>
    /// A rule's place: its group's index and its own index in that group. EndOfGroup as Rule means last.
    /// </summary>
    private readonly record struct RulePosition(int Group, int Rule);

    private const int EndOfGroup = int.MaxValue;

    /// <summary>
    /// A group's or rule's title: an unframed tree node, an arrow and the text, rather than CollapsingHeader's framed
    /// bar in the theme's header colour. Clickable across the row's width; the indent below it is this class's own, so
    /// nothing is pushed for it.
    /// </summary>
    private const ImGuiTreeNodeFlags RowFlags =
        ImGuiTreeNodeFlags.SpanAvailWidth | ImGuiTreeNodeFlags.NoTreePushOnOpen;

    /// <summary>Each group's or rule's ExileCore drawers, parsed on first draw and dropped with the object.</summary>
    private static readonly ConditionalWeakTable<object, List<ISettingsHolder>> DrawersByObject = new();

    /// <summary>The Alert rules section, closed by default like the other sections.</summary>
    public static void Draw(MonsterModAlerterSettings settings)
    {
        if (!ImGui.TreeNodeEx("Alert rules###mmaAlertRules"))
            return;

        DrawGroups(settings.Groups.Content);
        ImGui.TreePop();
    }

    private static void DrawGroups(List<AlertGroup> groups)
    {
        // Moves and deletions are applied after the loop, never inside it, so the list is not changed while drawn.
        (int From, int To)? groupMove = null;
        (RulePosition From, RulePosition To)? ruleMove = null;
        int? groupToDelete = null;
        RulePosition? ruleToDelete = null;

        for (var g = 0; g < groups.Count; g++)
        {
            var group = groups[g];

            // Keyed by the object, not its index, so a header keeps its open state when the group moves.
            ImGui.PushID(RuntimeHelpers.GetHashCode(group));

            ImGui.BeginGroup();
            DragHandle(GroupPayload, g, group.HeaderText);
            ImGui.SameLine();
            EnabledCheckbox(group.Enabled);
            ImGui.SameLine();
            var open = ImGui.TreeNodeEx($"{group.HeaderText}###group", RowFlags);
            ImGui.EndGroup();

            var (droppedGroup, droppedRule) = DroppedOnRow();
            if (droppedGroup is { } fromGroup)
                groupMove = (fromGroup, g);
            if (droppedRule is { } fromRule)
                ruleMove = (fromRule, new RulePosition(g, EndOfGroup));

            if (open)
            {
                ImGui.Indent();
                DrawSettingsOf(group);

                var rules = group.Rules.Content;

                for (var r = 0; r < rules.Count; r++)
                {
                    var rule = rules[r];
                    ImGui.PushID(RuntimeHelpers.GetHashCode(rule));

                    ImGui.BeginGroup();
                    DragHandle(RulePayload, new RulePosition(g, r), rule.HeaderText);
                    ImGui.SameLine();
                    EnabledCheckbox(rule.Enabled);
                    ImGui.SameLine();
                    var ruleOpen = ImGui.TreeNodeEx($"{rule.HeaderText}###rule", RowFlags);
                    ImGui.EndGroup();

                    if (DroppedOnRow().Rule is { } source)
                        ruleMove = (source, new RulePosition(g, r));

                    if (ruleOpen)
                    {
                        ImGui.Indent();

                        // A ListNode's choices are not saved, so they are supplied before it is drawn.
                        if (!ReferenceEquals(rule.PlaySound.Values, AlertSound.SoundNames))
                            rule.PlaySound.Values = AlertSound.SoundNames;

                        DrawSettingsOf(rule);

                        if (CtrlButton($"Delete rule: {rule.DisplayName}###deleteRule"))
                            ruleToDelete = new RulePosition(g, r);

                        ImGui.Unindent();
                    }

                    ImGui.PopID();
                }

                if (ImGui.Button("Add rule"))
                    rules.Add(new AlertRule());

                ImGui.SameLine();

                if (CtrlButton($"Delete group: {group.DisplayName}###deleteGroup"))
                    groupToDelete = g;

                ImGui.Unindent();
            }

            ImGui.PopID();
        }

        if (ImGui.Button("Add group"))
            groups.Add(new AlertGroup());

        if (groupMove is { } gm)
            Move(groups, gm.From, gm.To);

        if (ruleMove is { } rm)
            MoveRule(groups, rm.From, rm.To);

        if (ruleToDelete is { } rd && rd.Group < groups.Count && rd.Rule < groups[rd.Group].Rules.Content.Count)
            groups[rd.Group].Rules.Content.RemoveAt(rd.Rule);

        if (groupToDelete is { } gd && gd < groups.Count)
            groups.RemoveAt(gd);
    }

    /// <summary>The "=" handle a group or rule is dragged by, showing its name while dragged.</summary>
    private static void DragHandle<T>(string payloadType, T payload, string name) where T : unmanaged
    {
        ImGui.PushStyleColor(ImGuiCol.Button, 0);
        ImGui.Button("=");
        ImGui.PopStyleColor();

        if (ImGui.BeginDragDropSource())
        {
            ImGuiHelpers.SetDragDropPayload(payloadType, payload);
            ImGui.Text(name);
            ImGui.EndDragDropSource();
        }
        else if (ImGui.IsItemHovered())
        {
            ImGui.SetTooltip("Drag onto another row to move it there.");
        }
    }

    /// <summary>
    /// What was dropped on the row just drawn (the last ImGui group), if anything was released over it this frame: a
    /// group's index or a rule's place.
    /// </summary>
    private static (int? Group, RulePosition? Rule) DroppedOnRow()
    {
        if (!ImGui.BeginDragDropTarget())
            return (null, null);

        var group = ImGuiHelpers.AcceptDragDropPayload<int>(GroupPayload);
        var rule = ImGuiHelpers.AcceptDragDropPayload<RulePosition>(RulePayload);
        ImGui.EndDragDropTarget();

        // As ReAgent does: acted on only as the mouse is let go over the target.
        return ImGui.IsMouseReleased(ImGuiMouseButton.Left) ? (group, rule) : (null, null);
    }

    private static void EnabledCheckbox(ExileCore.Shared.Nodes.ToggleNode enabled)
    {
        var value = enabled.Value;
        if (ImGui.Checkbox("##enabled", ref value))
            enabled.Value = value;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enabled");
    }

    /// <summary>
    /// A button clickable only while Ctrl is held, as the reset button is, so a stray click cannot delete.
    /// </summary>
    private static bool CtrlButton(string label)
    {
        var armed = ImGui.GetIO().KeyCtrl;

        ImGui.BeginDisabled(!armed);
        var clicked = ImGui.Button(label);
        ImGui.EndDisabled();

        if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenDisabled))
            ImGui.SetTooltip("Hold Ctrl to click it.");

        return clicked && armed;
    }

    private static void DrawSettingsOf(ISettings settings)
    {
        if (!DrawersByObject.TryGetValue(settings, out var drawers))
        {
            drawers = new List<ISettingsHolder>();
            SettingsParser.Parse(settings, drawers);
            DrawersByObject.Add(settings, drawers);
        }

        foreach (var drawer in drawers)
            drawer.Draw();
    }

    private static void Move<T>(List<T> list, int from, int to)
    {
        if (from == to || from < 0 || to < 0 || from >= list.Count || to >= list.Count)
            return;

        var item = list[from];
        list.RemoveAt(from);
        list.Insert(to, item);
    }

    /// <summary>
    /// Moves a rule to where it was dropped: within its group, or into another group at that place or at its end.
    /// </summary>
    private static void MoveRule(List<AlertGroup> groups, RulePosition from, RulePosition to)
    {
        if (from.Group < 0 || from.Group >= groups.Count || to.Group < 0 || to.Group >= groups.Count)
            return;

        var source = groups[from.Group].Rules.Content;

        if (from.Group == to.Group)
        {
            Move(source, from.Rule, to.Rule == EndOfGroup ? source.Count - 1 : to.Rule);
            return;
        }

        if (from.Rule < 0 || from.Rule >= source.Count)
            return;

        var target = groups[to.Group].Rules.Content;
        var rule = source[from.Rule];
        source.RemoveAt(from.Rule);
        target.Insert(System.Math.Clamp(to.Rule, 0, target.Count), rule);
    }
}
