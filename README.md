# Valheim Process Progress

BepInEx mod for [Valheim](https://www.valheimgame.com/) that shows a small
progress bar above things that take time, so you can see at a glance how long
is left - without guessing or checking each one.

> **Unofficial mod.** This is a fan-made mod, not affiliated with or endorsed by
> Iron Gate. It marks your game as modded (the game shows this in the main menu),
> as Iron Gate asks mod authors to do.

## What gets a bar

Anything within range of you (10 m by default):

- **Growing plants** - crops and tree saplings: percent grown and time left
- **Picked plants that regrow** - berry bushes, mushrooms, herbs: time until
  they're ready to pick again
- **Fermenters** - fermentation progress and time left; nothing is shown for
  an empty fermenter
- **Beehives** - honey stored (e.g. `2/4`) and time until the next one

Finished things show a gold **Ready** bar. When something is stuck, the bar
turns red and shows the game's own reason, for example a plant with no sun
or in the wrong biome, a fermenter that needs a roof (without one the game
keeps resetting fermentation), or beehives that need more open space.

Times come from the game's own calculations, so the bar always matches what
will actually happen. The bars sit with the game's enemy health bars: they
hide together with the HUD and follow the game's UI scale setting.

## Settings

In `BepInEx\config\com.michal.valheim.processprogress.cfg`:

- `Range` - how far from you bars are shown, 3 to 40 m (default 10)
- `ShowPlants`, `ShowPickables`, `ShowFermenters`, `ShowBeehives` - turn
  each kind on or off

## Requirements

- Valheim (tested on 1.0.15)
- [BepInEx](https://valheim.thunderstore.io/package/denikson/BepInExPack_Valheim/) 5.4.x

## Installation (players)

1. Install BepInEx for Valheim if you haven't already (see link above, or
   use [r2modman](https://valheim.thunderstore.io/package/ebkr/r2modman/)).
2. Download `ProcessProgress.dll` from the
   [latest release](../../releases/latest).
3. Drop it into `<Valheim install folder>\BepInEx\plugins\ProcessProgress\`.
4. Launch the game and walk up to a growing plant, a fermenter or a beehive.

## Building from source

Requires the [.NET 8 SDK](https://dotnet.microsoft.com/download) (or newer)
and a local Valheim install with BepInEx installed.

```bash
git clone https://github.com/Ab5oluteZer0/valheim-process-progress.git
cd valheim-process-progress
dotnet build -c Release -p:ValheimPath="C:\Path\To\Valheim"
```

If you don't pass `-p:ValheimPath`, the build looks for a `VALHEIM_PATH`
environment variable, then falls back to the default Steam location
(`C:\Program Files (x86)\Steam\steamapps\common\Valheim`).

The build automatically copies the built DLL into
`<Valheim>\BepInEx\plugins\ProcessProgress\` for quick in-game testing.

## Notes on how it works (and a few gotchas found along the way)

- Each kind of object is one small `IProgressProvider` class, so adding a new
  kind means adding one class - scanning and bars don't change.
- Growth and fermentation times are read through the game's own private
  methods (`Plant.GetGrowTime`, `Plant.TimeSincePlanted`,
  `Fermenter.GetFermentationTime`) rather than re-implemented.
- After you tap a fermenter, the game "resets" its start time by writing an
  `int` 0 but reads it back as a `long`, so the old time stays behind and an
  empty fermenter looks finished. Emptiness is checked by its content, like
  the game itself does.
- The bar height comes from the visible model, not from colliders - a
  fermenter has invisible volumes reaching far above the barrel.
- The area around you is scanned once a second, values are refreshed twice a
  second, and bars are reused from a pool, so it stays cheap even in a busy
  farm.

## License

MIT - see [LICENSE](LICENSE).
