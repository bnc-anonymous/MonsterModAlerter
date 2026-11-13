using System.Collections.Generic;
using System.Runtime.CompilerServices;
using ExileCore;
using ExileCore.Shared.Interfaces;
using ImGuiNET;

namespace MonsterModAlerter;

/// <summary>
/// Draws the Alert groups list and each group's rules, so that both can be reordered by dragging and a rule moved into
/// another group - which ExileCore's ContentNode list cannot do. The look follows ReAgent's group and rule lists: a
/// drag handle, the group or rule's Enabled on its header, and its settings below when open. Those settings are still
/// drawn by ExileCore: each group and rule is parsed once by SettingsParser, as the root settings are, and its drawers
/// called here.
/// </summary>
internal static class AlertGroupsEditor
{
    private const string GroupPayload = "MmaGroupIndex";
    private const string RulePayload = "MmaRuleIndex";

    /// <summary>Where a dragged rule came from: its group's index and its own index in that group.</summary>
    private readonly record struct RulePosition(int Group, int Rule);

    /// <summary>Each group's or rule's ExileCore drawers, parsed on first draw and dropped with the object.</summary>
    private static readonly ConditionalWeakTable<object, List<ISettingsHolder>> DrawersByObject = new();

    public static void Draw(MonsterModAlerterSettings settings)
    {
        var groups = settings.Groups.Content;

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

            DragHandle(GroupPayload, g, group.HeaderText);
            ImGui.SameLine();
            EnabledCheckbox(group.Enabled);
            ImGui.SameLine();

            var open = ImGui.CollapsingHeader($"{group.HeaderText}###group");
            if (DroppedOn<int>(GroupPayload) is { } from)
                groupMove = (from, g);

            if (open)
            {
                ImGui.Indent();
                DrawSettingsOf(group);

                var rules = group.Rules.Content;

                for (var r = 0; r < rules.Count; r++)
                {
                    var rule = rules[r];
                    ImGui.PushID(RuntimeHelpers.GetHashCode(rule));

                    DragHandle(RulePayload, new RulePosition(g, r), rule.HeaderText);
                    ImGui.SameLine();
                    EnabledCheckbox(rule.Enabled);
                    ImGui.SameLine();

                    var ruleOpen = ImGui.CollapsingHeader($"{rule.HeaderText}###rule");
                    if (DroppedOn<RulePosition>(RulePayload) is { } source)
                        ruleMove = (source, new RulePosition(g, r));

                    if (ruleOpen)
                    {
                        ImGui.Indent();
                        DrawSettingsOf(rule);

                        if (CtrlButton("Delete rule"))
                            ruleToDelete = new RulePosition(g, r);

                        ImGui.Unindent();
                    }

                    ImGui.PopID();
                }

                if (ImGui.Button("Add rule"))
                    rules.Add(new AlertRule());

                ImGui.SameLine();

                if (CtrlButton("Delete group"))
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
            ImGui.SetTooltip("Drag onto another to move it there.");
        }
    }

    /// <summary>The payload dropped on the item just drawn, if one was released over it this frame.</summary>
    private static T? DroppedOn<T>(string payloadType) where T : unmanaged
    {
        if (!ImGui.BeginDragDropTarget())
            return null;

        var payload = ImGuiHelpers.AcceptDragDropPayload<T>(payloadType);
        ImGui.EndDragDropTarget();

        // As ReAgent does: acted on only as the mouse is let go over the target.
        return payload != null && ImGui.IsMouseReleased(ImGuiMouseButton.Left) ? payload : null;
    }

    private static void EnabledCheckbox(ExileCore.Shared.Nodes.ToggleNode enabled)
    {
        var value = enabled.Value;
        if (ImGui.Checkbox("##enabled", ref value))
            enabled.Value = value;

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Enabled");
    }

    /// <summary>A button clickable only while Ctrl is held, as the reset button is, so a stray click cannot delete.</summary>
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

    /// <summary>Moves a rule to where it was dropped: within its group, or into another group at that place.</summary>
    private static void MoveRule(List<AlertGroup> groups, RulePosition from, RulePosition to)
    {
        if (from.Group < 0 || from.Group >= groups.Count || to.Group < 0 || to.Group >= groups.Count)
            return;

        var source = groups[from.Group].Rules.Content;

        if (from.Group == to.Group)
        {
            Move(source, from.Rule, to.Rule);
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
