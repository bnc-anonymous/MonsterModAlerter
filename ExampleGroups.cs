using System.Collections.Generic;
using ExileCore.Shared.Enums;

namespace MonsterModAlerter;

/// <summary>
/// The alert groups a new install starts with, and the ones a reset leaves: worked examples of what a rule can do. Each
/// group starts switched off, its rules switched on, so nothing is drawn until a group is ticked. Only the settings
/// that differ from a new rule's are given.
/// </summary>
internal static class ExampleGroups
{
    /// <summary>A new list of new groups each call, so no two settings objects share one.</summary>
    public static List<AlertGroup> Create() =>
    [
        Group("Example: Gold farm Allflame Ember of the Gilded",
            new AlertRule
            {
                Icon = MapIconsIndex.SanctumGoldConvert,
                SearchMods = { Value = false },
                SearchBuffs = { Value = false },
                MetadataPaths = { Value = "Metadata/Monsters/VaalForgeMan/VaalForgeMan" },
                Priority = { Value = 1 },
                UniqueMonsters = { Value = false },
                Label = { Value = "Gold" },
                ShowModCount = { Value = true },
                DrawModCountInWorld = { Value = true },
            }),
        Group("Example: Dangerous tormented spirit modifiers blue skull with warning sound",
            new AlertRule
            {
                Icon = MapIconsIndex.CorpseTypeUndead,
                SearchTerm = { Value = "TormentHauntedMonsterThief" },
                Priority = { Value = 50 },
                DrawModCountInWorld = { Value = true },
                PlaySound = { Value = "danger" },
            }),
        Group("Example: Rares with large yellow icons, number of rare modifiers, and world text",
            new AlertRule
            {
                Icon = MapIconsIndex.LootFilterLargeYellowCircle,
                SearchMods = { Value = false },
                SearchBuffs = { Value = false },
                MetadataPaths = { Value = "Metadata/Monster" },
                UniqueMonsters = { Value = false },
                Label = { Value = "RareMonster" },
                ShowModCount = { Value = true },
                ModCountOffsetY = { Value = -2 },
            }),
        Group("Example: Rares with dangerous modifiers, large red icons, number of rare modifiers, and world text",
            DangerousRare("MonsterModRemoveFlaskChargeAura"),
            DangerousRare("proximity_shield_aura")),
        Group("Example: Critters",
            new AlertRule
            {
                Icon = MapIconsIndex.MissionAlly,
                MetadataPaths = { Value = "Metadata/Critters" },
                AnyEntity = { Value = true },
                NormalMonsters = { Value = true },
                MagicMonsters = { Value = true },
                MapIconSize = { Value = 25 },
                ShowInWorld = { Value = false },
                DrawLabel = { Value = false },
                DrawModCountInWorld = { Value = true },
                AddToSeenTally = { Value = true },
            }),
        Group("Example: Fishing",
            new AlertRule
            {
                Icon = MapIconsIndex.LootFilterLargeWhiteUpsideDownHouse,
                MetadataPaths = { Value = "albino" },
                AnyEntity = { Value = true },
                NormalMonsters = { Value = true },
                MagicMonsters = { Value = true },
                DrawWorldIcon = { Value = true },
                DrawLabel = { Value = false },
                DrawModCountInWorld = { Value = true },
            }),
        Group("Example: Divination touched",
            new AlertRule
            {
                Icon = MapIconsIndex.RewardDivinationCards,
                SearchTerm = { Value = "div_card_touched_visual" },
                Priority = { Value = 1 },
                NormalMonsters = { Value = true },
                MagicMonsters = { Value = true },
                MapIconSize = { Value = 16 },
                ShowInWorld = { Value = false },
                DrawWorldIcon = { Value = true },
                DrawLabel = { Value = false },
                DrawModCountInWorld = { Value = true },
            }),
    ];

    private static AlertGroup Group(string name, params AlertRule[] rules)
    {
        var group = new AlertGroup { Enabled = { Value = false }, Name = { Value = name } };
        group.Rules.Content.AddRange(rules);
        return group;
    }

    /// <summary>The two rules of the dangerous rares example, which differ only in the modifier searched for.</summary>
    private static AlertRule DangerousRare(string searchTerm) => new()
    {
        Icon = MapIconsIndex.LootFilterLargeRedCircle,
        SearchTerm = { Value = searchTerm },
        MetadataPaths = { Value = "Metadata/Monster" },
        Priority = { Value = 1 },
        UniqueMonsters = { Value = false },
        Label = { Value = "DangerousRareMonster" },
        ShowModCount = { Value = true },
        ModCountOffsetY = { Value = -2 },
    };
}
