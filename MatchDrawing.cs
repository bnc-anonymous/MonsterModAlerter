using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Helpers;
using Color = SharpDX.Color;
using RectangleF = SharpDX.RectangleF;

namespace MonsterModAlerter;

/// <summary>
/// Draws the scan's matches and remembered positions: icons, boxes, labels and counts in the world, then icons and
/// counts on whichever map is up, limited to that map's area.
/// </summary>
internal sealed class MatchDrawing
{
    /// <summary>The HUD's shared icon atlas, the one MinimapIcons draws from.</summary>
    private const string IconAtlasTexture = "Icons.png";

    private readonly Graphics _graphics;

    /// <summary>One per match, in the same order, refilled every frame by ReadPlacements.</summary>
    private readonly List<Placement> _placements = new();

    /// <summary>Entities that already have a count in the pass being drawn; refilled by DrawCounts.</summary>
    private readonly HashSet<uint> _numberedIds = new();

    /// <summary>
    /// How long the corner map's rectangle is kept before it is read again. It changes only with the resolution, the
    /// window size or the game's UI scale, so a read every 2 s catches those without a restart.
    /// </summary>
    private const long CornerMapRefreshMs = 2000;

    private readonly Stopwatch _sinceCornerMapRead = new();
    private RectangleF _cornerMap;

    /// <summary>
    /// Where one match is drawn this frame. Read once per match, so the icon and count passes agree and neither reads
    /// the game. The default, for an entity that is gone, dead or unreadable, draws nothing.
    /// </summary>
    /// <param name="OnMap">Drawn on the map that is up, at MapPoint.</param>
    /// <param name="InWorld">Drawn in the world, at ScreenPoint.</param>
    private readonly record struct Placement(bool OnMap, Vector2 MapPoint, bool InWorld, Vector2 ScreenPoint);

    public MatchDrawing(Graphics graphics) => _graphics = graphics;

    /// <summary>
    /// Full window, minus whatever the open side panels cover when Prevent drawing over panels is on. Anything drawn
    /// outside this would appear on top of the panel. The inventory and stash only cover part of the screen, so rather
    /// than hiding everything the drawable area shrinks to the part they leave uncovered, the way Radar does.
    /// </summary>
    public static RectangleF DrawRegionOf(
        GameController gameController, IngameUIElements ingameUi, bool preventDrawingOverPanels)
    {
        var region = gameController.Window.GetWindowRectangleTimeCache with { Location = SharpDX.Vector2.Zero };

        if (!preventDrawingOverPanels)
            return region;

        if (ingameUi.OpenRightPanel?.IsVisible == true)
            region.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;

        if (ingameUi.OpenLeftPanel?.IsVisible == true)
            region.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;

        return region;
    }

    /// <summary>
    /// The part of the screen map drawing is limited to this frame, or null when no map is drawn on. The large map
    /// covers the screen, so it gets the draw region; its own rectangle reads 0x0 while open (AutoExpedition PoE1's
    /// Minimap.Surface). The corner map is ExileCore's SmallMiniMap, whose rectangle is its square (CornerMapOf), taken
    /// within the draw region; drawn on only with Enable drawing on the corner map on. A corner map that reports no
    /// area falls back to the draw region, since a clip of no area would draw nothing at all.
    /// </summary>
    public RectangleF? MapAreaOf(IngameUIElements ingameUi, RectangleF region, bool drawOnCornerMap)
    {
        if (Safe.Read(ingameUi, static ui => ui.Map.LargeMap.IsVisibleLocal, false))
            return region;

        if (!drawOnCornerMap || !Safe.Read(ingameUi, static ui => ui.Map.SmallMiniMap.IsVisibleLocal, false))
            return null;

        var cornerMap = CornerMapOf(ingameUi);

        if (cornerMap.Width <= 0f || cornerMap.Height <= 0f)
            return region;

        var left = Math.Max(cornerMap.Left, region.Left);
        var top = Math.Max(cornerMap.Top, region.Top);
        var right = Math.Min(cornerMap.Right, region.Right);
        var bottom = Math.Min(cornerMap.Bottom, region.Bottom);

        // Covered entirely by an open side panel.
        if (right <= left || bottom <= top)
            return null;

        return new RectangleF(left, top, right - left, bottom - top);
    }

    /// <summary>
    /// The corner map's rectangle, read again when it is older than CornerMapRefreshMs, after ForgetCornerMap, or when
    /// the last read held no area, as during a load. Its visibility is still read every frame by MapAreaOf.
    /// </summary>
    private RectangleF CornerMapOf(IngameUIElements ingameUi)
    {
        if (_cornerMap.Width <= 0f || _cornerMap.Height <= 0f ||
            _sinceCornerMapRead.ElapsedMilliseconds >= CornerMapRefreshMs)
        {
            _cornerMap = Safe.Read(ingameUi, static ui => ui.Map.SmallMiniMap.GetClientRectCache, default(RectangleF));
            _sinceCornerMapRead.Restart();
        }

        return _cornerMap;
    }

    /// <summary>The corner map's rectangle as last read, for the debug overlay; empty before the first read.</summary>
    public RectangleF CornerMap => _cornerMap;

    /// <summary>Makes the next MapAreaOf read the corner map's rectangle again. Called on area change.</summary>
    public void ForgetCornerMap() => _cornerMap = default;

    /// <param name="mapArea">From MapAreaOf: where map drawing may go, or null to draw on no map.</param>
    public void Draw(
        GameController gameController, MonsterModAlerterSettings settings, List<RuleMatch> matches,
        List<RememberedPosition> outOfRange, RectangleF region, RectangleF? mapArea)
    {
        ReadPlacements(gameController, matches, mapArea != null, settings.World.Enable);

        // The world first: the game draws the large map over the world, so its icons go over the world's here too.
        for (var i = 0; i < matches.Count; i++)
        {
            if (_placements[i].InWorld)
                DrawInWorld(settings.World, _placements[i].ScreenPoint, matches[i], region);
        }

        if (settings.ModCount.DrawInWorld)
            DrawCounts(matches, settings.ModCount, onMap: false, region);

        if (mapArea is not { } area)
            return;

        // Clipped to the map's area, text included, so nothing spills past the corner map's square: an icon or number
        // over its edge is cut there. Whatever lies wholly outside is skipped as well.
        using (_graphics.BeginRectClip(area, true))
        {
            // Remembered positions first, so a live entity's icon always sits on top of a remembered one.
            DrawRememberedPositions(outOfRange, area, settings.ModCount);

            for (var i = 0; i < matches.Count; i++)
            {
                var placement = _placements[i];
                var iconSize = matches[i].Rule.MapIconSize.Value;

                if (placement.OnMap && Overlaps(area, placement.MapPoint, new Vector2(iconSize)))
                    DrawIcon(placement.MapPoint, iconSize, matches[i].Rule);
            }

            // After every icon, so a number is never buried under one.
            DrawCounts(matches, settings.ModCount, onMap: true, area);
        }
    }

    private void ReadPlacements(
        GameController gameController, List<RuleMatch> matches, bool mapDrawn, bool worldEnabled)
    {
        _placements.Clear();

        foreach (var match in matches)
        {
            var state = (Drawing: this, GameController: gameController, Match: match,
                OnMap: mapDrawn && match.Rule.ShowOnMap, InWorld: worldEnabled && match.Rule.ShowInWorld);

            _placements.Add(Safe.Read(state,
                static s => s.Drawing.PlacementOf(s.GameController, s.Match, s.OnMap, s.InWorld), default));
        }
    }

    // GridToMap handles the corner map and the open large map, including camera rotation and per-cell terrain height.
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

    /// <summary>Whether a box of this size centred on this point reaches into the area at all.</summary>
    private static bool Overlaps(RectangleF area, Vector2 centre, Vector2 size) =>
        centre.X + size.X / 2f > area.Left && centre.X - size.X / 2f < area.Right &&
        centre.Y + size.Y / 2f > area.Top && centre.Y - size.Y / 2f < area.Bottom;

    private void DrawInWorld(WorldSettings world, Vector2 screenPos, RuleMatch match, RectangleF region)
    {
        if (!region.Contains(screenPos.X, screenPos.Y))
            return;

        var worldIconSize = match.Rule.WorldIconSize.Value;

        // The box frames the entity, not the icon, so it is drawn once no matter how many rules matched - several
        // identical boxes around one entity would just thicken the outline.
        if (world.DrawBox && match.Slot == 0)
        {
            var half = world.BoxSize.Value / 2f;
            var boxColour = world.UseRuleTintForBox ? match.Rule.Tint.Value : world.BoxColour.Value;
            _graphics.DrawFrame(
                new RectangleF(screenPos.X - half, screenPos.Y - half, world.BoxSize.Value, world.BoxSize.Value),
                boxColour,
                world.BoxThickness.Value);
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
            _graphics.DrawText(text, new Vector2(screenPos.X - textSize.X / 2f, y), world.LabelColour.Value);
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
        List<RememberedPosition> stale, RectangleF area, ModCountSettings countSettings)
    {
        foreach (var entry in stale)
        {
            var rule = entry.Rule;
            if (!rule.ShowOnMap)
                continue;

            var mapPoint = Safe.Read((Graphics: _graphics, entry.GridPos),
                static s => (Vector2?)s.Graphics.GridToMap(s.GridPos, s.GridPos), null);
            var iconSize = rule.MapIconSize.Value;

            if (mapPoint is not { } point || !Overlaps(area, point, new Vector2(iconSize)))
                continue;

            DrawIcon(point, iconSize, rule);

            if (!rule.ShowModCount || entry.ModCount < rule.ModCountMinimum.Value)
                continue;

            // The count is the whole point of remembering these - it is what the kill order is decided on - so it is
            // drawn alongside rather than dropped. Rare affixes are fixed at spawn, so the remembered number stays
            // accurate however long ago it was read.
            using (_graphics.SetTextScale(countSettings.TextScale.Value))
            {
                var text = entry.ModCount.ToString();
                var size = _graphics.MeasureText(text);
                var at = point + countSettings.OffsetFor(iconSize, size);

                if (Overlaps(area, at, size))
                    DrawNumber(text, size, at, rule.ModCountColour.Value, countSettings);
            }
        }
    }

    /// <summary>
    /// Draws each entity's modifier count as a bare number, e.g. "12", on the map or in the world. Centred on the
    /// entity by default so it reads as belonging to that icon rather than to a neighbouring one.
    /// </summary>
    /// <param name="area">The map area for the map, or the draw region for the world.</param>
    private void DrawCounts(List<RuleMatch> matches, ModCountSettings countSettings, bool onMap, RectangleF area)
    {
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

                if (onMap ? !placement.OnMap : !placement.InWorld)
                    continue;

                var text = match.ModCount.ToString();
                var size = _graphics.MeasureText(text);
                var at = onMap
                    ? placement.MapPoint + countSettings.OffsetFor(rule.MapIconSize.Value, size)
                    : placement.ScreenPoint + countSettings.OffsetFor(rule.WorldIconSize.Value, size);

                if (onMap ? Overlaps(area, at, size) : area.Contains(at.X, at.Y))
                    DrawNumber(text, size, at, rule.ModCountColour.Value, countSettings);
            }
        }
    }

    private void DrawNumber(string text, Vector2 size, Vector2 at, Color tint, ModCountSettings settings)
    {
        var pos = new Vector2(at.X - size.X / 2f, at.Y - size.Y / 2f);

        if (settings.DrawBackground)
        {
            _graphics.DrawBox(
                new RectangleF(pos.X - 2f, pos.Y - 1f, size.X + 4f, size.Y + 2f),
                settings.BackgroundColour.Value);
        }

        // Eight offset copies underneath, which reads over any terrain without covering the map the way a filled box
        // does. Diagonals included: without them the corners of each glyph stay bare and the number still disappears
        // against a busy background.
        if (settings.DrawOutline)
        {
            var outline = settings.OutlineColour.Value;

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
