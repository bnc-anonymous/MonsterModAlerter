using System.Diagnostics;
using System.Linq;
using ExileCore;

namespace MonsterModAlerter;

/// <summary>
/// Marks monsters, and with Match Any Entity any entity, by the names of their modifiers and buffs, their metadata path
/// and their rarity, as set by the alert rules: an icon on the minimap and in the world, an optional modifier count, and a
/// sound. Optional per rule: the last position of a monster that left range, and a per-area tally.
///
/// The work is split by what it is for: EntityScan walks the entities every Scan Interval and runs RuleMatcher over them,
/// keeping RememberedPositions and SeenTally up to date; MatchDrawing and DebugOverlay draw what the last scan published;
/// AlertSound plays the sound.
/// </summary>
public class MonsterModAlerter : BaseSettingsPlugin<MonsterModAlerterSettings>
{
    private readonly SeenTally _tally = new();
    private readonly EntityScan _scan;
    private readonly AlertSound _sound = new();
    private readonly Stopwatch _scanTimer = Stopwatch.StartNew();
    private MatchDrawing _matchDrawing;
    private DebugOverlay _debugOverlay;

    /// <summary>
    /// Set by the Reset Counts button and consumed on the next Tick. Deferred for the same reason the settings reset is:
    /// the button fires on the UI thread, and emptying the tally underneath a scan that is walking it would throw.
    /// </summary>
    private volatile bool _resetSeenRequested;

    public MonsterModAlerter()
    {
        Name = "Monster Mod Alerter";
        _scan = new EntityScan(_tally);
    }

    public override bool Initialise()
    {
        // ExileCore sorts plugins by Order ascending and renders in that sequence, so a high value draws on top of other
        // plugins. The sort runs once, after every plugin has been initialised, so assigning here applies this session -
        // but later edits need a HUD restart.
        Order = Settings.RenderOrder.Value;

        // First run (or after deleting every rule) starts with a usable example.
        if (Settings.Rules.Content.Count == 0)
            Settings.Rules.Content.Add(AlertRule.Default());

        Settings.ForceRefresh.OnPressed = ForceRefresh;
        Settings.SeenPanel.Reset.OnPressed = () => _resetSeenRequested = true;

        // Registered with the HUD's own input handling, which is what makes PressedOnce work. Re-registered on change,
        // since the key it was told about is the one it watches.
        var toggleKey = Settings.SeenPanel.ToggleKey;
        Input.RegisterKey(toggleKey.Value);
        toggleKey.OnValueChanged += () => Input.RegisterKey(toggleKey.Value);

        _matchDrawing = new MatchDrawing(Graphics);
        _debugOverlay = new DebugOverlay(Graphics, Name);

        return true;
    }

    public override void AreaChange(AreaInstance area)
    {
        _scan.Clear();
        _sound.ForgetAnnounced();

        // "This map" is the whole of what the tally means, so a new area is where it starts again.
        _tally.Clear();
    }

    /// <summary>
    /// Drops the announced entities and forces the next Tick to re-scan instead of waiting out the scan interval. Drawing
    /// settings are read per frame, so this is only needed to re-arm sound alerts or to apply a rule change without
    /// waiting. Remembered positions are dropped too: Refresh Now exists to re-derive everything from the current state,
    /// and keeping ghosts would defeat that.
    /// </summary>
    private void ForceRefresh()
    {
        _sound.ForgetAnnounced();
        _scan.Clear();
        _scanTimer.Restart();

        if (GameController.InGame)
            _scan.Run(GameController, Settings);
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

        if (_resetSeenRequested)
        {
            _resetSeenRequested = false;
            _tally.Clear();
        }

        if (!Settings.Enable || !GameController.InGame)
            return null;

        // Polled before the scan interval gate, so the key still answers on the frames that do not re-scan. Toggling the
        // setting itself is deliberate: the menu tick and the key are one switch, not two that can disagree.
        if (Settings.SeenPanel.ToggleKey.PressedOnce())
            Settings.SeenPanel.Show.Value = !Settings.SeenPanel.Show.Value;

        if (_scanTimer.ElapsedMilliseconds < Settings.ScanIntervalMs)
            return null;

        _scanTimer.Restart();
        _scan.Run(GameController, Settings);
        _sound.PlayIfDue(GameController, Settings.Sound, _scan.Matches);
        return null;
    }

    public override void Render()
    {
        var matches = _scan.Matches;
        var outOfRange = _scan.OutOfRange;

        if (!Settings.Enable || !GameController.InGame)
            return;

        var ingameUi = GameController.IngameState.IngameUi;

        if (!Settings.IgnoreFullscreenPanels && ingameUi.FullscreenPanels.Any(x => x.IsVisible))
            return;

        if (!Settings.IgnoreLargePanels && ingameUi.LargePanels.Any(x => x.IsVisible))
            return;

        // Drawn before the nothing-to-draw check below: the tally is a record of the whole map, so it has to stay up on
        // the screen where everything has already been killed.
        _tally.Draw(Graphics, Settings.SeenPanel);

        var region = MatchDrawing.DrawRegionOf(GameController, ingameUi, Settings.LimitDrawRegion);

        var largeMapVisible = ingameUi.Map.LargeMap.IsVisibleLocal;
        var minimapVisible = Settings.DrawOnlyOnLargeMap
            ? largeMapVisible
            : largeMapVisible || ingameUi.Map.SmallMiniMap.IsVisibleLocal;

        if (matches.Count > 0 || outOfRange.Count > 0)
            _matchDrawing.Draw(GameController, Settings, matches, outOfRange, region, minimapVisible);

        // Drawn when nothing matched as well: that is exactly the state the overlay is opened to explain.
        if (Settings.DebugOverlay)
            _debugOverlay.Draw(GameController, Settings, _scan, _tally, matches, region, ingameUi, minimapVisible);
    }
}
