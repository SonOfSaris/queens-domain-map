# Queen's Domain Map

An interactive 3D map of the whole world of Queen's Domain that runs in your browser: every item pickup, secret wall,
boss, NPC, save point, teleporter, locked door and inscription as a clickable guide, ticked off from your own save file.

Unofficial and fan-made; not affiliated with the game's developers or publisher. **No game content is in this
repository or in the download.** The map is built on your PC from your own copy of the game.

## Getting it

You need Windows 10 or 11 and the game installed.

1. Download `QueensDomainMap-…-win-x64.zip` from the
   [Releases page](https://github.com/SonOfSaris/queens-domain-map/releases/latest) and unpack it into a folder of
   its own.
2. Double-click `QueensDomainMap.exe`. Windows may show "Windows protected your PC" the first time, because the
   program is not signed: choose *More info*, then *Run anyway*.
3. Your browser opens the map. On the first start it asks to build the map: this reads the game's files, takes about a
   minute and about 600 MB of disk space in a `data` folder beside the program. Nothing in the game folder is changed.

A Steam install is found by itself. If the game is not found, choose *Game folder…* and pick the folder that holds
`QueensDomain.exe`.

While the map runs, its icon sits next to the clock (possibly behind the small arrow there): click it to open the map
again, right-click it to quit. Starting the program a second time just opens the map. *View → Map program → Quit* on
the page stops it too.

Everything stays on your PC. The program serves the page to your own browser only (`http://localhost:8765`), and it
reads your save files without ever writing to them. To remove it, quit it and delete its folder.

## Using it

| Input | Action |
| --- | --- |
| Left drag | Rotate around the pivot (the small yellow dot) |
| Right drag, or Shift + drag | Pan |
| Mouse wheel | Zoom |
| W A S D, Q / E | Move, down / up (hold Shift to go faster) |
| Double-click a surface | Put the pivot there |
| Tab | Switch between the orbit view and first person at the dot |
| Click a marker | Show its details |
| F / M | Fly to / mark the selected marker as found |
| P | Fly to your character (when a save is loaded) |
| X / C / H | Markers through walls / cutaway / hide the side panel |
| `/` | Jump to the search box |

**First person** (Tab, the button at the bottom right, or Y on a controller) puts the camera on the yellow dot at eye
height, facing the way you were looking. Walk with W A S D, look by dragging. Switching back pulls the camera out to
the distance and tilt it had before, around wherever you walked to.

**Controller** (Xbox layout; press any button once so the browser activates it):

| Input | Action |
| --- | --- |
| Left stick | Move (click the stick to go fast) |
| Right stick | Look / rotate |
| LB / RB | Down / up |
| LT / RT | Zoom out / in — in first person: slower / faster |
| A / B | Select the marker under the crosshair / deselect |
| X / Y | Mark found / orbit view ⇄ first person |
| D-pad left / right | Previous / next marker of the same kind |
| D-pad up / down | Fly to your character / cutaway |
| Start / Back | Hide the side panel / markers through walls |

- **Guide tab** — tick categories on and off, expand one (▸) to list everything in it, click an entry to fly to the next
  one you have not found. Searching matches item, enemy, NPC and place names. "Pickups:" filters item pickups by the
  placement tag the designers gave them (*hidden*, *out of view*, *accessible later*), to those inside containers or
  to those an enemy carries.
  The Save box at the top loads a save slot (see "Your save file" below).
  "Not on the map" lists every item that is not a world pickup, grouped by how you get it: Witch's Grimoire
  inscriptions, boss and enemy rewards, NPC gifts, shops, weapon evolutions, drops, quests and challenges. Click one
  for the details, a **Go** button to the boss, NPC, shop or base weapon, and — for keys — where they are used.
- **Zones tab** — show or hide each level of the world, click a name to fly there, ◎ to show only that zone.
- **View tab** — the **cutaway** hides everything above a height, which is the easy way to look into rooms and
  dungeons from above ("Follow pivot" keeps the cut just above wherever you are looking). Also marker range and size,
  textures, water and field of view.

Found marks you set by hand are stored in your browser for `localhost:8765`. Use *View → Progress → Export / Import* to
back them up or move them to another browser.

## Your save file

The map reads the game's save slots (it never writes to them) and ticks off what you already have. The **Save**
box at the top of the Guide tab lists the slots it found — "Slot 1" is `World0.sav` — plus the game's own backups.
The slot you played most recently is loaded when the map starts, and it is re-read by itself a few seconds after the
game saves again.

- Everything the save has dealt with counts as found: pickups taken, chests and secret walls opened, doors unlocked,
  bosses and one-time enemies defeated, save points and teleporters activated, NPCs met, notes read. The first time
  a save loads, **Hide found** is switched on, so only what is left stays on the map. That also holds when you click
  an entry in the list: only the ones still to get light up, and the ‹ › buttons on the card step through those
  ("2 / 4 left").
- "Not on the map" items you own (or once owned) get a ✓, and item cards say how many you carry.
- **◉ You** (or P) flies to the green ▲ marker: the place and facing the save was written at. The game only stores
  this when it saves, so it is where you last saved rather than a live position. *Stand here* on its card looks
  through your character's eyes.
- *Loot on the ground (save)* — off by default — shows drops the save still remembers lying around.
- Marks you set by hand (M) still work for things a save cannot know about (wall hints, a few inscriptions).

A single save from anywhere: pick *Open a save file…* in the Save box, or drop a `World<N>.sav` on the page.

### Game and save folders

Left alone, the program looks by itself: for the game around its own folder and in your Steam libraries, and for
saves in the game's `Saved\SaveGames` folder and in `%LOCALAPPDATA%\QueensDomain\Saved\SaveGames`. When the game is
somewhere else, or saves from several installs or accounts show up as "profile 1", "profile 2" and you want just one
of them, say where things are: **Save box → Game and save folders…** (also under *View → Folders*).

- **Game folder** — the one with `QueensDomain.exe`. The map is built from it, and that install's saves are looked
  for beside it.
- **Save folder** — the folder with `World0.sav` in it, or a folder above it (`Saved`, `SaveGames`). Once it is set,
  saves are read from there and nowhere else. To see one account only, pick that account's folder inside `SaveGames`.

Type or paste a path, or use **Browse…**, which opens the Windows folder window; its *Folder:* field takes a pasted
path too. The line under each field says what was found while you type. An empty field means "look by itself".
**Save** stores both in `folders.json` beside the program; delete that file to start over. A game folder without the
game in it, or a folder that does not exist, is not accepted.

## After a game update

The map notices that the game's files are no longer the ones it was built from and shows a notice above the search
box with a **Rebuild…** button. *View → Map program → Rebuild the map…* does the same at any time. A rebuild takes
about a minute and replaces the `data` folder; found marks are kept, because marker ids are `level/actor name` and
survive unless the developers rename an actor.

Mods count as a change too: the map is built from all pak files in the game's `Paks` folder, so a mod that changes
items or levels shows up in the map. Choose *Later* on the notice to keep the map as it is.

## What is on the map

Numbers from the November 2026 build of the game:

| | Count |
| --- | --- |
| Zones (streamed levels) | 39 |
| Triangles of world geometry | about 14 million |
| Item pickups | 1,614 (177 key items, 90 weapons, 19 accessories, 115 upgrades, 93 treasures, 690 consumables, 429 coin pickups, 1 artifact) |
| Secret walls and hidden doors | 124 |
| Bosses and minibosses | 27 |
| Enemy spawn points (off by default) | 1,684 |
| NPCs / save points / teleporters | 40 / 32 / 14 |
| Locked doors / key slots | 54 / 49 |
| Inscriptions and notes (with their full text) | 81 |
| Chests, levers, wall hints (off by default) | 105 / 177 / 51 |

46 of the 93 treasures do not lie around: an enemy that never comes back carries them. Those are the loot sacks of the
36 Star Lizards, the two big Star Lizards' statuettes, the Orbewa guards' silver jewellery, the gargoyle's monocle, the
two crowned slimes and what the two treasure mimics hold. They sit on the map as Treasure markers at the enemy's spawn
point ("Carried by: …") and are ticked off once your save has that enemy defeated. Two more treasures have no place of
their own (Erma's Gold Fish Treasure and the Jar Club's evolution, the Symbol of Jigeki); they stay under
"Not on the map". The game's own "Collect every Treasure" challenge wants 93 of these 95, sold ones included.

Item names, descriptions and icons, enemy names and stats, shop prices and drop chances are read from the game data.
Zone names are the in-game region names where a save point or teleporter provides one; other levels show their
internal name spelled out (for example `710_SZ_AQ` → "Secret Zone · Aqua Ruins").

## Gear stats

A second page compares every weapon (melee, guns, throwing) and accessory, Witch's Grimoire items included: damage per
hit and per second at any player level, at weapon level 1, at the level cap or upgraded, plus damage types, reach,
specials and where to get each one. Open it from the program's icon next to the clock (*Gear stats*) or at
`http://localhost:8765/stats.html`. "How the numbers work" at the bottom of the page explains the formulas.

Its numbers are read from your game's files when the map is built, with one exception: a stats asset only stores
values that differ from defaults, and those defaults live in the game's program rather than its data. The page
carries the defaults of the 14 Nov 2026 build (`DEFAULTS` in `stats.html`); they need a re-check if an update
rebalances weapons across the board.

## Known limits

- Lighting is a plain sky light — the game's own lights, fog, skies and particle effects are not reproduced.
- Only static meshes are drawn. Characters, animated props and anything a blueprint spawns at runtime are missing;
  enemies and NPCs appear as markers at their spawn points instead.
- Materials are reduced to their main texture; blended or animated materials look simpler than in the game.
- Zones overlap vertically (islands above, underworlds below). Use the Zones tab or the cutaway to separate them.
  The two "intro cutscene" sets are off by default because they re-use pieces of the world at other coordinates.
- Item sources are traced through the game data: level placements, shop stock, NPC reward lists, the reward NPCs that
  bosses spawn when defeated, dialogue give-actions, weapon evolutions, drop tables, the game's quest-reward table and
  the Witch's Grimoire. The Grimoire's 33 inscriptions are stored only as hashes, so the map shows which 38 items they
  unlock but not the words themselves. A few sources only exist in blueprint script; those are worded as such
  ("likely a scripted event in …", "item description: …"). Four sources are not in the data at all and were
  supplied by a player: Umbada's Trading Outpost Key (after spending 1,000 coins in his shop), the two "secret"
  Akunra gems (forging the final sword) and the Mystical Nut of the White Tree (the hidden White Tree Guardian).
- Some enemies only spawn while a save flag has a certain value (a wall destroyed, a boss still alive). Their cards
  say which flag, and whether your save meets it; the flag names are the game's internal ones. One of them can never
  be met: one of the three Learned Star Lizards in the Tower of Knowledge waits for `NecroWall2Destroyed`, while the wall
  beside it sets `NecroWall02Destroyed`. In the 14 Nov 2026 build that lizard and its Moldy Loot Sack do not appear,
  and the map marks them "Never appears (game bug)". The exporter keeps a short hand-checked list of such flags
  (`NeverSetFlags` in `tools\Exporter\Markers.cs`).
- Save tracking follows the ids the game itself stores. Not covered: wall hints, points of interest, five
  inscriptions without a note id, and levers without a saved state. Enemies that respawn are never ticked off; their
  cards show how often you beat them instead. A boss the game tracks only by a quest flag is ticked off from the
  kill counter, which works when that boss exists once in the world.
- Windows only. The game's files are read with a version of CUE4Parse pinned for Unreal Engine 4.27; a game update
  that changes the engine or renames things the exporter looks for needs a new version of this program.

## Command line

`QueensDomainMap.exe` takes a few options, for a shortcut or a console:

| Option | |
| --- | --- |
| `--port=<number>` | Serve on another port than 8765. Found marks are kept per port. |
| `--home=<folder>` | Where `data` and `folders.json` are kept (default: beside the program). |
| `--game=<folder>`, `--saves=<folder>` | Game and save folder for this run only. |
| `--no-open` | Do not open the browser. |
| `--no-tray` | No icon next to the clock; stop it from the page. |
| `--web=<folder>` | Serve the page's files from a folder instead of the built-in copy (for working on the page). |

## For developers

### Layout

- `index.html`, `app.js`, `style.css` — the map page ([three.js](https://threejs.org) r169 in `lib\`)
- `gvas.js`, `save.js` — reader for the game's save files and the rules that match a save to the map's markers
- `stats.html` — the gear stats page
- `tools\Exporter\` — the program (C#, .NET 10). `App.cs` is the map itself: local web server, folder and save
  handling, the build of the map data, `Win.cs` the Windows parts (tray icon, folder window, Steam lookup). The rest
  is the exporter that turns the game's pak files into the `data` folder: `WorldExporter.cs` (levels and geometry),
  `Materials.cs` (textures), `Markers.cs` and `Sources.cs` (markers, items and where they come from), `Stats.cs`
  (gear stats), on top of [CUE4Parse](https://github.com/FabianFG/CUE4Parse).
- `tools\build.ps1` — builds `QueensDomainMap.exe` and the release zip
- `tools\kismet.mjs` — prints dumped blueprint bytecode as pseudo-code (Node.js)

The page's files are embedded in the program at build time. What the program writes at run time:
`data\world.json`, `data\geo\*.bin`, `data\tex\*.png` (zones, baked geometry, textures), `data\markers.json`,
`data\icons.png` (markers, item and enemy tables, item icons), `data\stats-src.json` (gear stats), `data\build.json`
(which game files the data was built from), `data\_survey\*.tsv` and `folders.json`.

### Building

```
powershell -ExecutionPolicy Bypass -File tools\build.ps1
```

writes `dist\QueensDomainMap\QueensDomainMap.exe` and `dist\QueensDomainMap-<version>-win-x64.zip`. It needs git; on
first run it downloads the .NET 10 SDK (unless one is installed) and CUE4Parse at the pinned commit into `tools\_deps`,
about 1 GB, nothing system-wide. The program is one self-contained file: players need no .NET install.

`tools\build.ps1 -Dev` makes a quick console build, `tools\_deps\bin\QDExport.exe`, for working on the program.
Run it with `--web=<this folder> --home=<a test folder> --port=8766` and page edits show on reload.

### Looking into the game's data

With a Paks folder and a command, the program is the exporter and a set of research tools instead of the map (the
release exe has no console window of its own, but writes to the console it is started from):

```
QueensDomainMap.exe <Paks folder> export <out folder>
```

builds the data by hand; `--levels=a,b`, `--no-geo` and `--no-tex` shorten a run. `data\_survey\*.tsv` lists what the
export found (actor classes, materials, meshes), which is the place to look if an update adds content the map does not
show. The export log ends with a `sources:` line naming any item it could not trace. To investigate one:
`whoref` (which packages reference each item), `graph` (the full package reference graph), `names <text>` (search
every package's name table), `grep <level or *> <text>` and `grepall <package> <text>` (search a level's actors or a
package's exports), `rawgrep <text>` (count a text in the unpacked bytes of every package), `probe`, and
`stats <out folder>` (only the gear stats data). To read what a blueprint actually does,
`script <package> <out.json>` (or `scriptdir <content folder> <out folder>`) dumps it together with its compiled
script, and `node tools\kismet.mjs <out.json> [function] [--grep=text]` prints that script as readable pseudo-code.

## Licence

The program and its source code are under the [MIT licence](LICENSE). That covers this project's own code only: not
the game, and not the map data the program builds from it.

## Credits

- [CUE4Parse](https://github.com/FabianFG/CUE4Parse) by FabianFG and contributors (Apache License 2.0) reads the
  game's files.
- [three.js](https://threejs.org) (MIT licence) draws the map.
- The release contains the .NET runtime (MIT licence) and the packages listed in its `THIRD-PARTY-NOTICES.txt`.

Queen's Domain and everything in it belong to its developers and publisher. The `data` folder the program creates
contains content taken from your copy of the game: it is for your own use with the game you own, so please do not
share or upload it.
