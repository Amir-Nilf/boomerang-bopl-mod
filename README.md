# Boomerang

A Bopl Battle ability. Throw it, and it comes back.


<img width="800" height="450" alt="ezgif-86b9b5e476a7600d" src="https://github.com/user-attachments/assets/cd416ed9-ab30-4331-b7da-4d4d0ec6c950" />


Hold to wind up, release to throw. The boomerang flies out, hangs in the air spinning for a
moment, then snaps back to you. Anyone it touches is cut down, and anything explosive
it passes goes off. It never hurts the player who threw it.

On the way out it stops at walls and spins there. On the way back it passes straight through
platforms, so it always finds you. If it can't reach you within 3 seconds it blinks and
gives up. Scales with bopl's size.

- **Cooldown:** 2.45 seconds
- **Range:** a light throw travels about a quarter as far as a full one
- **Hang time:** 1.5 seconds at the far end
- **Dependencies:** BepInEx only

## Installing

Install with Thunderstore Mod Manager or r2modman, then launch the game from the mod manager.
The ability appears in the ability-select grid.

By hand: drop `Boomerang.dll` into `BepInEx/plugins/Boomerang/`.

## Playing online

Private lobbies work; invite a friend through Steam as usual. **Both players need the same
mods installed**, or the ability lists won't line up. Thunderstore can export your whole
profile as a code for them to import.

Public matchmaking is disabled by the game whenever any mod is loaded. That's Bopl's rule, not
this mod's.

## Building from source

Requires the .NET SDK and a Steam copy of Bopl Battle.

```bash
dotnet build -c Release
```

The paths to the game and to your mod profile are set at the top of `Boomerang.csproj` — change
them to match your machine. **Close the game before building**; Windows locks the DLL while it
is loaded.

## License

MIT
