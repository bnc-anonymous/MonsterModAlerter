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
///
/// Everything is drawn in ascending priority, an entity's number with its icons: after the entity's highest-priority
/// match, so the number sits over its own icons and under a higher-priority entity's icon and number, never over them.
/// </summary>
internal sealed class MatchDrawing
{
    /// <summary>The HUD's shared icon atlas, the one MinimapIcons draws from.</summary>
    private const string IconAtlasTexture = "Icons.png";

    private readonly Graphics _graphics;

    /// <summary>One per match, in the same order, refilled every frame by ReadPlacements.</summary>
    private readonly List<Placement> _placements = new();

    /// <summary>Each entity's highest-priority match, by index: where its number goes in the drawing order.</summary>
    private readonly Dictionary<uint, int> _lastMatchOfEntity = new();

    /// <summary>The match whose number each entity shows on the map, by index; refilled by FindNumbers.</summary>
    private readonly Dictionary<uint, int> _numberOnMap = new();

    /// <summary>The match whose number each entity shows in the world, by index; refilled by FindNumbers.</summary>
    private readonly Dictionary<uint, int> _numberInWorld = new();

    /// <summary>
    /// How long the corner map's rectangle is kept before it is read again. It changes only with the resolution, the
    /// window size or the game's UI scale, so a read every 2 s catches those without a restart.
    /// </summary>
    private const long CornerMapRefreshMs = 2000;

    private readonly Stopwatch _sinceCornerMapRead = new();
    private RectangleF _cornerMap;

    /// <summary>
    /// Entities whose live match stands in for their remembered position this frame; refilled by Draw.
    /// </summary>
    private readonly HashSet<uint> _shownLiveIds = new();

    private readonly List<RememberedPosition> _rememberedDrawn = new();

    /// <summary>
    /// Where one match is drawn this frame. Read once per match, so the icon and count passes agree and neither reads
    /// the game. The default, for a dead entity, draws nothing; so does GoneOrUnread, which lets the entity's
    /// remembered position stand in.
    /// </summary>
    /// <param name="OnMap">Drawn on the map that is up, at MapPoint.</param>
    /// <param name="InWorld">Drawn in the world, at ScreenPoint.</param>
    /// <param name="Gone">
    /// The entity is no longer valid - it has left the entity list - or could not be read, so its remembered position,
    /// if it has one, is drawn in its place until the next scan. False for a dead entity, which is valid and simply not
    /// drawn.
    /// </param>
    private readonly record struct Placement(
        bool OnMap, Vector2 MapPoint, bool InWorld, Vector2 ScreenPoint, bool Gone = false)
    {
        public static readonly Placement GoneOrUnread = new(false, default, false, default, Gone: true);
    }

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

    /// <summary>The remembered positions the last frame drew, for the debug overlay.</summary>
    public List<RememberedPosition> RememberedDrawn => _rememberedDrawn;

    /// <summary>Makes the next MapAreaOf read the corner map's rectangle again. Called on area change.</summary>
    public void ForgetCornerMap() => _cornerMap = default;

    /// <param name="remembered">
    /// The scan's remembered positions, live entities' included (EntityScan.Remembered).
    /// </param>
    /// <param name="mapArea">From MapAreaOf: where map drawing may go, or null to draw on no map.</param>
    public void Draw(
        GameController gameController, MonsterModAlerterSettings settings, List<RuleMatch> matches,
        List<RememberedPosition> remembered, RectangleF region, RectangleF? mapArea)
    {
        _rememberedDrawn.Clear();
        ReadPlacements(gameController, matches, mapArea != null);
        FindNumbers(matches);

        // The world first: the game draws the large map over the world, so its icons go over the world's here too.
        for (var i = 0; i < matches.Count; i++)
        {
            if (_placements[i].InWorld)
                DrawInWorld(_placements[i].ScreenPoint, matches[i], region);

            DrawNumberAfter(i, matches, onMap: false, region);
        }

        if (mapArea is not { } area)
            return;

        // Clipped to the map's area, text included, so nothing spills past the corner map's square: an icon or number
        // over its edge is cut there. Whatever lies wholly outside is skipped as well.
        using (_graphics.BeginRectClip(area, true))
        {
            // An entity matched live and still valid shows live, dead or not; one that has left the entity list since
            // the scan shows at its remembered position, so nothing blinks out until the next scan catches up.
            _shownLiveIds.Clear();

            for (var i = 0; i < matches.Count; i++)
            {
                if (!_placements[i].Gone)
                    _shownLiveIds.Add(matches[i].EntityId);
            }

            // Remembered positions first, so a live entity's icon always sits on top of a remembered one.
            DrawRememberedPositions(remembered, area);

            for (var i = 0; i < matches.Count; i++)
            {
                var placement = _placements[i];
                var iconSize = matches[i].Rule.MapIconSize.Value;

                if (placement.OnMap && Overlaps(area, placement.MapPoint, new Vector2(iconSize)))
                    DrawIcon(placement.MapPoint, iconSize, matches[i].Rule);

                DrawNumberAfter(i, matches, onMap: true, area);
            }
        }
    }

    private void ReadPlacements(GameController gameController, List<RuleMatch> matches, bool mapDrawn)
    {
        _placements.Clear();

        foreach (var match in matches)
        {
            var state = (Drawing: this, GameController: gameController, Match: match,
                OnMap: mapDrawn && match.Rule.ShowOnMap, InWorld: match.Rule.ShowInWorld.Value);

            _placements.Add(Safe.Read(state,
                static s => s.Drawing.PlacementOf(s.GameController, s.Match, s.OnMap, s.InWorld),
                Placement.GoneOrUnread));
        }
    }

    // GridToMap handles the corner map and the open large map, including camera rotation and per-cell terrain height.
    private Placement PlacementOf(GameController gameController, RuleMatch match, bool onMap, bool inWorld)
    {
        var entity = match.Entity;

        if (!entity.IsValid)
            return Placement.GoneOrUnread;

        if (match.CheckAlive && !entity.IsAlive)
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

    /// <summary>
    /// The rule's world icon, box and label, each as the rule sets it. Matches are drawn in ascending priority, so the
    /// highest-priority rule's box sits on top where several rules box the same monster.
    /// </summary>
    private void DrawInWorld(Vector2 screenPos, RuleMatch match, RectangleF region)
    {
        if (!region.Contains(screenPos.X, screenPos.Y))
            return;

        var rule = match.Rule;
        var worldIconSize = rule.WorldIconSize.Value;

        if (rule.DrawBox)
        {
            var boxSize = rule.BoxSize.Value;
            var half = boxSize / 2f;
            _graphics.DrawFrame(new RectangleF(screenPos.X - half, screenPos.Y - half, boxSize, boxSize),
                rule.BoxColour.Value, rule.BoxThickness.Value);
        }

        if (rule.DrawWorldIcon)
            DrawIcon(screenPos, worldIconSize, rule);

        if (!rule.DrawLabel)
            return;

        // Sits below whichever of the two is taller, so it never overlaps either.
        var halfExtent = Math.Max(
            rule.DrawBox ? rule.BoxSize.Value : 0,
            rule.DrawWorldIcon ? worldIconSize : 0) / 2f;

        const float labelGap = 2f;

        var text = string.IsNullOrWhiteSpace(rule.Label?.Value) ? match.MatchedName : rule.Label.Value;
        using (_graphics.SetTextScale(rule.LabelScale.Value))
        {
            var textSize = _graphics.MeasureText(text);

            // Stacked rather than side by side: labels are far wider than icons, so a row of them would run into each
            // other and into the neighbouring entities'.
            var y = screenPos.Y + halfExtent + labelGap + match.Slot * (textSize.Y + labelGap);
            _graphics.DrawText(text, new Vector2(screenPos.X - textSize.X / 2f, y), rule.LabelColour.Value);
        }
    }

    private void DrawIcon(Vector2 center, int size, AlertRule rule)
    {
        var half = size / 2f;
        var rect = new RectangleF(center.X - half, center.Y - half, size, size);
        _graphics.DrawImage(IconAtlasTexture, rect, SpriteHelper.GetUV(rule.Icon), rule.Tint.Value);
    }

    /// <summary>
    /// Last known positions of entities not shown live this frame. Drawn from a stored grid position, never from the
    /// entity, which is out of the entity list or about to be.
    /// </summary>
    private void DrawRememberedPositions(List<RememberedPosition> remembered, RectangleF area)
    {
        foreach (var entry in remembered)
        {
            var rule = entry.Rule;
            if (!rule.ShowOnMap || _shownLiveIds.Contains(entry.Id))
                continue;

            var mapPoint = Safe.Read((Graphics: _graphics, entry.GridPos),
                static s => (Vector2?)s.Graphics.GridToMap(s.GridPos, s.GridPos), null);
            var iconSize = rule.MapIconSize.Value;

            if (mapPoint is not { } point || !Overlaps(area, point, new Vector2(iconSize)))
                continue;

            DrawIcon(point, iconSize, rule);
            _rememberedDrawn.Add(entry);

            if (!rule.ShowModCount || entry.ModCount < rule.ModCountMinimum.Value)
                continue;

            // The count is the whole point of remembering these - it is what the kill order is decided on - so it is
            // drawn alongside rather than dropped. Rare affixes are fixed at spawn, so the remembered number stays
            // accurate however long ago it was read.
            var text = entry.ModCount.ToString();
            var size = _graphics.MeasureText(text);
            var at = point + CountOffsetOf(rule);

            if (Overlaps(area, at, size))
                DrawNumber(text, size, at, rule.ModCountColour.Value);
        }
    }

    /// <summary>
    /// Works out, once a frame, each entity's highest-priority match and which match's number it shows: one number per
    /// entity, however many of its rules ask for one, since two would land on the same pixels. The highest-priority
    /// rule that shows a number gives it, on the map; in the world, the highest whose rule also draws it there, so a
    /// lower-priority rule that asks still gets its number in the world.
    /// </summary>
    private void FindNumbers(List<RuleMatch> matches)
    {
        _lastMatchOfEntity.Clear();
        _numberOnMap.Clear();
        _numberInWorld.Clear();

        // Ascending priority, so a later match overwrites a lower-priority one.
        for (var i = 0; i < matches.Count; i++)
        {
            var match = matches[i];
            var rule = match.Rule;
            _lastMatchOfEntity[match.EntityId] = i;

            if (!rule.ShowModCount || match.ModCount < rule.ModCountMinimum.Value)
                continue;

            _numberOnMap[match.EntityId] = i;

            if (rule.DrawModCountInWorld)
                _numberInWorld[match.EntityId] = i;
        }
    }

    /// <summary>
    /// Draws the entity's number, as a bare number such as "12", when match i is the entity's highest-priority match,
    /// the point in the drawing order its number belongs. Drawn where the numbering match is placed, in that match's
    /// rule's colour and offset.
    /// </summary>
    /// <param name="area">The map area for the map, or the draw region for the world.</param>
    private void DrawNumberAfter(int i, List<RuleMatch> matches, bool onMap, RectangleF area)
    {
        var id = matches[i].EntityId;

        if (_lastMatchOfEntity[id] != i || !(onMap ? _numberOnMap : _numberInWorld).TryGetValue(id, out var n))
            return;

        var match = matches[n];
        var placement = _placements[n];

        if (onMap ? !placement.OnMap : !placement.InWorld)
            return;

        var text = match.ModCount.ToString();
        var size = _graphics.MeasureText(text);
        var at = (onMap ? placement.MapPoint : placement.ScreenPoint) + CountOffsetOf(match.Rule);

        if (onMap ? Overlaps(area, at, size) : area.Contains(at.X, at.Y))
            DrawNumber(text, size, at, match.Rule.ModCountColour.Value);
    }

    /// <summary>Where a rule's number goes, from the icon's centre: its Mod count X offset and Y offset.</summary>
    private static Vector2 CountOffsetOf(AlertRule rule) =>
        new(rule.ModCountOffsetX.Value, rule.ModCountOffsetY.Value);

    /// <summary>The number centred on a point, over a black outline.</summary>
    private void DrawNumber(string text, Vector2 size, Vector2 at, Color tint)
    {
        var pos = new Vector2(at.X - size.X / 2f, at.Y - size.Y / 2f);

        // Eight offset copies underneath, which reads over any terrain without covering the map the way a filled box
        // does. Diagonals included: without them the corners of each glyph stay bare and the number still disappears
        // against a busy background.
        for (var dx = -1; dx <= 1; dx++)
        for (var dy = -1; dy <= 1; dy++)
        {
            if (dx != 0 || dy != 0)
                _graphics.DrawText(text, new Vector2(pos.X + dx, pos.Y + dy), Color.Black);
        }

        _graphics.DrawText(text, pos, tint);
    }
}
