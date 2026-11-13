using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared.Nodes;

namespace MonsterModAlerter;

/// <summary>
/// Marks monsters, and with Match Any Entity any entity, by the names of their modifiers and buffs, their metadata path
/// and their rarity, as set by the alert rules: an icon on the minimap and in the world, an optional modifier count,
/// and a sound. Optional per rule: the last position of a monster that left range, and a per-area tally.
///
/// The work is split by what it is for: EntityScan walks the entities every Scan Interval and runs RuleMatcher over
/// them, keeping RememberedPositions and SeenTally up to date; MatchDrawing and DebugOverlay draw what the last scan
/// published; AlertSound plays the sound.
/// </summary>
public class MonsterModAlerter : BaseSettingsPlugin<MonsterModAlerterSettings>
{
    private readonly SeenTally _tally = new();
    private readonly EntityScan _scan;
    private readonly AlertSound _sound = new();
    private readonly Stopwatch _scanTimer = Stopwatch.StartNew();
    private MatchDrawing _matchDrawing;
    private DebugOverlay _debugOverlay;

    // Set by the Refresh Now and Reset Counts buttons and answered by the next Tick. A button fires while the menu is
    // drawn; answering in the Tick keeps every change to the scan's state, and every scan, in one place.
    private volatile bool _refreshRequested;
    private volatile bool _resetSeenRequested;

    public MonsterModAlerter()
    {
        Name = "Monster Mod Alerter";
        _scan = new EntityScan(_tally);
    }

    public override bool Initialise()
    {
        // ExileCore sorts plugins by Order ascending and renders in that sequence, so a high value draws on top of
        // other plugins. The sort runs once, after every plugin has been initialised, so assigning here applies this
        // session - but later edits need a HUD restart.
        Order = Settings.RenderOrder.Value;

        // First run (or after deleting every rule) starts with a usable example.
        if (Settings.Rules.Content.Count == 0)
            Settings.Rules.Content.Add(AlertRule.Default());

        Settings.ForceRefresh.OnPressed = () => _refreshRequested = true;
        Settings.SeenPanel.Reset.OnPressed = () => _resetSeenRequested = true;

        RegisterHotkey(Settings.SeenPanel.ToggleKey);

        _matchDrawing = new MatchDrawing(Graphics);
        _debugOverlay = new DebugOverlay(Graphics, Name);

        return true;
    }

    /// <summary>
    /// Tells Input about a hotkey so PressedOnce answers, and again whenever it is rebound in the menu. A key left unbound
    /// is not registered, since registering None would claim a key nobody pressed.
    /// </summary>
    private static void RegisterHotkey(HotkeyNodeV2 hotkey)
    {
        RegisterIfBound(hotkey);
        hotkey.OnValueChanged += () => RegisterIfBound(hotkey);
    }

    private static void RegisterIfBound(HotkeyNodeV2 hotkey)
    {
        if (IsBound(hotkey))
            Input.RegisterKey(hotkey.Value);
    }

    private static bool IsBound(HotkeyNodeV2 hotkey) =>
        Safe.Read(hotkey, static h => h.Value.Key, Keys.None) != Keys.None;

    public override void AreaChange(AreaInstance area)
    {
        _scan.Clear();
        _sound.ForgetAnnounced();

        // "This map" is the whole of what the tally means, so a new area is where it starts again.
        _tally.Clear();
    }

    /// <summary>
    /// Drops the announced entities and re-scans now instead of waiting out the scan interval. Drawing settings are
    /// read per frame, so this is only needed to re-arm sound alerts or to apply a rule change without waiting.
    /// Remembered positions are dropped too: Refresh Now exists to re-derive everything from the current state, and
    /// keeping ghosts would defeat that.
    /// </summary>
    private void ForceRefresh()
    {
        _sound.ForgetAnnounced();
        _scan.Clear();
        _scanTimer.Restart();

        if (Safe.Read(GameController, static g => g.InGame, false))
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

        if (_refreshRequested)
        {
            _refreshRequested = false;
            ForceRefresh();
        }

        if (_resetSeenRequested)
        {
            _resetSeenRequested = false;
            _tally.Clear();
        }

        if (!Settings.Enable || !Safe.Read(GameController, static g => g.InGame, false))
            return null;

        // Polled before the scan interval gate, so the key still answers on the frames that do not re-scan. Toggling
        // the setting itself is deliberate: the menu tick and the key are one switch, not two that can disagree.
        var toggleKey = Settings.SeenPanel.ToggleKey;

        if (IsBound(toggleKey) && toggleKey.PressedOnce())
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

        if (!Settings.Enable || !Safe.Read(GameController, static g => g.InGame, false))
            return;

        var ingameUi = Safe.Read(GameController, static g => g.IngameState.IngameUi, null);

        if (ingameUi == null)
            return;

        if (!Settings.IgnoreFullscreenPanels &&
            Safe.Read(ingameUi, static ui => ui.FullscreenPanels.Any(panel => panel.IsVisible), false))
            return;

        if (!Settings.IgnoreLargePanels &&
            Safe.Read(ingameUi, static ui => ui.LargePanels.Any(panel => panel.IsVisible), false))
            return;

        // Drawn before the nothing-to-draw check below: the tally is a record of the whole map, so it has to stay up on
        // the screen where everything has already been killed.
        _tally.Draw(Graphics, Settings.SeenPanel);

        // Empty when it cannot be read, which draws nothing rather than drawing over the panels.
        var region = Safe.Read((GameController, Ui: ingameUi, Limit: Settings.LimitDrawRegion.Value),
            static s => MatchDrawing.DrawRegionOf(s.GameController, s.Ui, s.Limit), default);

        var largeMapVisible = Safe.Read(ingameUi, static ui => ui.Map.LargeMap.IsVisibleLocal, false);
        var minimapVisible = Settings.DrawOnlyOnLargeMap
            ? largeMapVisible
            : largeMapVisible || Safe.Read(ingameUi, static ui => ui.Map.SmallMiniMap.IsVisibleLocal, false);

        if (matches.Count > 0 || outOfRange.Count > 0)
            _matchDrawing.Draw(GameController, Settings, matches, outOfRange, region, minimapVisible);

        // Drawn when nothing matched as well: that is exactly the state the overlay is opened to explain. Guarded as a
        // whole: it reads freely, and a throw part-way only cuts this frame's text short.
        if (Settings.DebugOverlay)
        {
            Safe.Try(() => _debugOverlay.Draw(
                GameController, Settings, _scan, _tally, matches, region, ingameUi, minimapVisible));
        }
    }
}
