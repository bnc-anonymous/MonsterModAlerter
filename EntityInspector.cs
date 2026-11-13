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
/// After DevTree's window of the same name, its rows titled as DevTree titles them: the list is a snapshot, taken when
/// the window opens and by Refresh, so the rows do not reorder under the mouse as distances change. With Show pins on,
/// every listed entity carries DevTree's pin in the world - a disc at its feet, a stalk and its row's number above - so
/// a monster on screen can be found in the list. Hovering an entity's row or details turns its pin green and doubles
/// it, and draws a line to it from the player, for one off screen. The game's own outline of a hovered monster cannot
/// be had: the game draws it for the entity under the mouse, and setting that would mean writing to the game's memory.
/// The bounding box ExileCore can draw (Render.BoundsNum) was tried and covered the tile rather than the monster (seen
/// in game, 2026-11-13).
///
/// The filter matches text in the row's title (so "(Rare)" finds rare monsters, where "rare" would also find any
/// with a RareMonsterPack mod), the metadata path, the name, a mod or a buff, ignoring case.
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

    // DevTree's pin colour for a listed entity, and AutoExpedition's red for one gone since the snapshot.
    private static readonly SharpDX.Color PinColour = SharpDX.Color.White;
    private static readonly SharpDX.Color GonePinColour = Colours.FromArgb(255, 235, 90, 90);

    // DevTree's pin at its default sizes, in world units: a disc of radius 4 at the feet and a stalk 100 high, both
    // doubled on the hovered entity's pin (DevTree's hovered expansion factor). DevTree doubles the text too; here it
    // stays at scale 1, since doubled text was too large in game when the hovered pin carried the name (2026-11-13).
    private const float PinFootRadius = 4f;
    private const float PinStalkHeight = 100f;
    private const float HoveredPinScale = 2f;
    private const float PinTextScale = 1f;

    /// <summary>How far off screen, in pixels, a pin's foot may be and the pin still drawn: DevTree's margin.</summary>
    private const float PinScreenMargin = 50f;

    /// <summary>
    /// A row of the list: the entity, its index (its place in the snapshot, as in DevTree, so filtering does not
    /// renumber rows) and the row's title, made once a frame for the filter, the row and its pin.
    /// </summary>
    private readonly record struct EntityRow(Entity Entity, int Index, string Title);

    private bool _snapshotDue = true;
    private bool _showAllEntities;
    private bool _showPins = true;
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

        EntityRow? hovered = null;
        List<EntityRow> shown = null;
        var windowOpen = true;

        ImGui.SetNextWindowSize(new Vector2(640f, 560f), ImGuiCond.FirstUseEver);

        if (ImGui.Begin("Inspect world entities###mmaEntityInspector", ref windowOpen))
        {
            ImGui.Checkbox("Show all entities", ref _showAllEntities);
            ImGui.SameLine();

            if (ImGui.Button("Refresh"))
                _snapshot = SnapshotOf(gameController);

            ImGui.SameLine();
            ImGui.Checkbox("Show pins", ref _showPins);

            if (ImGui.IsItemHovered())
                ImGui.SetTooltip("Marks each listed entity in the world with its number, as DevTree does.");

            // A label before the box, so a drag that overshoots the box's left edge stays over the window: off it, the
            // HUD sends keys to the game and Delete stops working. A click selects all the text, so replacing it needs
            // no drag at all.
            ImGui.AlignTextToFramePadding();
            ImGui.TextUnformatted("Filter:");
            ImGui.SameLine();
            ImGui.SetNextItemWidth(-1f);
            ImGui.InputTextWithHint("##mmaEntityFilter", "row text, metadata path, name, mod or buff", ref _filter,
                200, ImGuiInputTextFlags.AutoSelectAll);

            var filter = _filter.Trim();
            shown = _snapshot
                .Select((entity, index) => (Entity: entity, Index: index))
                .Where(item => _showAllEntities || Safe.Read(item.Entity, static e => e.Path, "")
                    .StartsWith(MonsterPathPrefix, StringComparison.OrdinalIgnoreCase))
                .Select(item => new EntityRow(item.Entity, item.Index, TitleOf(item.Entity, item.Index)))
                .Where(row => filter.Length == 0 || MatchesFilter(row, filter))
                .ToList();

            ImGui.TextDisabled(_showAllEntities
                ? $"Showing {shown.Count} {(shown.Count == 1 ? "entity" : "entities")}, nearest first"
                : $"Showing {shown.Count} {(shown.Count == 1 ? "monster" : "monsters")} out of {_snapshot.Count} " +
                  "total entities, nearest first");
            ImGui.Separator();

            if (ImGui.BeginChild("##mmaEntities"))
            {
                foreach (var row in shown)
                {
                    ImGui.PushID((int)Safe.Read(row.Entity, static e => e.Id, 0u));

                    // The row and its details as one group, so hovering either highlights the entity.
                    ImGui.BeginGroup();

                    if (ImGui.TreeNodeEx($"{row.Title}###entity", ImGuiTreeNodeFlags.SpanAvailWidth))
                    {
                        DrawDetailsOf(row.Entity);
                        ImGui.TreePop();
                    }

                    ImGui.EndGroup();

                    if (ImGui.IsItemHovered(ImGuiHoveredFlags.AllowWhenBlockedByActiveItem))
                        hovered = row;

                    ImGui.PopID();
                }
            }

            ImGui.EndChild();
        }

        ImGui.End();

        if (!windowOpen)
        {
            Open = false;
            return;
        }

        // Nothing listed while the window is collapsed, so no pins. The hovered pin is drawn last, over the others.
        if (shown == null)
            return;

        if (_showPins)
        {
            foreach (var row in shown.Where(row => row.Entity != hovered?.Entity))
                DrawPin(gameController, graphics, row.Entity, row.Index, isHovered: false);
        }

        if (hovered is { } hoveredRow)
            DrawPin(gameController, graphics, hoveredRow.Entity, hoveredRow.Index, isHovered: true);
    }

    /// <summary>
    /// Whether the filter is part of the row's title, the entity's metadata path, its name, one of its mods or one of
    /// its buffs. The title is checked first: it is made already, where the rest are read from the entity.
    /// </summary>
    private static bool MatchesFilter(EntityRow row, string filter)
    {
        bool Has(string text) => text?.Contains(filter, StringComparison.OrdinalIgnoreCase) == true;

        var entity = row.Entity;

        return Has(row.Title) ||
               Has(Safe.Read(entity, static e => e.Path, null)) ||
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
    /// The row's title, exactly as DevTree's entity list writes it: "[index] ", then the entity's own ToString(), then
    /// its address in hex in square brackets unless the text already holds it (DevTree.DebugCollectionCore, with its
    /// Hide addresses setting at its default, off).
    /// </summary>
    private static string TitleOf(Entity entity, int index)
    {
        var text = Safe.Read(entity, static e => e.ToString(), null) ?? "ToString(): <exception thrown>";
        var address = $"{Safe.Read(entity, static e => e.Address, 0L):X}";

        if (!text.Contains(address))
            text += $" [{address}]";

        return $"[{index}] {text}";
    }

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
    /// A label and a read-only text box of one entry per line, one to ten lines high, then it scrolls.
    /// </summary>
    /// <param name="id">The text box's ImGui id, fixed, since the label carries a count that changes.</param>
    private static void CopyableLines(string id, string label, List<string> lines)
    {
        ImGui.TextUnformatted(label);

        // An empty list still gets its box, one line high, so every entity's details have the same shape.
        var text = string.Join("\n", lines);
        var height = ImGui.GetTextLineHeight() * Math.Clamp(lines.Count, 1, 10) +
                     ImGui.GetStyle().FramePadding.Y * 2f + 2f;
        ImGui.InputTextMultiline($"##{id}", ref text, (uint)text.Length + 1, new Vector2(-1f, height),
            ImGuiInputTextFlags.ReadOnly);
    }

    /// <summary>
    /// DevTree's pin on an entity: a disc on the ground at its feet, a stalk up from it and its number at the top,
    /// white, or red once the entity is gone. The hovered entity's pin is green and twice the size, with a line on the
    /// ground from the player to it, drawn even when the pin is not. A pin whose foot is off screen is not drawn.
    /// </summary>
    private static void DrawPin(GameController gameController, Graphics graphics, Entity entity, int index,
        bool isHovered)
    {
        Safe.Try(() =>
        {
            var gridPos = entity.GridPosNum;
            var colour = isHovered ? HighlightColour : entity.IsValid ? PinColour : GonePinColour;

            if (isHovered && gameController.Player is { } player)
                graphics.DrawLineInWorld(player.GridPosNum, gridPos, 2f, colour);

            var feet = gameController.IngameState.Data.ToWorldWithTerrainHeight(gridPos);
            var camera = gameController.IngameState.Camera;
            var stalkBottom = camera.WorldToScreen(feet);
            var screen = gameController.Window.GetWindowRectangleTimeCache with { Location = SharpDX.Vector2.Zero };
            screen.Inflate(PinScreenMargin, PinScreenMargin);

            if (!screen.Contains(stalkBottom.X, stalkBottom.Y))
                return;

            var size = isHovered ? HoveredPinScale : 1f;
            var stalkTop = camera.WorldToScreen(feet with { Z = feet.Z - PinStalkHeight * size });

            graphics.DrawFilledCircleInWorld(feet, PinFootRadius * size, colour);
            graphics.DrawLine(stalkBottom, stalkTop, 2f, colour);

            using (graphics.SetTextScale(PinTextScale))
            {
                graphics.DrawTextWithBackground($"{index}", stalkTop, colour,
                    FontAlign.Center | FontAlign.VerticalCenter, SharpDX.Color.Black);
            }
        });
    }
}
