using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using ExileCore;

namespace MonsterModAlerter;

/// <summary>
/// The alert sound: at most one per Minimum time between sounds, for the highest-priority match that has not been
/// announced yet, or for any match while Loop while alive is on. A rule's Play sound names a .wav in the HUD's Sounds
/// folder; it is played through WaveOutSound at the Sound section's Volume, as Autoloot plays its sounds, rather than
/// through the HUD's SoundController, which has no volume and whose XAudio2 crashed the HUD in Autoloot.
/// </summary>
internal sealed class AlertSound
{
    /// <summary>A rule's Play sound that plays nothing.</summary>
    public const string None = "None";

    /// <summary>A new rule's sound: the HUD's spoken warning.</summary>
    public const string Default = "attention";

    private static readonly Dictionary<string, byte[]> WavByName = new(StringComparer.OrdinalIgnoreCase);
    private static List<string> _soundNames;

    private readonly HashSet<uint> _announcedIds = new();
    private readonly Stopwatch _sinceLastSound = Stopwatch.StartNew();

    /// <summary>
    /// The choices for a rule's Play sound: None, then every .wav in the HUD's Sounds folder by name, sorted. Read
    /// once; a file added later shows after a HUD restart.
    /// </summary>
    public static List<string> SoundNames => _soundNames ??= ReadSoundNames();

    /// <summary>The HUD's Sounds folder, beside ExileCore.dll (alert, attention, beast, bomb and others).</summary>
    private static string HudSoundsFolder() =>
        Path.Combine(Path.GetDirectoryName(typeof(GameController).Assembly.Location) ?? "", "Sounds");

    private static List<string> ReadSoundNames()
    {
        var names = new List<string> { None };

        try
        {
            names.AddRange(Directory.GetFiles(HudSoundsFolder(), "*.wav")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase));
        }
        catch
        {
            // No Sounds folder: None only.
        }

        return names;
    }

    /// <summary>
    /// Lets every entity be announced again. Leaves the timer running: restarting it would force a full wait, which is
    /// the opposite of what forgetting is for.
    /// </summary>
    public void ForgetAnnounced() => _announcedIds.Clear();

    public void PlayIfDue(SoundSettings settings, List<RuleMatch> matches)
    {
        if (settings.DisableAllSounds || matches.Count == 0)
            return;

        // Looping re-announces after every Minimum time between sounds; otherwise each entity is announced once.
        var loop = settings.LoopWhileAlive.Value;

        // matches is priority-ascending, so the last eligible match is the most important one.
        RuleMatch target = null;
        foreach (var match in matches)
        {
            if (!match.Rule.HasSound)
                continue;
            if (!loop && _announcedIds.Contains(match.EntityId))
                continue;
            target = match;
        }

        if (target == null)
            return;

        if (_sinceLastSound.ElapsedMilliseconds < settings.MinimumTimeBetweenSoundsMs)
            return;

        if (!Play(target.Rule.PlaySound.Value, settings.Volume.Value))
            return;

        _sinceLastSound.Restart();

        // Only committed after the sound played, so a suppressed alert is not lost. Every entity matched now counts as
        // announced, so monsters arriving together are announced once between them.
        foreach (var match in matches)
            _announcedIds.Add(match.EntityId);
    }

    /// <summary>Plays the default sound at a volume in per cent, for the Sound section's test button.</summary>
    public static void PlayTest(int volumePercent) => Play(Default, volumePercent);

    /// <summary>
    /// Plays a sound from the HUD's Sounds folder by name at a volume in per cent (above 100 louder, clipped), without
    /// waiting. False when it cannot be read or played; a missing file is logged once per name.
    /// </summary>
    private static bool Play(string name, int volumePercent)
    {
        if (volumePercent <= 0 || string.IsNullOrWhiteSpace(name) || name == None)
            return false;

        if (!WavByName.TryGetValue(name, out var wav))
        {
            wav = Safe.Read(() =>
            {
                var file = Path.Combine(HudSoundsFolder(), $"{name}.wav");
                return File.Exists(file) ? File.ReadAllBytes(file) : null;
            }, null);

            WavByName[name] = wav;

            if (wav == null)
                DebugWindow.LogError($"[MonsterModAlerter] No sound '{name}' in the HUD's Sounds folder");
        }

        return wav != null && Safe.Read(() => WaveOutSound.Play(wav, Math.Clamp(volumePercent, 0, 300) / 100f), false);
    }
}
