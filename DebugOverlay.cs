using System;
using System.Collections.Generic;
using System.Numerics;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using Color = SharpDX.Color;
using RectangleF = SharpDX.RectangleF;

namespace MonsterModAlerter;

/// <summary>
/// The debug overlay: what the scan and the draw path decided this frame, written top-left, so an invisible icon can be
/// told apart from a wrongly-sized one without guessing. The map area map drawing is limited to is framed in cyan.
/// </summary>
internal sealed class DebugOverlay
{
    private const float Left = 20f;
    private const float Top = 120f;
    private const float LineHeight = 18f;

    private readonly Graphics _graphics;
    private readonly string _pluginName;
    private float _lineY;

    public DebugOverlay(Graphics graphics, string pluginName)
    {
        _graphics = graphics;
        _pluginName = pluginName;
    }

    public void Draw(
        GameController gameController, MonsterModAlerterSettings settings, EntityScan scan, SeenTally tally,
        List<RuleMatch> matches, RectangleF region, IngameUIElements ingameUi, RectangleF? mapArea,
        MatchDrawing matchDrawing)
    {
        _lineY = Top;

        if (mapArea is { } framed)
            _graphics.DrawFrame(framed, Color.Cyan, 1);

        var rememberedDrawn = matchDrawing.RememberedDrawn;
        var cornerMap = matchDrawing.CornerMap;

        WriteLine($"[{_pluginName}] matches={matches.Count}  rules={settings.Rules.Content.Count}  " +
                  $"remembered={scan.RememberedCount} (drawn this frame={rememberedDrawn.Count})", Color.White);

        // The second walk is the one that is easy to be wrong about: entities=0 says no rule asked for anything past
        // the monster list, which is the first thing to check when a critter rule draws nothing at all.
        WriteLine($"  any-entity walk: entities={scan.AnyEntityWalkCount}  matches outside the monster gates=" +
                  $"{scan.AnyEntityMatchCount}  seen this map={tally.KindCount} kinds",
            scan.AnyEntityWalkCount > 0 ? Color.LightGreen : Color.White);

        // The nearest remembered position and whether it is inside the forget radius: if an icon is lingering, this
        // says whether the player is simply not close enough yet to disprove it.
        if (rememberedDrawn.Count > 0 && gameController?.Player != null)
        {
            var here = gameController.Player.GridPosNum;
            var nearest = float.MaxValue;

            foreach (var entry in rememberedDrawn)
            {
                var dx = entry.GridPos.X - here.X;
                var dy = entry.GridPos.Y - here.Y;
                nearest = Math.Min(nearest, (float)Math.Sqrt(dx * dx + dy * dy));
            }

            const int radius = RememberedPositions.ForgetRadius;
            WriteLine($"  nearest stale icon {nearest:0} grid away, forget radius {radius} - " +
                      (nearest <= radius ? "inside, so it would have been dropped" : "walk closer to clear it"),
                nearest <= radius ? Color.Orange : Color.White);
        }

        WriteLine($"cornerMap={ingameUi.Map.SmallMiniMap.IsVisibleLocal} " +
                  $"({cornerMap.X:0},{cornerMap.Y:0} {cornerMap.Width:0}x{cornerMap.Height:0})  " +
                  $"largeMap={ingameUi.Map.LargeMap.IsVisibleLocal}  " +
                  $"cornerMapDrawing={settings.Display.EnableDrawingOnCornerMap.Value}  " +
                  (mapArea is { } area
                      ? $"mapArea=({area.X:0},{area.Y:0} {area.Width:0}x{area.Height:0})"
                      : "mapArea=none"),
            mapArea != null ? Color.LightGreen : Color.Orange);
        WriteLine($"region=({region.Left:0},{region.Top:0})-({region.Right:0},{region.Bottom:0})  " +
                  $"preventDrawingOverPanels={settings.Display.PreventDrawingOverPanels.Value}", Color.White);

        // Only for a rule that is actually counting, and only the first such match: printing every mod on every match
        // would bury the rest of the overlay.
        var firstShowingModCount = FirstMatchShowingModCount(matches);

        foreach (var match in matches)
        {
            var gridPos = match.Entity.GridPosNum;
            var point = _graphics.GridToMap(gridPos, gridPos);
            var inMapArea = mapArea?.Contains(point.X, point.Y) == true;

            WriteLine($"  '{match.Rule.SearchTerm.Value}' icon={match.Rule.Icon} " +
                      $"mapSize={match.Rule.MapIconSize.Value} " +
                      $"worldSize={match.Rule.WorldIconSize.Value} showMap={match.Rule.ShowOnMap.Value} " +
                      $"mapPos=({point.X:0},{point.Y:0}) inMapArea={inMapArea}",
                inMapArea && match.Rule.ShowOnMap ? Color.LightGreen : Color.Orange);

            if (ReferenceEquals(match, firstShowingModCount))
                WriteModBreakdown(match);
        }
    }

    private void WriteLine(string text, Color color)
    {
        _graphics.DrawText(text, new Vector2(Left, _lineY), color);
        _lineY += LineHeight;
    }

    private static RuleMatch FirstMatchShowingModCount(List<RuleMatch> matches)
    {
        foreach (var match in matches)
        {
            if (match.Rule.ShowModCount)
                return match;
        }

        return null;
    }

    /// <summary>
    /// Lists the entity's raw mod entries, marking which ones the count includes. This is how a number that disagrees
    /// with the monster's tooltip gets diagnosed rather than guessed at.
    /// </summary>
    private void WriteModBreakdown(RuleMatch match)
    {
        if (!match.Entity.TryGetComponent<ObjectMagicProperties>(out var props) || props?.Mods is not { } mods)
        {
            WriteLine("    no ObjectMagicProperties.Mods on this monster", Color.Orange);
            return;
        }

        WriteLine($"    counted {match.ModCount} of {mods.Count} entries " +
                  $"(prefix '{RuleMatcher.RareModPrefix}', duplicates ignored)", Color.White);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            if (string.IsNullOrEmpty(mod))
                continue;

            // A duplicate and a non-affix are excluded for different reasons, so they are marked differently -
            // otherwise a wrong count sends the reader looking at the wrong thing.
            if (!mod.StartsWith(RuleMatcher.RareModPrefix, StringComparison.OrdinalIgnoreCase))
                WriteLine($"        {mod}", Color.Gray);
            else if (seen.Add(mod))
                WriteLine($"      + {mod}", Color.LightGreen);
            else
                WriteLine($"      = {mod}  (duplicate)", Color.Orange);
        }
    }
}
