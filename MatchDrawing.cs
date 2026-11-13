using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Helpers;
using Color = SharpDX.Color;
using RectangleF = SharpDX.RectangleF;

namespace MonsterModAlerter;

/// <summary>
/// Draws the scan's matches and remembered positions: icons on the minimap and large map, icons, boxes and labels in
/// the world, and the modifier counts.
/// </summary>
internal sealed class MatchDrawing
{
    /// <summary>The HUD's shared icon atlas, the one MinimapIcons draws from.</summary>
    private const string IconAtlasTexture = "Icons.png";

    private readonly Graphics _graphics;

    /// <summary>One per match, in the same order, refilled every frame by ReadPlacements.</summary>
    private readonly List<Placement> _placements = new();

    /// <summary>Entities that already have a count this frame; refilled by DrawModCounts.</summary>
    private readonly HashSet<uint> _numberedIds = new();

    /// <summary>
    /// Where one match is drawn this frame. Read once per match, so the icon pass and the count pass agree and neither
    /// reads the game. The default, for an entity that is gone, dead or unreadable, draws nothing.
    /// </summary>
    /// <param name="OnMap">Drawn on the minimap or large map, at MapPoint.</param>
    /// <param name="InWorld">Drawn in the world, at ScreenPoint.</param>
    private readonly record struct Placement(bool OnMap, Vector2 MapPoint, bool InWorld, Vector2 ScreenPoint);

    public MatchDrawing(Graphics graphics) => _graphics = graphics;

    /// <summary>
    /// Full window, minus whatever the open side panels cover. Anything drawn outside this would appear on top of the
    /// panel, which is what Limit Draw Region exists to prevent. The inventory and stash only cover part of the screen,
    /// so rather than hiding everything the drawable area shrinks to the part they leave uncovered, the way Radar does.
    /// </summary>
    public static RectangleF DrawRegionOf(
        GameController gameController, IngameUIElements ingameUi, bool limitDrawRegion)
    {
        var region = gameController.Window.GetWindowRectangleTimeCache with { Location = SharpDX.Vector2.Zero };

        if (!limitDrawRegion)
            return region;

        if (ingameUi.OpenRightPanel?.IsVisible == true)
            region.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;

        if (ingameUi.OpenLeftPanel?.IsVisible == true)
            region.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;

        return region;
    }

    public void Draw(
        GameController gameController, MonsterModAlerterSettings settings, List<RuleMatch> matches,
        List<RememberedPosition> outOfRange, RectangleF region, bool minimapVisible)
    {
        // Drawn first, so a live entity's icon always sits on top of a remembered one.
        if (minimapVisible)
            DrawRememberedPositions(outOfRange, region, settings.ModCount);

        ReadPlacements(gameController, matches, minimapVisible, settings.World.Enable);

        for (var i = 0; i < matches.Count; i++)
        {
            var placement = _placements[i];
            var match = matches[i];

            if (placement.OnMap && region.Contains(placement.MapPoint.X, placement.MapPoint.Y))
                DrawIcon(placement.MapPoint, match.Rule.IconSize.Value, match.Rule);

            if (placement.InWorld)
                DrawInWorld(settings.World, placement.ScreenPoint, match, region);
        }

        // A second pass, after every icon, so a number is never buried under one.
        DrawModCounts(settings, matches, region, minimapVisible);
    }

    private void ReadPlacements(
        GameController gameController, List<RuleMatch> matches, bool minimapVisible, bool worldEnabled)
    {
        _placements.Clear();

        foreach (var match in matches)
        {
            var state = (Drawing: this, GameController: gameController, Match: match,
                OnMap: minimapVisible && match.Rule.ShowOnMinimap, InWorld: worldEnabled && match.Rule.ShowInWorld);

            _placements.Add(Safe.Read(state,
                static s => s.Drawing.PlacementOf(s.GameController, s.Match, s.OnMap, s.InWorld), default));
        }
    }

    // GridToMap handles the small minimap and the open large map, including camera rotation and per-cell terrain
    // height.
    private Placement PlacementOf(GameController gameController, RuleMatch match, bool onMap, bool inWorld)
    {
        var entity = match.Entity;

        if (!entity.IsValid || (match.CheckAlive && !entity.IsAlive))
            return default;

        var mapPoint = default(Vector2);

        if (onMap)
        {
            var gridPos = entity.GridPosNum;
            mapPoint = _graphics.GridToMap(gridPos, gridPos);
        }

        var screenPoint = inWorld ? gameController.IngameState.Camera.WorldToScreen(entity.PosNum) : default;

        return new Placement(onMap, mapPoint, inWorld, screenPoint);
    }

    private void DrawInWorld(WorldDrawSettings world, Vector2 screenPos, RuleMatch match, RectangleF region)
    {
        if (!region.Contains(screenPos.X, screenPos.Y))
            return;

        var worldIconSize = match.Rule.WorldIconSize.Value;

        // The box frames the entity, not the icon, so it is drawn once no matter how many rules matched - several
        // identical boxes around one entity would just thicken the outline.
        if (world.DrawBox && match.Slot == 0)
        {
            var half = world.BoxSize.Value / 2f;
            var boxColor = world.UseRuleTintForBox ? match.Rule.Tint.Value : world.BoxColor.Value;
            _graphics.DrawFrame(
                new RectangleF(screenPos.X - half, screenPos.Y - half, world.BoxSize.Value, world.BoxSize.Value),
                boxColor,
                world.Thickness.Value);
        }

        if (world.DrawIcon)
            DrawIcon(screenPos, worldIconSize, match.Rule);

        if (!world.DrawLabel)
            return;

        // Sits below whichever of the two is taller, so it never overlaps either.
        var halfExtent = Math.Max(
            world.DrawBox ? world.BoxSize.Value : 0,
            world.DrawIcon ? worldIconSize : 0) / 2f;

        const float labelGap = 2f;

        var text = string.IsNullOrWhiteSpace(match.Rule.Label?.Value) ? match.MatchedName : match.Rule.Label.Value;
        using (_graphics.SetTextScale(world.LabelScale.Value))
        {
            var textSize = _graphics.MeasureText(text);

            // Stacked rather than side by side: labels are far wider than icons, so a row of them would run into each
            // other and into the neighbouring entities'.
            var y = screenPos.Y + halfExtent + labelGap + match.Slot * (textSize.Y + labelGap);
            _graphics.DrawText(text, new Vector2(screenPos.X - textSize.X / 2f, y), world.LabelColor.Value);
        }
    }

    private void DrawIcon(Vector2 center, int size, AlertRule rule)
    {
        var half = size / 2f;
        var rect = new RectangleF(center.X - half, center.Y - half, size, size);
        _graphics.DrawImage(IconAtlasTexture, rect, SpriteHelper.GetUV(rule.Icon), rule.Tint.Value);
    }

    /// <summary>
    /// Last known positions of entities that have dropped out of range. Drawn from a stored grid position, never from
    /// the entity: by definition it is no longer in the entity list.
    /// </summary>
    private void DrawRememberedPositions(
        List<RememberedPosition> stale, RectangleF region, ModCountSettings countSettings)
    {
        if (stale.Count == 0)
            return;

        foreach (var entry in stale)
        {
            var rule = entry.Rule;
            if (!rule.ShowOnMinimap)
                continue;

            var mapPoint = Safe.Read((Graphics: _graphics, entry.GridPos),
                static s => (Vector2?)s.Graphics.GridToMap(s.GridPos, s.GridPos), null);

            if (mapPoint is not { } point || !region.Contains(point.X, point.Y))
                continue;

            DrawIcon(point, rule.IconSize.Value, rule);

            if (!rule.ShowModCount || entry.ModCount < rule.ModCountMinimum.Value)
                continue;

            // The count is the whole point of remembering these - it is what the kill order is decided on - so it is
            // drawn alongside rather than dropped. Rare affixes are fixed at spawn, so the remembered number stays
            // accurate however long ago it was read.
            using (_graphics.SetTextScale(countSettings.TextScale.Value))
            {
                var text = entry.ModCount.ToString();
                var size = _graphics.MeasureText(text);

                DrawNumber(text, size, point + countSettings.OffsetFor(rule.IconSize.Value, size),
                    region, rule.ModCountColor.Value, countSettings);
            }
        }
    }

    /// <summary>
    /// Draws each entity's modifier count as a bare number, e.g. "12". Centred on the entity by default so it reads as
    /// belonging to that dot rather than to a neighbouring one.
    /// </summary>
    private void DrawModCounts(
        MonsterModAlerterSettings settings, List<RuleMatch> matches, RectangleF region, bool minimapVisible)
    {
        var countSettings = settings.ModCount;
        var alsoWorld = countSettings.DrawInWorld;

        if (!minimapVisible && !alsoWorld)
            return;

        var any = false;
        foreach (var match in matches)
        {
            if (match.Rule.ShowModCount)
            {
                any = true;
                break;
            }
        }

        // Nothing to draw is the common case, so the text-scale scope is not even entered.
        if (!any)
            return;

        // One number per entity, however many of its rules ask for one - two would land on the same pixels. Walked
        // backwards because the list is sorted by ascending priority, so this hands the number to the highest-priority
        // rule, which matters once rules can be counting different things.
        _numberedIds.Clear();

        using (_graphics.SetTextScale(countSettings.TextScale.Value))
        {
            for (var i = matches.Count - 1; i >= 0; i--)
            {
                var match = matches[i];
                var rule = match.Rule;

                if (!rule.ShowModCount || match.ModCount < rule.ModCountMinimum.Value)
                    continue;

                if (!_numberedIds.Add(match.EntityId))
                    continue;

                var placement = _placements[i];

                if (!placement.OnMap && !placement.InWorld)
                    continue;

                var text = match.ModCount.ToString();
                var size = _graphics.MeasureText(text);
                var tint = rule.ModCountColor.Value;

                if (placement.OnMap)
                {
                    var at = placement.MapPoint + countSettings.OffsetFor(rule.IconSize.Value, size);
                    DrawNumber(text, size, at, region, tint, countSettings);
                }

                if (alsoWorld && placement.InWorld)
                {
                    var at = placement.ScreenPoint + countSettings.OffsetFor(rule.WorldIconSize.Value, size);
                    DrawNumber(text, size, at, region, tint, countSettings);
                }
            }
        }
    }

    private void DrawNumber(
        string text, Vector2 size, Vector2 at, RectangleF region, Color tint, ModCountSettings settings)
    {
        if (!region.Contains(at.X, at.Y))
            return;

        var pos = new Vector2(at.X - size.X / 2f, at.Y - size.Y / 2f);

        if (settings.DrawBackground)
        {
            _graphics.DrawBox(
                new RectangleF(pos.X - 2f, pos.Y - 1f, size.X + 4f, size.Y + 2f),
                settings.BackgroundColor.Value);
        }

        // Eight offset copies underneath, which reads over any terrain without covering the map the way a filled box
        // does. Diagonals included: without them the corners of each glyph stay bare and the number still disappears
        // against a busy background.
        if (settings.DrawOutline)
        {
            var outline = settings.OutlineColor.Value;

            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx != 0 || dy != 0)
                    _graphics.DrawText(text, new Vector2(pos.X + dx, pos.Y + dy), outline);
            }
        }

        _graphics.DrawText(text, pos, tint);
    }
}
