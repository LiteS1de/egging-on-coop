<p align="center"><a href="README.md">Русский</a> · <b>English</b></p>

# Egging On Co-op

A co-op mod for [Egging On](https://store.steampowered.com/app/2897610/Egging_On/) for 2–4 players: your friends' eggs in your world,
a shared world (objects, bottles, platforms, events, drone pads), teleport to a friend, emotes and pings, a shame board.
Connect with an invite code, through Radmin VPN or by IP.

> The mod's in-game text is in Russian for now.

## Install

1. Download `EggCoop-<version>.zip` from [Releases](../../releases).
2. Unpack it anywhere and run **`Установить.bat`** ("Install").
   The script finds the game in your Steam libraries (or asks for the folder with `Egging On.exe`), installs the mod loader
   [BepInEx 5.4.23.5](https://github.com/BepInEx/BepInEx) — it is already in the zip — and the mod itself.
   Your settings and other mods are left alone.
3. Press **F9** in the game.

To update, run `Установить.bat` from the new zip. To remove, run `Удалить.bat` ("Uninstall").
All players need the same mod version. More details are in `Инструкция.txt` inside the zip (Russian).

## Controls

| Key | What it does |
|---|---|
| F9 | co-op window: host / join, host settings, players, shame board, key bindings |
| T | teleport to a friend (with 2+ friends, pick one with a number key) |
| G (hold) | emote wheel: hover an emote and release, the × in the centre closes it |
| mouse wheel click | ping where the camera is looking |

## Building from source

You need the .NET SDK 8+ and the game with BepInEx 5 installed (the build references its `Assembly-CSharp.dll` and others;
no game files are in this repository).

```bash
dotnet build src/EggCoop -c Release -p:GameDir="C:/Program Files (x86)/Steam/steamapps/common/Egging On"
dotnet run --project src/SelfTest
```

`tools/lab.ps1 pack` builds the zip for players (`dist/EggCoop-<version>.zip`; BepInEx is downloaded and checked by SHA-256).
`tools/lab.ps1 up` starts two copies of the game on one PC for testing (`lab/p1`, `lab/p2`, `-c Lab`); `NOTES.md` is the log of
decisions and test results.

## Licenses

- BepInEx 5.4.23.5 — MIT, shipped unchanged in the zip (`licenses/BepInEx-LICENSE.txt`).
- Emote images — [Twemoji](https://github.com/jdecked/twemoji) 15.1, CC-BY 4.0.
- LiteNetLib (MIT), Mono.Nat (MIT) — the mod's networking libraries.
- Egging On belongs to its authors; the mod contains no game files.
