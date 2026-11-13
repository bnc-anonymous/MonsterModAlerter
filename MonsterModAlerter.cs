using System.Diagnostics;
using System.Linq;
using System.Windows.Forms;
using ExileCore;
using ExileCore.Shared.Nodes;

namespace MonsterModAlerter;

/// <summary>
/// Marks monsters, and with Match any entity any entity, by the names of their modifiers and buffs, their metadata path
/// and their rarity, as set by the alert rules: an icon on the map and in the world, an optional modifier count, and a
/// sound. Optional per rule: the last position of a monster that left range, and a per-area seen tally.
///
/// The work is split by what it is for: EntityScan walks the entities every scan interval and runs RuleMatcher over
/// them, keeping RememberedPositions and SeenTally up to date; MatchDrawing and DebugOverlay draw what the last scan
/// published; AlertSound plays the sound.
/// </summary>
public class MonsterModAlerter : BaseSettingsPlugin<MonsterModAlerterSettings>
{
    /// <summary>The ConfigVersion a saved file reaches once every step in ApplySettingsChanges has run.</summary>
    private const int CurrentConfigVersion = 0;

    private readonly SeenTally _tally = new();
    private readonly EntityScan _scan;
    private readonly AlertSound _sound = new();
    private readonly Stopwatch _scanTimer = Stopwatch.StartNew();
    private MatchDrawing _matchDrawing;
    private DebugOverlay _debugOverlay;

    public MonsterModAlerter() => _scan = new EntityScan(_tally);

    public override bool Initialise()
    {
        // ExileCore sorts plugins by Order ascending and renders in that sequence, so a high value draws on top of
        // other plugins. The sort runs once, after every plugin has been initialised, so assigning here applies this
        // session - but later edits need a HUD restart.
        Order = Settings.Display.RenderOrder.Value;

        // First run, or after every rule is deleted, starts with the example rule.
        if (Settings.Rules.Content.Count == 0)
            Settings.Rules.Content.Add(AlertRule.Example());

        RegisterHotkey(Settings.SeenTally.ToggleKey);
        ApplySettingsChanges();

        _matchDrawing = new MatchDrawing(Graphics);
        _debugOverlay = new DebugOverlay(Graphics, Name);

        return true;
    }

    /// <summary>
    /// Brings a saved settings file up to date with changed defaults, once per change. No default has changed since the
    /// plugin was published, so there are no steps yet; the first adds one and raises CurrentConfigVersion to 1.
    /// </summary>
    private void ApplySettingsChanges()
    {
        if (Settings.ConfigVersion >= CurrentConfigVersion)
            return;

        Settings.ConfigVersion = CurrentConfigVersion;
    }

    /// <summary>
    /// Tells Input about a hotkey so PressedOnce answers, and again whenever it is rebound in the menu. A key left
    /// unbound is not registered, since registering None would claim a key nobody pressed.
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
        _matchDrawing?.ForgetCornerMap();

        // "This map" is the whole of what the tally means, so a new area is where it starts again.
        _tally.Clear();
    }

    /// <summary>
    /// Drops the announced entities and re-scans now instead of waiting out the scan interval. Drawing settings are
    /// read per frame, so this is only needed to re-arm sound alerts or to apply a rule change without waiting.
    /// Remembered positions are dropped too: Refresh now exists to re-derive everything from the current state, and
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
        // Before the Enable check, so the menu's buttons work with the plugin switched off.
        AnswerMenuButtons();

        if (!Settings.Enable || !Safe.Read(GameController, static g => g.InGame, false))
            return null;

        // Polled before the scan interval gate, so the key still answers on the frames that do not re-scan. Toggling
        // the setting itself is deliberate: the menu and the key are one switch, not two that can disagree.
        var toggleKey = Settings.SeenTally.ToggleKey;

        if (IsBound(toggleKey) && toggleKey.PressedOnce())
            Settings.SeenTally.Show.Value = !Settings.SeenTally.Show.Value;

        if (_scanTimer.ElapsedMilliseconds < Settings.Debug.EntityScanIntervalMs)
            return null;

        _scanTimer.Restart();
        _scan.Run(GameController, Settings);
        _sound.PlayIfDue(GameController, Settings.Sound, _scan.Matches);
        return null;
    }

    /// <summary>
    /// The menu's buttons raise flags in the settings and this answers them. A button fires while the menu is drawn;
    /// answering here keeps every change to the scan's state, and every scan, in the Tick.
    /// </summary>
    private void AnswerMenuButtons()
    {
        if (Settings.ResetRequested)
        {
            Settings.ResetRequested = false;
            SettingsReset.Apply(Settings);
            ForceRefresh();
            LogMessage("Settings and rules reset to defaults", 5f);
            CursorMessage.Show(CursorMessage.Kind.Success, "Settings reset");
        }

        if (Settings.Debug.RefreshRequested)
        {
            Settings.Debug.RefreshRequested = false;
            ForceRefresh();
            CursorMessage.Show(CursorMessage.Kind.Info, "Refreshed");
        }

        if (Settings.SeenTally.ClearRequested)
        {
            Settings.SeenTally.ClearRequested = false;
            _tally.Clear();
            CursorMessage.Show(CursorMessage.Kind.Info, "Tally cleared");
        }
    }

    public override void Render()
    {
        // Before the Enable check, so a message raised with the plugin switched off is still seen.
        CursorMessage.Draw(Graphics, GameController);

        var matches = _scan.Matches;
        var remembered = _scan.Remembered;

        if (!Settings.Enable || !Safe.Read(GameController, static g => g.InGame, false))
            return;

        var ingameUi = Safe.Read(GameController, static g => g.IngameState.IngameUi, null);

        if (ingameUi == null)
            return;

        var display = Settings.Display;

        var preventDrawingOverPanels = display.PreventDrawingOverPanels.Value;

        // A fullscreen or large panel leaves no world worth drawing on; a side panel only covers part of the screen,
        // which the draw region below leaves out.
        if (preventDrawingOverPanels &&
            Safe.Read(ingameUi, static ui => ui.FullscreenPanels.Any(panel => panel.IsVisible) ||
                                             ui.LargePanels.Any(panel => panel.IsVisible), false))
            return;

        // Drawn before the nothing-to-draw check below: the tally is a record of the whole map, so it has to stay up on
        // the screen where everything has already been killed.
        _tally.Draw(Graphics, Settings.SeenTally);

        // Empty when it cannot be read, which draws nothing rather than drawing over the panels.
        var region = Safe.Read((GameController, Ui: ingameUi, Prevent: preventDrawingOverPanels),
            static s => MatchDrawing.DrawRegionOf(s.GameController, s.Ui, s.Prevent), default);

        var mapArea = _matchDrawing.MapAreaOf(ingameUi, region, display.EnableDrawingOnCornerMap);

        _matchDrawing.Draw(GameController, Settings, matches, remembered, region, mapArea);

        // Drawn when nothing matched as well: that is exactly the state the overlay is opened to explain. Guarded as a
        // whole: it reads freely, and a throw part-way only cuts this frame's text short.
        if (Settings.Debug.ShowOverlay)
        {
            Safe.Try(() => _debugOverlay.Draw(
                GameController, Settings, _scan, _tally, matches, region, ingameUi, mapArea, _matchDrawing));
        }
    }
}
