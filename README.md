# MonsterModAlerter

An ExileApi plugin for Path of Exile 1: marks monsters by their modifiers and buffs, with an icon on the minimap and in the world, and a sound.

## Install

Install using **PluginUpdater**: Add > paste this plugin's URL: `https://github.com/bnc-anonymous/MonsterModAlerter` > Clone

## Usage

The plugin works from a list of **Alert Rules** in its settings. A new install starts with one example rule.

Each rule has a search term, matched against the names of a monster's modifiers and buffs, ignoring case. These are the game's internal names, such as `TormentHauntedMonsterThief` or `torment_cutthroat`, not the text shown in game.

- **Several terms**, separated by commas, must all be present: `mod1,mod2` marks only monsters carrying both.
- **Wildcards (regex)** treats each term as a regular expression: `MonsterMod.*Touched` matches every Touched modifier.
- **Monster Filter** matches the monster's metadata path, such as `Metadata/Monsters/BeehiveBehemoth`. A rule with a filter and no search term marks every monster on that path.
- **Rarity Filter** narrows the global rarity filter for one rule.

Each rule sets its own icon, minimap and world icon sizes, tint, priority and sound. Sounds are files in the HUD's `Sounds` folder, named without the extension.

## More per rule

- **Show Rare Modifier Count** draws how many rare modifiers the monster has, or with **Count Search Term Matches Instead**, how many of its modifiers match the search term. **Count Minimum** skips monsters below that count.
- **Remember Position** keeps the icon on the map after the monster leaves range, until it is seen dead, you come near the spot and it is gone, or you change area.
- **Count In Seen This Map** adds what the rule matches to an on-screen tally for the current area, switched on under **Seen This Map Counter**.
- **Match Any Entity** tests the rule against everything loaded, not only hostile monsters: critters, chests, shrines. The metadata path is searched as well, so `Metadata/Critters` marks every critter.

## Finding modifier names

Turn on **Debug Overlay**, and **Show Rare Modifier Count** on a rule that matches the monster: the overlay lists the modifier entries of one monster that rule matched, and which of them were counted.

## License

MIT
