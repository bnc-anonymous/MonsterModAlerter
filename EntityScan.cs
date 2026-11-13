using System;
using System.Collections.Generic;
using System.Linq;
using ExileCore;
using ExileCore.PoEMemory.Components;
using ExileCore.PoEMemory.MemoryObjects;
using ExileCore.Shared.Enums;

namespace MonsterModAlerter;

/// <summary>
/// Walks the loaded entities, runs the alert rules against them, and publishes the matches and the remembered positions
/// that are out of range. Also records into the seen tally and keeps the remembered positions up to date.
/// </summary>
internal sealed class EntityScan
{
    private readonly RememberedPositions _remembered = new();
    private readonly SeenTally _tally;

    // Run builds fresh lists and swaps them in; drawing reads each reference once. The swap is atomic, so the two never
    // touch the same list even if the scan runs off the render thread.
    private volatile List<RuleMatch> _matches = new();
    private volatile List<RememberedPosition> _outOfRange = new();

    public EntityScan(SeenTally tally) => _tally = tally;

    /// <summary>The last scan's matches, in ascending priority, so the highest-priority icon is drawn last.</summary>
    public List<RuleMatch> Matches => _matches;

    /// <summary>The remembered positions of entities the last scan did not match live.</summary>
    public List<RememberedPosition> OutOfRange => _outOfRange;

    public int RememberedCount => _remembered.Count;

    /// <summary>
    /// How many entities the last scan's walk over every entity covered, for the debug overlay. 0 when no rule asked for
    /// that walk.
    /// </summary>
    public int AnyEntityWalkCount { get; private set; }

    /// <summary>How many entities outside the monster gates the last scan matched, for the debug overlay.</summary>
    public int AnyEntityMatchCount { get; private set; }

    /// <summary>Drops the matches and every remembered position. The tally is left alone.</summary>
    public void Clear()
    {
        _matches = new List<RuleMatch>();
        _outOfRange = new List<RememberedPosition>();
        _remembered.Clear();
    }

    public void Run(GameController gameController, MonsterModAlerterSettings settings)
    {
        // A rule needs something to go on, but either half will do: a mod term, or a monster filter naming (or
        // excluding) a family. A rule with neither would match every monster.
        var rules = settings.Rules?.Content?
            .Where(r => r is { Enabled.Value: true } &&
                        (!string.IsNullOrWhiteSpace(r.SearchTerm?.Value) || r.Metadata?.IsBlank == false))
            .ToArray() ?? [];

        if (rules.Length == 0)
        {
            Clear();
            return;
        }

        var matches = new List<RuleMatch>();

        // Local, not a field, so a Refresh Now from the UI thread cannot collide with a scan in progress.
        var matcher = new RuleMatcher(rules, settings.CheckMods, settings.CheckBuffs, settings.AllowMultipleMatches);

        var maxDistance = settings.MaxDistance.Value;
        var allowMultipleMatches = settings.AllowMultipleMatches.Value;

        // Whether anything is asking to see past the monster list at all. When nothing is, the second walk below never
        // happens.
        var anyEntityRules = false;
        foreach (var rule in rules)
        {
            if (rule.AnyEntity?.Value == true)
            {
                anyEntityRules = true;
                break;
            }
        }

        // Every entity the game currently has loaded, whatever its state. Absence from this, at a spot the player is
        // standing next to, is what proves a remembered entity is gone - see RememberedPositions.ForgetAbsentNear.
        var loadedIds = _remembered.Count > 0 ? new HashSet<uint>() : null;

        // Which entities the monster walk has already offered to every rule. The second walk skips these rather than
        // skipping by entity type: whether the game files it under Monster is exactly what this must not depend on,
        // since critters may well be filed there too.
        var offeredIds = anyEntityRules ? new HashSet<uint>() : null;

        var anyEntityMatches = 0;

        foreach (var entity in gameController.EntityListWrapper.ValidEntitiesByType[EntityType.Monster])
        {
            if (entity == null)
                continue;

            offeredIds?.Add(entity.Id);

            // Non-hostile things in this list are still offered to Match Any Entity rules below - they are only
            // disqualified from being a monster, which is what those rules are for.
            var hostile = entity.IsHostile;

            if (!hostile && !anyEntityRules)
                continue;

            loadedIds?.Add(entity.Id);

            // Checked before the range gate, not after: a monster killed while remembered has to have its icon retired,
            // and this is the only moment it can be known that it died.
            if (!entity.IsAlive)
            {
                _remembered.Forget(entity.Id);
                continue;
            }

            if (maxDistance > 0 && entity.DistancePlayer > maxDistance)
                continue;

            // Global filter - the cheap gate that applies to every monster-only alert rule. Rarity is a monster's own
            // property, so it does not gate the rules that are not asking for one; those would be thrown away wholesale
            // by a filter set to Rare + Unique.
            var monsterOk = hostile && settings.Rarity.Allows(entity.Rarity);

            if (!monsterOk && !anyEntityRules)
                continue;

            if (OfferToRules(entity, monsterOk, anyEntityRules, checkAlive: true) && !monsterOk)
                anyEntityMatches++;
        }

        if (anyEntityRules)
        {
            // Everything the game has loaded, monsters included - the monster list is a subset of it, which is why the
            // ids already offered are skipped. Only entered when a rule asks for it, since this is the whole entity list
            // rather than one bucket of it.
            var all = gameController.EntityListWrapper.Entities;
            AnyEntityWalkCount = all?.Count ?? 0;

            if (all != null)
            {
                try
                {
                    foreach (var entity in all)
                    {
                        if (entity == null || offeredIds.Contains(entity.Id) || !entity.IsValid)
                            continue;

                        // Anything without a position cannot be drawn on the map or in the world, and asking one how
                        // far away it is is what would throw. This is also the line between things that are somewhere
                        // and bookkeeping entities.
                        if (!entity.HasComponent<Positioned>())
                            continue;

                        loadedIds?.Add(entity.Id);

                        // A door or a chest has no life to lose, so absence of the component means alive here rather
                        // than dead - the opposite of what IsAlive would answer.
                        if (IsDead(entity))
                        {
                            _remembered.Forget(entity.Id);
                            continue;
                        }

                        if (maxDistance > 0 && entity.DistancePlayer > maxDistance)
                            continue;

                        if (OfferToRules(entity, monsterOk: false, anyOk: true, checkAlive: false))
                            anyEntityMatches++;
                    }
                }
                catch (InvalidOperationException)
                {
                    // The HUD adds and removes entities on its own thread. If it did so mid-walk, this scan is simply
                    // abandoned - the next one is 250ms away and will see the settled list. Deliberately narrow:
                    // anything else is a bug worth surfacing.
                }
            }
        }
        else
        {
            AnyEntityWalkCount = 0;
        }

        AnyEntityMatchCount = anyEntityMatches;

        if (loadedIds != null && gameController?.Player is { } player)
            _remembered.ForgetAbsentNear(player.GridPosNum, settings.ForgetRadius.Value, loadedIds);

        // Ascending priority so the highest-priority icon is drawn last, i.e. on top.
        matches.Sort((a, b) => a.Rule.Priority.Value.CompareTo(b.Rule.Priority.Value));
        _matches = matches;
        _outOfRange = _remembered.OutOfRangeOf(matches);

        _tally.PublishRows();

        // Runs the rules against one entity and files whatever they said. Returns whether anything matched, so the
        // caller can count what the second walk is contributing.
        bool OfferToRules(Entity entity, bool monsterOk, bool anyOk, bool checkAlive)
        {
            var best = matcher.BestMatchOf(entity, monsterOk, anyOk, checkAlive);

            if (best == null)
                return false;

            // Tallied when any of the rules that matched asked to be, whether or not it is the one that won the icon:
            // the tally is asking what was here, not which rule drew it.
            bool tally = best.Rule.CountInSeenPanel;

            if (allowMultipleMatches)
            {
                // Slots are assigned here so drawing never has to group by entity: it just reads the position each
                // icon was given.
                var matchesOfEntity = matcher.MatchesOfEntity;

                for (var i = 0; i < matchesOfEntity.Count; i++)
                {
                    matches.Add(matchesOfEntity[i] with { Slot = i, SlotCount = matchesOfEntity.Count });
                    tally |= matchesOfEntity[i].Rule.CountInSeenPanel;
                }
            }
            else
            {
                matches.Add(best);
            }

            if (tally)
                _tally.Record(entity.Id, entity.Path);

            // The highest-priority match owns the remembered icon, whether or not the others are also being drawn - one
            // entity leaves one mark on the map.
            if (best.Rule.RememberPosition)
            {
                _remembered.Remember(new RememberedPosition(entity.Id, entity.GridPosNum, best.Rule, best.ModCount));
            }
            else
            {
                // The option was turned off, or a higher-priority rule without it now wins.
                _remembered.Forget(entity.Id);
            }

            return true;
        }
    }

    /// <summary>
    /// Whether this entity is a corpse. Only the Life component is consulted, and only when it has one: a chest or a
    /// doorway has no life, and answering "dead" for those would hide every one of them. Monsters are asked IsAlive
    /// instead, which is meaningful for them.
    /// </summary>
    private static bool IsDead(Entity entity) =>
        entity.TryGetComponent<Life>(out var life) && life != null && life.CurHP <= 0;
}
