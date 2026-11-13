using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using ExileCore;

namespace MonsterModAlerter;

/// <summary>
/// The alert sound: at most one per cooldown, for the highest-priority match that has not been announced yet, or for any
/// match while Loop Sound While Monster Alive is on.
/// </summary>
internal sealed class AlertSound
{
    private readonly HashSet<uint> _announcedIds = new();
    private readonly Stopwatch _sinceLastSound = Stopwatch.StartNew();

    /// <summary>
    /// Lets every entity be announced again. Leaves the cooldown running: restarting it would force a full cooldown wait,
    /// which is the opposite of what forgetting is for.
    /// </summary>
    public void ForgetAnnounced() => _announcedIds.Clear();

    public void PlayIfDue(GameController gameController, SoundSettings settings, List<RuleMatch> matches)
    {
        if (!settings.Enable || matches.Count == 0)
            return;

        // Looping re-announces on every cooldown; otherwise each entity is announced once.
        var loop = settings.LoopWhileAlive.Value;

        // matches is priority-ascending, so the last eligible match is the most important one.
        RuleMatch target = null;
        foreach (var match in matches)
        {
            if (!match.Rule.PlaySound)
                continue;
            if (!loop && _announcedIds.Contains(match.Entity.Id))
                continue;
            target = match;
        }

        if (target == null)
            return;

        if (_sinceLastSound.ElapsedMilliseconds < settings.CooldownMs)
            return;

        var file = target.Rule.SoundFile?.Value?.Trim();
        if (string.IsNullOrWhiteSpace(file))
            return;

        var nameWithoutExtension = Path.GetFileNameWithoutExtension(file);
        if (string.IsNullOrWhiteSpace(nameWithoutExtension))
            return;

        // SoundController resolves relative to the plugin folder; the shared Sounds folder is one level up.
        var soundPath = Path.Combine("..", "Sounds", nameWithoutExtension).Replace('\\', '/');

        try
        {
            gameController.SoundController.PlaySound(soundPath);
            _sinceLastSound.Restart();

            // Only committed after the sound actually played, so a suppressed alert is not lost.
            foreach (var match in matches)
                _announcedIds.Add(match.Entity.Id);
        }
        catch (Exception ex)
        {
            DebugWindow.LogError($"[MonsterModAlerter] Could not play '{soundPath}': {ex.Message}");
        }
    }
}
