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
using System.Text.RegularExpressions;
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

    /// <summary>
    /// Where a monster was when it was last in range, for rules with Remember Position on. Holds
    /// the grid position rather than the Entity: the entity is gone from the list by the time this
    /// is drawn, and reading a stale one is not safe.
    /// </summary>
    private sealed record Remembered(uint Id, Vector2 GridPos, AlertRule Rule, int ModCount);

    // Scan() builds a fresh list and swaps it in; Render() reads the reference once. The swap is
    // atomic, so the two never touch the same list even if Tick runs off the render thread.
    private volatile List<Match> _matches = new();
    private volatile List<Remembered> _outOfRange = new();

    // Owned by the scan thread alone; Render only ever sees the _outOfRange snapshot above.
    private readonly Dictionary<uint, Remembered> _remembered = new();

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
        _outOfRange = new List<Remembered>();
        _remembered.Clear();
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

        // Remembered positions are dropped too: Refresh Now exists to re-derive everything from
        // the current state, and keeping ghosts would defeat that.
        _outOfRange = new List<Remembered>();
        _remembered.Clear();
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
            _outOfRange = new List<Remembered>();
            _remembered.Clear();
            return;
        }

        var found = new List<Match>();

        // Reused across entities within this scan; local so a Refresh Now from the UI thread
        // cannot collide with a scan in progress.
        var values = new List<string>(16);

        // Case-insensitive, since the same affix has been seen differing only in case.
        var countedMods = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var maxDistance = Settings.MaxDistance.Value;
        var checkMods = Settings.CheckMods;
        var checkBuffs = Settings.CheckBuffs;

        // Every monster the game currently has loaded, whatever its state. Absence from this, at a
        // spot we are standing next to, is what proves a remembered monster is gone — see
        // ForgetVanished.
        var present = _remembered.Count > 0 ? new HashSet<uint>() : null;

        foreach (var entity in GameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (entity == null || !entity.IsHostile)
                continue;

            present?.Add(entity.Id);

            // Checked before the range gate, not after: a monster killed while remembered has to
            // have its icon retired, and this is the only moment we can know that it died.
            if (!entity.IsAlive)
            {
                _remembered.Remove(entity.Id);
                continue;
            }

            if (maxDistance > 0 && entity.DistancePlayer > maxDistance)
                continue;

            // Global filter — the cheap gate that applies to every alert rule.
            if (!Settings.Rarity.Allows(entity.Rarity))
                continue;

            var match = FindMatch(entity, rules, checkMods, checkBuffs, values, countedMods);
            if (match == null)
                continue;

            found.Add(match);

            if (match.Rule.RememberPosition)
            {
                _remembered[entity.Id] =
                    new Remembered(entity.Id, entity.GridPosNum, match.Rule, match.ModCount);
            }
            else
            {
                // The option was turned off, or a higher-priority rule without it now wins.
                _remembered.Remove(entity.Id);
            }
        }

        if (present != null)
            ForgetVanished(present);

        // Ascending priority so the highest-priority icon is drawn last, i.e. on top.
        found.Sort((a, b) => a.Rule.Priority.Value.CompareTo(b.Rule.Priority.Value));
        _matches = found;
        _outOfRange = BuildOutOfRange(found);
    }

    /// <summary>
    /// Drops remembered icons for monsters that are no longer there.
    ///
    /// Seeing a monster die is not enough on its own: kill one and walk away, and it leaves the
    /// entity list without ever being observed dead, so its icon would sit on the map for the rest
    /// of the zone. Absence from the entity list is only evidence of that when we are close enough
    /// that the game would have loaded it — further out, absence just means out of range.
    /// </summary>
    private void ForgetVanished(HashSet<uint> present)
    {
        var player = GameController?.Player;
        if (player == null)
            return;

        var here = player.GridPosNum;
        var radius = Settings.ForgetRadius.Value;
        var radiusSquared = (float)radius * radius;

        List<uint> gone = null;

        foreach (var entry in _remembered.Values)
        {
            if (present.Contains(entry.Id))
                continue;

            // Squared distance: this runs over every remembered monster on every scan, and the
            // square root would buy nothing a squared threshold does not.
            var dx = entry.GridPos.X - here.X;
            var dy = entry.GridPos.Y - here.Y;

            if (dx * dx + dy * dy <= radiusSquared)
                (gone ??= []).Add(entry.Id);
        }

        if (gone == null)
            return;

        foreach (var id in gone)
            _remembered.Remove(id);
    }

    /// <summary>
    /// The remembered monsters that are not currently in range, so Render can draw them faded
    /// without doubling up on the ones it is already drawing live.
    /// </summary>
    private List<Remembered> BuildOutOfRange(List<Match> live)
    {
        if (_remembered.Count == 0)
            return new List<Remembered>();

        var liveIds = new HashSet<uint>(live.Count);
        foreach (var match in live)
            liveIds.Add(match.Entity.Id);

        var stale = new List<Remembered>(_remembered.Count);
        foreach (var entry in _remembered.Values)
        {
            // Dropped rather than kept when its rule is switched off or deleted, so stale icons
            // cannot outlive the rule that asked for them.
            if (!liveIds.Contains(entry.Id) && entry.Rule.Enabled && entry.Rule.RememberPosition)
                stale.Add(entry);
        }

        return stale;
    }

    /// <summary>
    /// Returns the highest-priority rule matching this monster, so one monster yields one icon
    /// rather than a stack of overlapping ones. A rule matches only when every one of its
    /// comma-separated terms is found somewhere on the monster.
    /// </summary>
    private static Match FindMatch(
        Entity entity,
        AlertRule[] rules,
        bool checkMods,
        bool checkBuffs,
        List<string> values,
        HashSet<string> countedMods)
    {
        // Gather the monster's mod and buff names once, then test all rules against that set —
        // cheaper than re-reading components per rule, and it lets terms match across both sources.
        values.Clear();
        countedMods.Clear();

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

                // Only the rare-monster modifiers proper, and only once each. The list carries
                // entries that are not rolled affixes at all, and repeats some of the ones that
                // are — both inflate the number past what the monster's own tooltip shows.
                if (mod.StartsWith(RareModPrefix, StringComparison.OrdinalIgnoreCase) &&
                    countedMods.Add(mod))
                {
                    modCount++;
                }

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

            var patterns = rule.Patterns;

            // A pattern that would not compile disarms the rule. Matching everything instead would
            // paint the whole screen; matching by the raw text would silently mean something else.
            if (rule.UseRegex && patterns.Length != terms.Length)
                continue;

            string display = null;
            var allPresent = true;

            for (var i = 0; i < terms.Length; i++)
            {
                var hit = rule.UseRegex
                    ? FirstValueMatching(values, patterns[i])
                    : FirstValueContaining(values, terms[i]);

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

    /// <summary>
    /// First mod or buff name the pattern matches. Unanchored, so a pattern may match anywhere in
    /// the name — the same reach a plain substring term has.
    /// </summary>
    private static string FirstValueMatching(List<string> values, Regex pattern)
    {
        foreach (var value in values)
        {
            if (pattern.IsMatch(value))
                return value;
        }

        return null;
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
        var outOfRange = _outOfRange;

        if (!Settings.Enable || !GameController.InGame || (matches.Count == 0 && outOfRange.Count == 0))
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

        // Drawn first, so a live monster's icon always sits on top of a remembered one.
        if (minimapVisible)
            DrawRemembered(outOfRange, region);

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

        Write($"[{Name}] matches={matches.Count}  rules={Settings.Rules.Content.Count}  " +
              $"remembered={_remembered.Count} (drawn stale={_outOfRange.Count})", Color.White);

        // The nearest remembered monster and whether it is inside the forget radius: if an icon is
        // lingering, this says whether we are simply not close enough yet to disprove it.
        if (_outOfRange.Count > 0 && GameController?.Player != null)
        {
            var here = GameController.Player.GridPosNum;
            var nearest = float.MaxValue;

            foreach (var entry in _outOfRange)
            {
                var dx = entry.GridPos.X - here.X;
                var dy = entry.GridPos.Y - here.Y;
                nearest = Math.Min(nearest, (float)Math.Sqrt(dx * dx + dy * dy));
            }

            var radius = Settings.ForgetRadius.Value;
            Write($"  nearest stale icon {nearest:0} grid away, forget radius {radius} — " +
                  (nearest <= radius ? "inside, so it would have been dropped" : "walk closer to clear it"),
                nearest <= radius ? Color.Orange : Color.White);
        }
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

        write($"    counted {match.ModCount} of {mods.Count} entries " +
              $"(prefix '{RareModPrefix}', duplicates ignored)", Color.White);

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var mod in mods)
        {
            if (string.IsNullOrEmpty(mod))
                continue;

            // A duplicate and a non-affix are excluded for different reasons, so they are marked
            // differently — otherwise a wrong count sends you looking at the wrong thing.
            if (!mod.StartsWith(RareModPrefix, StringComparison.OrdinalIgnoreCase))
                write($"        {mod}", Color.Gray);
            else if (seen.Add(mod))
                write($"      + {mod}", Color.LightGreen);
            else
                write($"      = {mod}  (duplicate)", Color.Orange);
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
    /// Last known positions of monsters that have dropped out of range, faded so they read as
    /// "was here" rather than "is here". Drawn from a stored grid position, never from the entity:
    /// by definition it is no longer in the entity list.
    /// </summary>
    private void DrawRemembered(List<Remembered> stale, RectangleF region)
    {
        if (stale.Count == 0)
            return;

        var countSettings = Settings.ModCount;

        foreach (var entry in stale)
        {
            var rule = entry.Rule;
            if (!rule.ShowOnMinimap)
                continue;

            var point = Graphics.GridToMap(entry.GridPos, entry.GridPos);
            if (!region.Contains(point.X, point.Y))
                continue;

            DrawIcon(point, rule.IconSize.Value, rule);

            if (!rule.ShowModCount || entry.ModCount < rule.ModCountMinimum.Value)
                continue;

            // The count is the whole point of remembering these — it is what the kill order is
            // decided on — so it is faded alongside rather than dropped. Rare affixes are fixed at
            // spawn, so the remembered number stays accurate however long ago it was read.
            using (Graphics.SetTextScale(countSettings.TextScale.Value))
            {
                var text = entry.ModCount.ToString();
                var size = Graphics.MeasureText(text);

                DrawNumber(text, size, point + countSettings.OffsetFor(rule.IconSize.Value, size),
                    region, rule.ModCountColor.Value, countSettings);
            }
        }
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
