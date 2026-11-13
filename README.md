# MonsterModAlerter

An ExileApi plugin for Path of Exile 1: marks monsters by their modifiers and buffs, with an icon on the map and in the world, and a sound.

## Install

Install using **PluginUpdater**: Add > paste this plugin's URL: `https://github.com/bnc-anonymous/MonsterModAlerter` > Clone

Then tick **Enable** in the plugin's settings.

## Usage

The plugin works from the list of **Alert rules** in its settings. A new install starts with one example rule.

Each rule has a **Search term**, matched against the names of a monster's modifiers, its buffs, or both, as the rule's **Search mods** and **Search buffs** boxes say, ignoring case. These are the game's internal names, such as `TormentHauntedMonsterThief` (a modifier) or `torment_cutthroat` (a buff), not the text shown in game.

- **Several terms**, separated by commas, must all be present: `mod1,mod2` marks only monsters carrying both.
- **Use regex** treats each term as a regular expression: `MonsterMod.*Touched` matches every Touched modifier.
- **Metadata paths** match the monster's metadata path, such as `Metadata/Monsters/BeehiveBehemoth`. A rule with metadata paths and no search term marks every monster on those paths; **Exclude metadata paths** marks every monster except those.
- **Monster rarity** sets which rarities of monster the rule matches, Normal, Magic, Rare and Unique: rare and unique by default.

Each rule sets its own icon, tint and priority, its map icon, its world icon, box and label with their sizes and colours, and its sound. A rule's **Play sound** picks a sound from the HUD's `Sounds` folder, or None; the **Sound** section sets the volume and the minimum time between sounds, or disables them all. A rule's settings that depend on a switch show only while it is on.

## More per rule

- **Show mod count** draws how many rare modifiers the monster has, or with **Count search matches**, how many of its modifiers match the search term. **Mod count minimum** skips monsters below that count.
- **Remember position** keeps the icon on the map after the monster leaves range, until it is seen dead, you come near the spot and it is gone, or you change area.
- **Add to seen tally** adds what the rule matches to an on-screen tally for the current area, switched on under **Seen tally**.
- **Match any entity** tests the rule against everything loaded, not only hostile monsters: critters, chests, shrines. The metadata path is searched as well, so `Metadata/Critters` marks every critter.

## Finding modifier and buff names

Turn on **Debug > Show overlay**, and **Show mod count** on a rule that matches the monster: the overlay lists the modifiers and the buffs of one monster that rule matched, so you can tell which a name is, and which modifiers were counted.

## License

MIT
