using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
using ExileCore.Shared.Helpers;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Color = SharpDX.Color;
using RectangleF = SharpDX.RectangleF;
using Vector2 = System.Numerics.Vector2;

namespace MonsterModAlerter;

public class MonsterModAlerter : BaseSettingsPlugin<MonsterModAlerterSettings>
{
    /// <summary>
    /// A monster matching a rule, plus the mod/buff text that triggered it and how many modifiers
    /// it carries. The count is read during the scan alongside the mods it is counting, so Render
    /// never touches a component.
    /// </summary>
    private sealed record Match(Entity Entity, AlertRule Rule, string MatchedName, int ModCount);

    /// <summary>
    /// Prefix ObjectMagicProperties.Mods uses for a rolled rare-monster affix, e.g.
    /// MonsterModEnrage. Only these are counted — see FindMatch.
    /// </summary>
    private const string RareModPrefix = "MonsterMod";

    // Scan() builds a fresh list and swaps it in; Render() reads the reference once. The swap is
    // atomic, so the two never touch the same list even if Tick runs off the render thread.
    private volatile List<Match> _matches = new();
    private readonly HashSet<uint> _alertedEntityIds = new();

    private readonly Stopwatch _scanTimer = Stopwatch.StartNew();
    private readonly Stopwatch _soundTimer = Stopwatch.StartNew();

    public MonsterModAlerter()
    {
        Name = "Monster Mod Alerter";
    }

    public override bool Initialise()
    {
        // ExileCore sorts plugins by Order ascending and renders in that sequence, so a high value
        // draws on top of other plugins. The sort runs once, after every plugin has been initialised,
        // so assigning here applies this session — but later edits need a HUD restart.
        Order = Settings.RenderOrder.Value;

        // First run (or after deleting every rule) starts you with a usable example.
        if (Settings.Rules.Content.Count == 0)
            Settings.Rules.Content.Add(AlertRule.Default());

        Settings.ForceRefresh.OnPressed = ForceRefresh;

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _matches = new List<Match>();
        _alertedEntityIds.Clear();
    }

    /// <summary>
    /// Drops the alerted-monster list and forces the next Tick to re-scan instead of waiting out
    /// the scan interval. Drawing settings are read per-frame, so this is only needed to re-arm
    /// sound alerts or to apply a rule change without waiting.
    /// </summary>
    private void ForceRefresh()
    {
        _alertedEntityIds.Clear();
        _matches = new List<Match>();
        _scanTimer.Restart();

        // _soundTimer is deliberately left alone: restarting it would force a full cooldown wait,
        // which is the opposite of re-arming the alert.
        if (GameController.InGame)
            Scan();
    }

    public override Job Tick()
    {
        // Handled before the Enable check so a reset still applies while the plugin is switched off.
        if (Settings.ResetRequested)
        {
            Settings.ResetRequested = false;
            Settings.ResetToDefaults();
            ForceRefresh();
            LogMessage($"{Name}: all rules deleted and settings restored to defaults.");
        }

        if (!Settings.Enable || !GameController.InGame)
            return null;

        if (_scanTimer.ElapsedMilliseconds < Settings.ScanIntervalMs)
            return null;

        _scanTimer.Restart();
        Scan();
        TryPlayAlerts();
        return null;
    }

    private void Scan()
    {
        // A rule needs something to go on, but either half will do: a mod term, or a monster
        // filter naming (or excluding) a family. A rule with neither would match every monster.
        var rules = Settings.Rules?.Content?
            .Where(r => r is { Enabled.Value: true } &&
                        (!string.IsNullOrWhiteSpace(r.SearchTerm?.Value) || r.Metadata?.IsBlank == false))
            .ToArray() ?? [];

        if (rules.Length == 0)
        {
            _matches = new List<Match>();
            return;
        }

        var found = new List<Match>();

        // Reused across entities within this scan; local so a Refresh Now from the UI thread
        // cannot collide with a scan in progress.
        var values = new List<string>(16);

        var maxDistance = Settings.MaxDistance.Value;
        var checkMods = Settings.CheckMods;
        var checkBuffs = Settings.CheckBuffs;

        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (entity == null || !entity.IsAlive || !entity.IsHostile)
                continue;

            if (maxDistance > 0 && entity.DistancePlayer > maxDistance)
                continue;

            // Global filter — the cheap gate that applies to every alert rule.
            if (!Settings.Rarity.Allows(entity.Rarity))
                continue;

            var match = FindMatch(entity, rules, checkMods, checkBuffs, values);
            if (match != null)
                found.Add(match);
        }

        // Ascending priority so the highest-priority icon is drawn last, i.e. on top.
        found.Sort((a, b) => a.Rule.Priority.Value.CompareTo(b.Rule.Priority.Value));
        _matches = found;
    }

    /// <summary>
    /// Returns the highest-priority rule matching this monster, so one monster yields one icon
    /// rather than a stack of overlapping ones. A rule matches only when every one of its
    /// comma-separated terms is found somewhere on the monster.
    /// </summary>
    private static Match FindMatch(Entity entity, AlertRule[] rules, bool checkMods, bool checkBuffs, List<string> values)
    {
        // Gather the monster's mod and buff names once, then test all rules against that set —
        // cheaper than re-reading components per rule, and it lets terms match across both sources.
        values.Clear();

        // Counted here rather than in a second pass, so the number comes from the same read as the
        // matching. Modifiers only: buffs come and go with auras and charges, and folding them in
        // would make the number flicker as you moved.
        var modCount = 0;

        if (entity.TryGetComponent<ObjectMagicProperties>(out var magicProps) && magicProps?.Mods is { } mods)
        {
            foreach (var mod in mods)
            {
                if (string.IsNullOrEmpty(mod))
                    continue;

                // Only the rare-monster modifiers proper. The list also carries entries that are
                // not rolled affixes at all, and counting those inflates the number past what the
                // monster's own tooltip shows.
                if (mod.StartsWith(RareModPrefix, StringComparison.OrdinalIgnoreCase))
                    modCount++;

                // Match Mods only governs what terms are tested against, never the count — searching
                // stays over every entry, since a term may well name one of the others.
                if (checkMods)
                    values.Add(mod);
            }
        }

        if (checkBuffs && entity.TryGetComponent<Buffs>(out var buffs) && buffs?.BuffsList is { } buffList)
        {
            foreach (var buff in buffList)
                if (!string.IsNullOrEmpty(buff?.Name))
                    values.Add(buff.Name);
        }

        // No early-out on an empty value list: a rule can now match on the monster filter alone,
        // and such a rule must still fire for a monster carrying no mods or buffs at all.
        Match best = null;
        var path = entity.Path;

        foreach (var rule in rules)
        {
            // Per-rule rarity intersects with the global filter, so a rule can only narrow it.
            if (!rule.Rarity.Allows(entity.Rarity))
                continue;

            // Already holding something this rule cannot outrank.
            if (best != null && rule.Priority.Value <= best.Rule.Priority.Value)
                continue;

            if (rule.Metadata?.Matches(path) == false)
                continue;

            var terms = rule.Terms;

            // No term means the monster filter alone decides, so what to label it with is the
            // filter itself rather than a mod name.
            if (terms.Length == 0)
            {
                if (rule.Metadata?.IsBlank != false)
                    continue;

                best = new Match(entity, rule, rule.Metadata.Describe(), modCount);
                continue;
            }

            string display = null;
            var allPresent = true;

            foreach (var term in terms)
            {
                var hit = FirstValueContaining(values, term);
                if (hit == null)
                {
                    allPresent = false;
                    break;
                }

                display = display == null ? hit : $"{display} + {hit}";
            }

            if (allPresent)
                best = new Match(entity, rule, display, modCount);
        }

        return best;
    }

    // OrdinalIgnoreCase avoids allocating a lowercased copy of every mod string each scan.
    private static string FirstValueContaining(List<string> values, string term)
    {
        foreach (var value in values)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
                return value;
        }

        return null;
    }

    public override void Render()
    {
        var matches = _matches;

        if (!Settings.Enable || !GameController.InGame || matches.Count == 0)
            return;

        var ingameUi = GameController.IngameState.IngameUi;

        if (!Settings.IgnoreFullscreenPanels && ingameUi.FullscreenPanels.Any(x => x.IsVisible))
            return;

        if (!Settings.IgnoreLargePanels && ingameUi.LargePanels.Any(x => x.IsVisible))
            return;

        // The inventory/stash only cover part of the screen, so rather than hiding everything we
        // shrink the drawable area to the part they leave uncovered, the way Radar does.
        var region = GetDrawRegion(ingameUi);

        var largeMapVisible = ingameUi.Map.LargeMap.IsVisibleLocal;
        var minimapVisible = Settings.DrawOnlyOnLargeMap
            ? largeMapVisible
            : largeMapVisible || ingameUi.Map.SmallMiniMap.IsVisibleLocal;

        foreach (var match in matches)
        {
            if (!match.Entity.IsValid || !match.Entity.IsAlive)
                continue;

            if (minimapVisible && match.Rule.ShowOnMinimap)
                DrawOnMinimap(match, region);

            if (Settings.World.Enable && match.Rule.ShowInWorld)
                DrawInWorld(match, region);
        }

        // A second pass, after every icon, so a number is never buried under one.
        DrawModCounts(matches, region, minimapVisible);

        if (Settings.DebugOverlay)
            DrawDebugOverlay(matches, region, ingameUi, minimapVisible);
    }

    /// <summary>
    /// Reports what the draw path actually decided this frame, so an invisible icon can be told
    /// apart from a wrongly-sized one without guessing.
    /// </summary>
    private void DrawDebugOverlay(List<Match> matches, RectangleF region, IngameUIElements ingameUi, bool minimapVisible)
    {
        var y = 120f;
        var line = 18f;

        void Write(string text, Color color)
        {
            Graphics.DrawText(text, new Vector2(20, y), color);
            y += line;
        }

        Write($"[{Name}] matches={matches.Count}  rules={Settings.Rules.Content.Count}", Color.White);
        Write($"smallMap={ingameUi.Map.SmallMiniMap.IsVisibleLocal}  largeMap={ingameUi.Map.LargeMap.IsVisibleLocal}  " +
              $"minimapVisible={minimapVisible}  onlyLarge={Settings.DrawOnlyOnLargeMap.Value}",
            minimapVisible ? Color.LightGreen : Color.Orange);
        Write($"region=({region.Left:0},{region.Top:0})-({region.Right:0},{region.Bottom:0})  limit={Settings.LimitDrawRegion.Value}", Color.White);

        foreach (var match in matches)
        {
            var gridPos = match.Entity.GridPosNum;
            var point = Graphics.GridToMap(gridPos, gridPos);
            var inRegion = region.Contains(point.X, point.Y);

            Write($"  '{match.Rule.SearchTerm.Value}' icon={match.Rule.Icon} mapSize={match.Rule.IconSize.Value} " +
                  $"worldSize={match.Rule.WorldIconSize.Value} showMap={match.Rule.ShowOnMinimap.Value} " +
                  $"mapPos=({point.X:0},{point.Y:0}) inRegion={inRegion}",
                inRegion && minimapVisible && match.Rule.ShowOnMinimap ? Color.LightGreen : Color.Orange);

            // Only for a rule that is actually counting, and only the first such monster: printing
            // every mod on every match would bury the rest of the overlay.
            if (match.Rule.ShowModCount && ReferenceEquals(match, FirstCounting(matches)))
                WriteModBreakdown(match, Write);
        }
    }

    private static Match FirstCounting(List<Match> matches)
    {
        foreach (var match in matches)
            if (match.Rule.ShowModCount)
                return match;

        return null;
    }

    /// <summary>
    /// Lists the monster's raw mod entries, marking which ones the count includes. This is how a
    /// number that disagrees with the monster's tooltip gets diagnosed rather than guessed at.
    /// </summary>
    private static void WriteModBreakdown(Match match, Action<string, Color> write)
    {
        if (!match.Entity.TryGetComponent<ObjectMagicProperties>(out var props) || props?.Mods is not { } mods)
        {
            write("    no ObjectMagicProperties.Mods on this monster", Color.Orange);
            return;
        }

        write($"    counted {match.ModCount} of {mods.Count} entries (prefix '{RareModPrefix}')", Color.White);

        foreach (var mod in mods)
        {
            if (string.IsNullOrEmpty(mod))
                continue;

            var counted = mod.StartsWith(RareModPrefix, StringComparison.OrdinalIgnoreCase);
            write($"      {(counted ? "+" : " ")} {mod}", counted ? Color.LightGreen : Color.Gray);
        }
    }

    /// <summary>
    /// Full window, minus whatever the open side panels cover. Anything drawn outside this would
    /// appear on top of the panel, which is what the region limiting exists to prevent.
    /// </summary>
    private RectangleF GetDrawRegion(IngameUIElements ingameUi)
    {
        var region = GameController.Window.GetWindowRectangleTimeCache with { Location = SharpDX.Vector2.Zero };

        if (!Settings.LimitDrawRegion)
            return region;

        if (ingameUi.OpenRightPanel?.IsVisible == true)
            region.Right = ingameUi.OpenRightPanel.GetClientRectCache.Left;

        if (ingameUi.OpenLeftPanel?.IsVisible == true)
            region.Left = ingameUi.OpenLeftPanel.GetClientRectCache.Right;

        return region;
    }

    // GridToMap handles the small minimap and the open large map, including camera rotation
    // and per-cell terrain height.
    private void DrawOnMinimap(Match match, RectangleF region)
    {
        var gridPos = match.Entity.GridPosNum;
        var point = Graphics.GridToMap(gridPos, gridPos);

        if (!region.Contains(point.X, point.Y))
            return;

        DrawIcon(point, match.Rule.IconSize.Value, match.Rule);
    }

    /// <summary>
    /// Draws each monster's modifier count as a bare number, e.g. "12". Centred on the monster so
    /// it reads as belonging to that dot rather than to a neighbouring one.
    /// </summary>
    private void DrawModCounts(List<Match> matches, RectangleF region, bool minimapVisible)
    {
        var settings = Settings.ModCount;
        var alsoWorld = settings.DrawInWorld;

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

        using (Graphics.SetTextScale(settings.TextScale.Value))
        {
            foreach (var match in matches)
            {
                var rule = match.Rule;

                if (!rule.ShowModCount || match.ModCount < rule.ModCountMinimum.Value)
                    continue;

                if (!match.Entity.IsValid || !match.Entity.IsAlive)
                    continue;

                var text = match.ModCount.ToString();
                var size = Graphics.MeasureText(text);
                var tint = rule.ModCountColor.Value;

                if (minimapVisible && rule.ShowOnMinimap)
                {
                    var grid = match.Entity.GridPosNum;
                    var at = Graphics.GridToMap(grid, grid) + settings.OffsetFor(rule.IconSize.Value, size);
                    DrawNumber(text, size, at, region, tint, settings);
                }

                if (alsoWorld && Settings.World.Enable && rule.ShowInWorld)
                {
                    var screen = GameController.IngameState.Camera.WorldToScreen(match.Entity.PosNum);
                    DrawNumber(text, size, screen + settings.OffsetFor(rule.WorldIconSize.Value, size),
                        region, tint, settings);
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
            Graphics.DrawBox(
                new RectangleF(pos.X - 2f, pos.Y - 1f, size.X + 4f, size.Y + 2f),
                settings.BackgroundColor.Value);
        }

        // Eight offset copies underneath, which reads over any terrain without covering the map
        // the way a filled box does. Diagonals included: without them the corners of each glyph
        // stay bare and the number still disappears against a busy background.
        if (settings.DrawOutline)
        {
            var outline = settings.OutlineColor.Value;

            for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
            {
                if (dx != 0 || dy != 0)
                    Graphics.DrawText(text, new Vector2(pos.X + dx, pos.Y + dy), outline);
            }
        }

        Graphics.DrawText(text, pos, tint);
    }

    private void DrawInWorld(Match match, RectangleF region)
    {
        var world = Settings.World;
        var screenPos = GameController.IngameState.Camera.WorldToScreen(match.Entity.PosNum);

        if (!region.Contains(screenPos.X, screenPos.Y))
            return;

        var worldIconSize = match.Rule.WorldIconSize.Value;

        // Box and icon share the monster's position so the box frames the icon rather than sitting
        // under it. Their sizes stay independent, so the box can be a tight frame or a wide halo.
        if (world.DrawBox)
        {
            var half = world.BoxSize.Value / 2f;
            var boxColor = world.UseRuleTintForBox ? match.Rule.Tint.Value : world.BoxColor.Value;
            Graphics.DrawFrame(
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
        using (Graphics.SetTextScale(world.LabelScale.Value))
        {
            var textSize = Graphics.MeasureText(text);
            var y = screenPos.Y + halfExtent + labelGap;
            Graphics.DrawText(text, new Vector2(screenPos.X - textSize.X / 2f, y), world.LabelColor.Value);
        }
    }

    private void DrawIcon(Vector2 center, int size, AlertRule rule)
    {
        var half = size / 2f;
        var rect = new RectangleF(center.X - half, center.Y - half, size, size);
        Graphics.DrawImage(IconAtlas.TextureName, rect, SpriteHelper.GetUV(rule.Icon), rule.Tint.Value);
    }

    /// <summary>
    /// One sound per cooldown window at most, using the highest-priority rule among unannounced matches.
    /// </summary>
    private void TryPlayAlerts()
    {
        var matches = _matches;
        if (!Settings.Sound.Enable || matches.Count == 0)
            return;

        // Looping re-announces on every cooldown tick; otherwise each monster is announced once.
        var loop = Settings.Sound.LoopWhileAlive.Value;

        // matches is priority-ascending, so the last eligible match is the most important one.
        Match target = null;
        foreach (var match in matches)
        {
            if (!match.Rule.PlaySound)
                continue;
            if (!loop && _alertedEntityIds.Contains(match.Entity.Id))
                continue;
            target = match;
        }

        if (target == null)
            return;

        if (_soundTimer.ElapsedMilliseconds < Settings.Sound.CooldownMs)
            return;

        var file = target.Rule.SoundFile?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(file))
            return;

        var nameWithoutExt = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrWhiteSpace(nameWithoutExt))
            return;

        // SoundController resolves relative to the plugin folder; the shared Sounds folder is one level up.
        var soundPath = Path.Combine("..", "Sounds", nameWithoutExt).Replace('\\', '/');

        try
        {
            GameController.SoundController.PlaySound(soundPath);
            _soundTimer.Restart();

            // Only commit after the sound actually played, so a suppressed alert is not lost.
            foreach (var match in matches)
                _alertedEntityIds.Add(match.Entity.Id);
        }
        catch (Exception ex)
        {
            LogError($"{Name}: failed to play '{soundPath}': {ex.Message}");
        }
    }
}
