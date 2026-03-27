
using ExileCore;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;
// For MonsterRarity enum
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.PoEMemory.Components;
using ExileCore.Shared;
using SharpDX;
using System;
using System.Linq;
using System.Numerics;
using ExileCore.PoEMemory.MemoryObjects;
using System.Collections.Generic;

namespace MyPlugin
{

public class MyPlugin : ExileCore.BaseSettingsPlugin<MyPluginSettings>
{
    private readonly HashSet<long> processedEntities = new HashSet<long>();
    public override bool Initialise()
    {
        //Perform one-time initialization here

        //Maybe load you custom config (only do so if builtin settings are inadequate for the job)
        //var configPath = Path.Join(ConfigDirectory, "custom_config.txt");
        //if (File.Exists(configPath))
        //{
        //    var data = File.ReadAllText(configPath);
        //}

        return true;
    }


    public override ExileCore.Job Tick()
    {
        //Perform non-render-related work here, e.g. position calculation.
        //This method is still called on every frame, so to really gain
        //an advantage over just throwing everything in the Render method
        //you have to return a custom job, but this is a bit of an advanced technique
        //here's how, just in case:
        //return new Job($"{nameof(MyPlugin)}MainJob", () =>
        //{
        //    var a = Math.Sqrt(7);
        //});

        //otherwise, just run your code here
        //var a = Math.Sqrt(7);
        return null;
    }


    public override void Render()
    {
        // Debug: Write key properties of each rare monster to a file (one entry per frame)
        try
        {
            string debugLogPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "MyPlugin_rare_debug.log");
            using (var sw = new System.IO.StreamWriter(debugLogPath, true))
            {
                foreach (var entity in GameController.Entities)
                {
                    if (!entity.IsAlive || !entity.IsHostile)
                        continue;
                    if (entity.Path != null && entity.Path.Contains("Metadata/Monsters/") && entity.Rarity.ToString() == "Rare")
                    {
                        var magicProps = entity.GetComponent<ExileCore.PoEMemory.Components.ObjectMagicProperties>();
                        string mods = magicProps != null && magicProps.Mods != null ? string.Join(", ", magicProps.Mods) : "(none)";
                        string buffs = entity.Buffs != null ? string.Join(", ", entity.Buffs.Select(b => b.Name)) : "(none)";
                        string debugMsg =
                            $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}]\n" +
                            $"  ID:    {entity.Id}\n" +
                            $"  Path:  {entity.Path}\n" +
                            $"  Alive: {entity.IsAlive}  Hostile: {entity.IsHostile}\n" +
                            $"  Pos:   {entity.Pos}\n" +
                            $"  Mods:  {mods}\n" +
                            $"  Buffs: {buffs}\n" +
                            $"----------------------------------------\n";
                        sw.Write(debugMsg);
                    }
                }
            }
        }
        catch { /* Ignore logging errors */ }

    if (!base.Settings.Enable)
            return;

        // Parse comma-separated targets

    bool found = false;
    foreach (var entity in base.GameController.Entities)
    {
        if (!entity.IsAlive || !entity.IsHostile)
            continue;

        if (entity.Path != null && entity.Path.Contains("Metadata/Monsters/") && entity.Rarity.ToString() == "Rare")
        {
            // Check ObjectMagicProperties.Mods for 'TormentHauntedMonsterThief' (case-insensitive)
            var magicProps = entity.GetComponent<ExileCore.PoEMemory.Components.ObjectMagicProperties>();
            if (magicProps != null && magicProps.Mods != null && magicProps.Mods.Any(mod => mod != null && mod.ToLowerInvariant().Contains("tormenthauntedmonsterthief")))
            {
                DrawAlert(entity);
                found = true;
                // Log to file for confirmation
                try
                {
                    string logPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "MyPlugin_thief_alerts.log");
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ALERT: Thief mod found on entity {entity.Id} {entity.Path}\n";
                    System.IO.File.AppendAllText(logPath, logLine);
                }
                catch { }
                continue;
            }
            // Check Buffs for 'tormenthauntedmonsterthief' (if it ever appears as a buff)
            if (entity.Buffs != null && entity.Buffs.Any(buff => buff.Name != null && buff.Name.ToLowerInvariant().Contains("tormenthauntedmonsterthief")))
            {
                DrawAlert(entity);
                found = true;
                try
                {
                    string logPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "MyPlugin_thief_alerts.log");
                    string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] ALERT: Thief mod found as buff on entity {entity.Id} {entity.Path}\n";
                    System.IO.File.AppendAllText(logPath, logLine);
                }
                catch { }
            }
        }
    }

        // Log match result to file
        try
        {
            string logPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "MyPlugin_match_log.txt");
            string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Match found: {found}\n";
            System.IO.File.AppendAllText(logPath, logLine);
        }
        catch { /* Ignore logging errors */ }

        if (!found)
        {
            base.Graphics.DrawText($"Plugin {base.GetType().Name} is working.", new SharpDX.Vector2(100, 100), SharpDX.Color.Red, 16);
        }
    }

    private void DrawAlert(ExileCore.PoEMemory.MemoryObjects.Entity entity)
    {
        // Example: Draw a red circle on the monster's position
        var worldPos = entity.Pos;
    var screenPos = base.GameController.IngameState.Camera.WorldToScreen(worldPos);
    base.Graphics.DrawFrame(new SharpDX.RectangleF(screenPos.X - 20, screenPos.Y - 20, 40, 40), SharpDX.Color.Red, 3);
    base.Graphics.DrawText($"Target mod/buff found!", new SharpDX.Vector2(screenPos.X, screenPos.Y - 30), SharpDX.Color.Yellow, 16);
        // You can add sound or other alert logic here
    }

    public override void EntityAdded(ExileCore.PoEMemory.MemoryObjects.Entity entity)
    {
        // Log rarity type and value for all monsters
        if (entity.Path != null && entity.Path.Contains("Metadata/Monsters/") && entity.Rarity == MonsterRarity.Rare)
        {
            processedEntities.Add(entity.Id);
            // Log all mods and buffs for this rare monster
            try
            {
                string logPath = System.IO.Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "MyPlugin_rare_entities.log");
                var magicProps = entity.GetComponent<ExileCore.PoEMemory.Components.ObjectMagicProperties>();
                string mods = magicProps != null && magicProps.Mods != null ? string.Join(", ", magicProps.Mods) : "(none)";
                string buffs = entity.Buffs != null ? string.Join(", ", entity.Buffs.Select(b => b.Name)) : "(none)";
                string logLine = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] Rare entity: {entity.Id} {entity.Path}\n  Mods: {mods}\n  Buffs: {buffs}\n";
                System.IO.File.AppendAllText(logPath, logLine);
            }
            catch { /* Ignore logging errors */ }
        }
    }

}
}