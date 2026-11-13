using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ImGuiNET;
using Graphics = ExileCore.Graphics;

namespace MonsterModAlerter;

/// <summary>
/// The Inspect world entities window: the loaded entities, nearest first, monsters only unless Show all entities is
/// ticked, each with what an alert rule can match it by - metadata path, mods, buffs, rarity, hostile, alive - in
/// read-only text fields that can be selected and copied with Ctrl+C. Opened from the button at the top of the
/// settings.
///
/// After DevTree's window of the same name: the list is a snapshot, taken when the window opens and by Refresh, so the
/// rows do not reorder under the mouse as distances change. Hovering an entity's row or details outlines it in the
/// world with its bounding box (Graphics.DrawBoundingBoxInWorld from the Render component, as WhereAreYouGoing and
/// WhereTheWispsAt draw theirs) and draws a line to it from the player, as DevTree does, for one off screen.
/// </summary>
internal sealed class EntityInspector
{
    private const string MonsterPathPrefix = "Metadata/Monsters";

    /// <summary>
    /// Set by the settings button, cleared by the window's close button. Static: settings nodes cannot reach the
    /// plugin.
    /// </summary>
    public static bool Open { get; set; }

    /// <summary>AutoExpedition's green, as the cursor message and the craft square use for "this one".</summary>
    private static readonly SharpDX.Color HighlightColour = Colours.FromArgb(255, 90, 255, 120);

    private bool _snapshotDue = true;
    private bool _showAllEntities;
    private List<Entity> _snapshot = new();

    /// <summary>
    /// Makes the next draw take a new snapshot. Called on area change, when every entity in it is gone.
    /// </summary>
    public void Invalidate() => _snapshotDue = true;

    public void Draw(GameController gameController, Graphics graphics)
    {
        if (!Open)
        {
            _snapshotDue = true;
            return;
        }

        if (_snapshotDue)
        {
            _snapshot = SnapshotOf(gameController);
            _snapshotDue = false;
        }

        Entity hovered = null;
        var windowOpen = true;

        ImGui.SetNextWindowSize(new Vector2(640f, 560f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Inspect world entities###mmaEntityInspector", ref windowOpen))
        {
            ImGui.Checkbox("Show all entities", ref _showAllEntities);
            ImGui.SameLine();

            if (ImGui.Button("Refresh"))
                _snapshot = SnapshotOf(gameController);

            var shown = _showAllEntities
                ? _snapshot
                : _snapshot.Where(entity => Safe.Read(entity, static e => e.Path, "")
                    .StartsWith(MonsterPathPrefix, StringComparison.OrdinalIgnoreCase)).ToList();

            ImGui.SameLine();
            ImGui.TextDisabled($"{shown.Count} of {_snapshot.Count}, nearest first");
            ImGui.Separator();

            if (ImGui.BeginChild("##mmaEntities"))
            {
                foreach (var entity in shown)
                {
                    ImGui.PushID((int)Safe.Read(entity, static e => e.Id, 0u));

                    // The row and its details as one group, so hovering either highlights the entity.
                    ImGui.BeginGroup();

                    if (ImGui.TreeNodeEx($"{LabelOf(entity)}###entity", ImGuiTreeNodeFlags.SpanAvailWidth))
                    {
                        DrawDetailsOf(entity);
                        ImGui.TreePop();
                    }

                    ImGui.EndGroup();

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                        hovered = entity;

                    ImGui.PopID();
                }
            }

            ImGui.EndChild();
        }

        ImGui.End();

        if (!windowOpen)
            Open = false;

        if (hovered != null)
            Highlight(gameController, graphics, hovered);
    }

    /// <summary>Every valid entity with a position, nearest to the player first.</summary>
    private static List<Entity> SnapshotOf(GameController gameController)
    {
        var entities = Safe.Read(gameController, static g => g.EntityListWrapper.OnlyValidEntities?.ToList(), null);

        if (entities == null)
            return new List<Entity>();

        return entities
            .Where(entity => entity != null && Safe.Read(entity, static e => e.HasComponent<Positioned>(), false))
            .OrderBy(entity => Safe.Read(entity, static e => e.DistancePlayer, float.MaxValue))
            .ToList();
    }

    /// <summary>
    /// The row's title: the short metadata path, the rarity for a monster, the distance, and (gone) if gone.
    /// </summary>
    private static string LabelOf(Entity entity) => Safe.Read(entity, static e =>
    {
        var path = e.Path ?? "";
        var rarity = path.StartsWith(MonsterPathPrefix, StringComparison.OrdinalIgnoreCase) ? $"  {e.Rarity}" : "";
        var gone = e.IsValid ? "" : "  (gone)";
        return $"{MetadataPaths.ShortNameOf(path)}{rarity}  {e.DistancePlayer:0}{gone}";
    }, "<unreadable>");

    private static void DrawDetailsOf(Entity entity)
    {
        var path = Safe.Read(entity, static e => e.Path, "") ?? "";
        var name = Safe.Read(entity,
            static e => e.TryGetComponent<Render>(out var render) ? render?.Name : null, null) ?? "";

        CopyableLine("Metadata path", path);
        CopyableLine("Name (Render.Name)", name);

        var facts = Safe.Read(entity, static e =>
            $"Rarity {e.Rarity}   Hostile {(e.IsHostile ? "yes" : "no")}   Alive {(e.IsAlive ? "yes" : "no")}   " +
            $"Distance {e.DistancePlayer:0}   Id {e.Id}", "");
        ImGui.TextDisabled(facts);

        var mods = Safe.Read(entity, static e => e.TryGetComponent<ObjectMagicProperties>(out var props)
            ? props?.Mods?.Where(m => !string.IsNullOrEmpty(m)).ToList()
            : null, null) ?? [];
        var rare = mods.Where(m => m.StartsWith(RuleMatcher.RareModPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        CopyableLines("mods", $"Mods (ObjectMagicProperties.Mods): {mods.Count}, {rare} rare", mods);

        var buffs = Safe.Read(entity, static e => e.TryGetComponent<Buffs>(out var b)
            ? b?.BuffsList?.Select(buff => buff?.Name).Where(n => !string.IsNullOrEmpty(n)).ToList()
            : null, null) ?? [];
        CopyableLines("buffs", $"Buffs (Buffs.BuffsList): {buffs.Count}", buffs);
    }

    /// <summary>A label and a read-only one-line text field, which can be selected and copied with Ctrl+C.</summary>
    private static void CopyableLine(string label, string text)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputText($"##{label}", ref text, (uint)text.Length + 1, ImGuiInputTextFlags.ReadOnly);
    }

    /// <summary>
    /// A label and a read-only text box of one entry per line, up to ten lines high, then it scrolls.
    /// </summary>
    /// <param name="id">The text box's ImGui id, fixed, since the label carries a count that changes.</param>
    private static void CopyableLines(string id, string label, List<string> lines)
    {
        ImGui.TextUnformatted(label);

        if (lines.Count == 0)
            return;

        var text = string.Join("\n", lines);
        var height = ImGui.GetTextLineHeight() * Math.Min(lines.Count, 10) + ImGui.GetStyle().FramePadding.Y * 2f + 2f;
        ImGui.InputTextMultiline($"##{id}", ref text, (uint)text.Length + 1, new Vector2(-1f, height),
            ImGuiInputTextFlags.ReadOnly);
    }

    private static void Highlight(GameController gameController, Graphics graphics, Entity entity)
    {
        Safe.Try(() =>
        {
            if (entity.TryGetComponent<Render>(out var render) && render != null)
                graphics.DrawBoundingBoxInWorld(entity.PosNum, HighlightColour, render.BoundsNum, render.RotationNum.X);

            if (gameController.Player is { } player)
                graphics.DrawLineInWorld(player.GridPosNum, entity.GridPosNum, 2f, HighlightColour);
        });
    }
}
