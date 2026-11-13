using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
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
/// rows do not reorder under the mouse as distances change. Hovering an entity's row or details marks it in the world
/// with DevTree's hovered pin - a disc at its feet, a stalk and its name above - and a line to it from the player, for
/// one off screen. The game's own outline of a hovered monster cannot be had: the game draws it for the entity under
/// the mouse, and setting that would mean writing to the game's memory. The bounding box ExileCore can draw
/// (Render.BoundsNum) was tried and covered the tile rather than the monster (seen in game, 2026-11-13).
///
/// The filter matches a word in the metadata path, the name, a mod or a buff, ignoring case.
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

    // DevTree's pin at its hovered size: its defaults (foot 4, stalk 100, in world units) doubled by its hovered
    // expansion factor of 2. The name on top stays at text scale 1, which the doubled 2 was too large for.
    private const float PinFootRadius = 8f;
    private const float PinStalkHeight = 200f;
    private const float PinTextScale = 1f;

    private bool _snapshotDue = true;
    private bool _showAllEntities;
    private string _filter = "";
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

            // A label before the box, so a drag that overshoots the box's left edge stays over the window: off it, the
            // HUD sends keys to the game and Delete stops working. A click selects all the text, so replacing it needs
            // no drag at all.
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Filter:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##mmaEntityFilter", "metadata path, name, mod or buff", ref _filter, 200,
                ImGuiInputTextFlags.AutoSelectAll);

            var filter = _filter.Trim();
            var shown = _snapshot
                .Where(entity => _showAllEntities || Safe.Read(entity, static e => e.Path, "")
                    .StartsWith(MonsterPathPrefix, StringComparison.OrdinalIgnoreCase))
                .Where(entity => filter.Length == 0 || MatchesFilter(entity, filter))
                .ToList();

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

    /// <summary>
    /// Whether the filter is part of the entity's metadata path, its name, one of its mods or one of its buffs.
    /// </summary>
    private static bool MatchesFilter(Entity entity, string filter)
    {
        bool Has(string text) => text?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

        return Has(Safe.Read(entity, static e => e.Path, null)) ||
               Has(NameOf(entity)) ||
               ModsOf(entity).Any(Has) ||
               BuffsOf(entity).Any(Has);
    }

    private static string NameOf(Entity entity) =>
        Safe.Read(entity, static e => e.TryGetComponent<Render>(out var render) ? render?.Name : null, null) ?? "";

    private static List<string> ModsOf(Entity entity) =>
        Safe.Read(entity, static e => e.TryGetComponent<ObjectMagicProperties>(out var props)
            ? props?.Mods?.Where(m => !string.IsNullOrEmpty(m)).ToList()
            : null, null) ?? [];

    private static List<string> BuffsOf(Entity entity) =>
        Safe.Read(entity, static e => e.TryGetComponent<Buffs>(out var b)
            ? b?.BuffsList?.Select(buff => buff?.Name).Where(n => !string.IsNullOrEmpty(n)).ToList()
            : null, null) ?? [];

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
        return $"{MetadataPaths.ShortNameOf(MetadataPaths.WithoutLevelTag(path))}{rarity}  {e.DistancePlayer:0}{gone}";
    }, "<unreadable>");

    private static void DrawDetailsOf(Entity entity)
    {
        var path = Safe.Read(entity, static e => e.Path, "") ?? "";
        var name = NameOf(entity);

        // Without the level tag, so a path copied into a rule matches the monster at every level.
        CopyableLine("Metadata path", MetadataPaths.WithoutLevelTag(path));
        CopyableLine("Name (Render.Name)", name);

        var facts = Safe.Read(entity, static e =>
            $"Rarity {e.Rarity}   Hostile {(e.IsHostile ? "yes" : "no")}   Alive {(e.IsAlive ? "yes" : "no")}   " +
            $"Distance {e.DistancePlayer:0}   Id {e.Id}" +
            (MetadataPaths.LevelTagOf(e.Path) is { } tag ? $"   Path tag {tag}" : ""), "");
        ImGui.TextDisabled(facts);

        var mods = ModsOf(entity);
        var rare = mods.Where(m => m.StartsWith(RuleMatcher.RareModPrefix, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        CopyableLines("mods", $"Mods (ObjectMagicProperties.Mods): {mods.Count}, {rare} rare", mods);

        var buffs = BuffsOf(entity);
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

    /// <summary>
    /// DevTree's hovered pin on the entity - a disc on the ground at its feet, a stalk up from it and its short name at
    /// the top - and a line on the ground from the player to it.
    /// </summary>
    private static void Highlight(GameController gameController, Graphics graphics, Entity entity)
    {
        Safe.Try(() =>
        {
            var gridPos = entity.GridPosNum;
            var feet = gameController.IngameState.Data.ToWorldWithTerrainHeight(gridPos);
            var camera = gameController.IngameState.Camera;
            var stalkBottom = camera.WorldToScreen(feet);
            var stalkTop = camera.WorldToScreen(feet with { Z = feet.Z - PinStalkHeight });

            if (gameController.Player is { } player)
                graphics.DrawLineInWorld(player.GridPosNum, gridPos, 2f, HighlightColour);

            graphics.DrawFilledCircleInWorld(feet, PinFootRadius, HighlightColour);
            graphics.DrawLine(stalkBottom, stalkTop, 2f, HighlightColour);

            using (graphics.SetTextScale(PinTextScale))
            {
                var name = MetadataPaths.ShortNameOf(MetadataPaths.WithoutLevelTag(entity.Path));
                graphics.DrawTextWithBackground(name, stalkTop, HighlightColour,
                    FontAlign.Center | FontAlign.VerticalCenter, SharpDX.Color.Black);
            }
        });
    }
}
