# StatEditor

An in-game menu for **The Spell Brigade** that lets you edit your stats during a run, and set XP and gold multipliers.

Press **F8** in game to open or close the menu.

## Requirements

- **MelonLoader 0.7.x** must be installed for The Spell Brigade. Install it from [melonwiki.xyz](https://melonwiki.xyz/) and launch the game once before adding the mod.
- Built for The Spell Brigade 1.2.0.

## What it does

- **Live stat editing.** Once you are in a run, the menu lists your wizard's stats with their current values. Each one has a slider and `-` / `+` buttons that add a bonus on top, and the change applies while you play.
- **Your Stats tab.** The 13 stats from the game's own stat panel, in the same order: Max Health, Health Regeneration, Armor, Dodge, Move Speed, Damage, Spell Size, Cast Speed, Critical Chance, Critical Damage, Mana Pull, Mana Gain and Luck.
- **Additional Modifiers tab.** Everything else the game tracks: rerolls, bans, pins, revives, projectiles, gold gain, drop chance, revive speed, healing and element multipliers, and more.
- **XP multiplier.** x1 to x10 in steps of 0.5.
- **Gold multiplier.** x1 to x10 in steps of 0.5, applied to the end-of-run gold reward and every bonus on it.
- **Vanilla by default.** Every run starts with your normal stats and both multipliers at x1. Tick "Keep stat bonuses between runs" if you want your bonuses re-applied automatically.

## Controls

| Key | Action |
| --- | --- |
| F8 | Open / close the menu |
| F9 | Cycle the menu font (while the menu is open) |

## Limits

| Stat | Largest bonus |
| --- | --- |
| Max Health | +10000 |
| Health Regeneration, Armor, Critical Damage | +500 |
| Mana Pull, Mana Gain, Revive Speed, Revive Zone Size | +1000 |
| Move Speed | +200 |
| Damage, Spell Size, Cast Speed | +100 |
| Dodge, Critical Chance, Luck | capped at 100 total |
| Drop Chance, Healing, Terrain Speed, Element Strength / Weakness | +5000 |
| Gold Gain | +50 |
| Rerolls, Bans, Pins, Revives, Projectiles | +20 |
| Everything else | +100 |

## Installation

**With a mod manager:** install through the Thunderstore app or r2modman.

**Manually:**

1. Install MelonLoader 0.7.x for The Spell Brigade and launch the game once.
2. Copy `StatEditor.dll` into `<game folder>\Mods\`.
3. Launch the game and press F8.

## Multiplayer

- The XP multiplier only takes effect when you are playing solo or hosting, because the host controls party XP.
- Stat edits apply to your own wizard only.
- Please only use this in solo runs or with friends who know you are using it.

## Configuration

Settings are saved in `<game folder>\UserData\MelonPreferences.cfg` under `[StatEditor]`. Everything there can also be changed from the menu.

## Uninstalling

Delete `StatEditor.dll` from the `Mods` folder.

## Source

[github.com/1Zoker/StatEditor](https://github.com/1Zoker/StatEditor)
