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
    private volatile List<RememberedPosition> _publishedRemembered = new();

    public EntityScan(SeenTally tally) => _tally = tally;

    /// <summary>The last scan's matches, in ascending priority, so the highest-priority icon is drawn last.</summary>
    public List<RuleMatch> Matches => _matches;

    /// <summary>
    /// The remembered positions as of the last scan, live entities' included; drawing shows those whose entity it does
    /// not draw live. See RememberedPositions.Snapshot.
    /// </summary>
    public List<RememberedPosition> Remembered => _publishedRemembered;

    public int RememberedCount => _remembered.Count;

    /// <summary>
    /// How many entities the last scan's walk over every entity covered, for the debug overlay. 0 when no rule asked
    /// for that walk.
    /// </summary>
    public int AnyEntityWalkCount { get; private set; }

    /// <summary>How many entities outside the monster gates the last scan matched, for the debug overlay.</summary>
    public int AnyEntityMatchCount { get; private set; }

    /// <summary>Drops the matches and every remembered position. The tally is left alone.</summary>
    public void Clear()
    {
        _matches = new List<RuleMatch>();
        _publishedRemembered = new List<RememberedPosition>();
        _remembered.Clear();
    }

    public void Run(GameController gameController, MonsterModAlerterSettings settings)
    {
        // A rule needs something to go on, but either half will do: a mod term, or Metadata paths naming (or excluding)
        // a family. A rule with neither would match every monster.
        var rules = settings.RulesOfEnabledGroups()
            .Where(r => r is { Enabled.Value: true } &&
                        (!string.IsNullOrWhiteSpace(r.SearchTerm?.Value) || r.HasMetadataPaths))
            .ToArray();

        if (rules.Length == 0)
        {
            Clear();
            return;
        }

        var matches = new List<RuleMatch>();

        var drawMultipleMatchingIcons = settings.Display.DrawMultipleMatchingIcons.Value;
        var matcher = new RuleMatcher(rules, drawMultipleMatchingIcons);

        // The rarities some monster rule accepts. A monster of any other rarity is skipped before its components are
        // read, the cheap gate a global rarity filter used to be, now worked out from the rules themselves.
        bool acceptsNormal = false, acceptsMagic = false, acceptsRare = false, acceptsUnique = false;

        foreach (var rule in rules)
        {
            if (rule.AnyEntity?.Value == true)
                continue;

            acceptsNormal |= rule.NormalMonsters;
            acceptsMagic |= rule.MagicMonsters;
            acceptsRare |= rule.RareMonsters;
            acceptsUnique |= rule.UniqueMonsters;
        }

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

        var monsters = Safe.Read(gameController,
            static g => g.EntityListWrapper.ValidEntitiesByType[EntityType.Monster], null);

        // Without the monster list nothing looks loaded, and every remembered position near the player would be
        // forgotten, so the scan publishes nothing and the last one stands.
        if (monsters == null)
            return;

        try
        {
            foreach (var entity in monsters)
            {
                // An entity that cannot be read is skipped, not the whole scan.
                if (entity != null)
                    Safe.Try(() => OfferMonster(entity));
            }

            AnyEntityWalkCount = 0;

            if (anyEntityRules)
            {
                // Everything the game has loaded, monsters included - the monster list is a subset of it, which is why
                // the ids already offered are skipped. Only entered when a rule asks for it, since this is the whole
                // entity list rather than one bucket of it.
                var all = Safe.Read(gameController, static g => g.EntityListWrapper.Entities, null);
                AnyEntityWalkCount = all?.Count ?? 0;

                if (all != null)
                {
                    foreach (var entity in all)
                    {
                        if (entity != null)
                            Safe.Try(() => OfferOther(entity));
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // A list changed during its walk. The scan is abandoned and the last one stands: a part-walked list leaves
            // entities out of loadedIds, and their remembered positions near the player would be forgotten. The next
            // scan is one scan interval away.
            return;
        }

        AnyEntityMatchCount = anyEntityMatches;

        if (loadedIds != null &&
            Safe.Read(gameController, static g => g.Player?.GridPosNum, null) is { } playerGridPos)
        {
            _remembered.ForgetAbsentNear(playerGridPos, loadedIds);
        }

        // Ascending priority so the highest-priority icon is drawn last, i.e. on top.
        matches.Sort((a, b) => a.Rule.Priority.Value.CompareTo(b.Rule.Priority.Value));
        _matches = matches;
        _publishedRemembered = _remembered.Snapshot(rules);

        _tally.PublishRows();

        bool RarityAccepted(MonsterRarity rarity) => rarity switch
        {
            MonsterRarity.White => acceptsNormal,
            MonsterRarity.Magic => acceptsMagic,
            MonsterRarity.Rare => acceptsRare,
            MonsterRarity.Unique => acceptsUnique,
            _ => false,
        };

        // An entity from the monster list.
        void OfferMonster(Entity entity)
        {
            offeredIds?.Add(entity.Id);

            // Non-hostile things in this list are still offered to Match any entity rules - they are only disqualified
            // from being a monster, which is what those rules are for.
            var hostile = entity.IsHostile;

            // Every way out below where no rule matches a loaded entity forgets its remembered position: one remembered
            // while a rule matched it, before the rule was edited not to, would otherwise stay drawn where it was then.
            if (!hostile && !anyEntityRules)
            {
                _remembered.Forget(entity.Id);
                return;
            }

            loadedIds?.Add(entity.Id);

            // Checked before the rarity gate, not after: a monster killed while remembered has to have its icon
            // retired, and this is the only moment it can be known that it died.
            if (!entity.IsAlive)
            {
                _remembered.Forget(entity.Id);
                return;
            }

            // Rarity is a monster's own property, so it does not gate the rules that are not asking for one; those
            // would be thrown away wholesale by it.
            var monsterOk = hostile && RarityAccepted(entity.Rarity);

            if (!monsterOk && !anyEntityRules)
            {
                _remembered.Forget(entity.Id);
                return;
            }

            if (OfferToRules(entity, monsterOk, anyEntityRules, checkAlive: true) && !monsterOk)
                anyEntityMatches++;
        }

        // An entity from the whole list that the monster walk did not already offer.
        void OfferOther(Entity entity)
        {
            if (offeredIds.Contains(entity.Id) || !entity.IsValid)
                return;

            // Anything without a position cannot be drawn on the map or in the world, and asking one how far away it is
            // is what would throw. This is also the line between things that are somewhere and bookkeeping entities.
            if (!entity.HasComponent<Positioned>())
                return;

            loadedIds?.Add(entity.Id);

            // A door or a chest has no life to lose, so absence of the component means alive here rather than dead -
            // the opposite of what IsAlive would answer.
            if (IsDead(entity))
            {
                _remembered.Forget(entity.Id);
                return;
            }

            if (OfferToRules(entity, monsterOk: false, anyOk: true, checkAlive: false))
                anyEntityMatches++;
        }

        // Runs the rules against one entity and files whatever they said. Returns whether anything matched, so the
        // caller can count what the second walk is contributing.
        bool OfferToRules(Entity entity, bool monsterOk, bool anyOk, bool checkAlive)
        {
            var best = matcher.BestMatchOf(entity, monsterOk, anyOk, checkAlive);

            if (best == null)
            {
                _remembered.Forget(entity.Id);
                return false;
            }

            // Tallied when any of the rules that matched asked to be, whether or not it is the one that won the icon:
            // the tally is asking what was here, not which rule drew it.
            bool tally = best.Rule.AddToSeenTally;

            if (drawMultipleMatchingIcons)
            {
                // Slots are assigned here so drawing never has to group by entity: it just reads the position each icon
                // was given.
                var matchesOfEntity = matcher.MatchesOfEntity;

                for (var i = 0; i < matchesOfEntity.Count; i++)
                {
                    matches.Add(matchesOfEntity[i] with { Slot = i, SlotCount = matchesOfEntity.Count });
                    tally |= matchesOfEntity[i].Rule.AddToSeenTally;
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
