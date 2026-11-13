# MonsterModAlerter

An ExileApi plugin for Path of Exile 1 that marks monsters by their modifiers, buffs, metadata path and rarity.

## Features

- Customisable icons on the map and in the world
- The number of rare modifiers shown on the icon (optional)
- A sound when a monster with a chosen modifier or buff comes into range (optional)
- Icons that stay on the map after the monster leaves range (optional)
- An entity inspector, modelled on DevTree's, for finding modifiers and buffs to copy into a rule

![The settings with an example group open, beside the large map marking rare monsters with icons, modifier counts and labels](images/1.jpg)

![The Inspect world entities window filtered to rare monsters, one opened to show its metadata path, modifiers and buffs, beside the numbered pins in the world](images/2.jpg)

## Install

Install using **PluginUpdater**: Add > paste this plugin's URL: `https://github.com/bnc-anonymous/MonsterModAlerter` > Clone

Then tick **Enable** in the plugin's settings.

## Usage

Rules are kept in groups under **Alert rules**. A new install starts with example groups, each switched off: tick one to try it.

Rules match the game's internal names for modifiers and buffs, such as `TormentHauntedMonsterThief`, not the text shown in game. **Inspect world entities**, at the top of the settings, lists them for the monsters around you.

## License

MIT
