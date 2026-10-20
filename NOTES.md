# Egging On — co-op mod notes

## Identity (observed 2026-10-20)
- Install: `D:\Games\Egging On` (Steam AppID 2897610)
- Engine: Unity 2022.3.62f2, Mono backend, URP
- Assembly-CSharp.dll SHA-256: 9F8F7E57EC088FBAD64543874ED0A38DDB5CFE4C7969FDAE69364B558768C520
- Egging On.exe SHA-256: 071575C328C68B87C5BF8267960BDDA6E7012BFE566A21A15FABDDDB8E665E43
- Company/product (app.info): Egobounds / Egging On → saves in `%USERPROFILE%\AppData\LocalLow\Egobounds\Egging On` (unverified)
- No anti-cheat. No loader installed. No existing co-op mod found (searched: web, 2026-10-20).
- Decompiled to `decomp/` (ilspycmd 11.1.0.9782). Do not ship anything from it.

## How the game works (for co-op)
- One continuous world: `ClimbBase` + additive biome scenes (Henhouse, Farm, Plant, Shop, Kitchen, Attic). No level transitions.
- Player = `EggController` (+subclasses Bomb/Rocket/Timer/Voxel/Wheel/Easy/Spaceship) + `EggCoreData` (Rigidbody, collider, Rewired `input`).
- Crack → `FullCrack` → yolk state (`RawEggController`, soft-body joints) → auto-rebuild via `SetRebuildPosition`. Shell shatter = RayFire.
- Singletons everywhere: `World.instance.GetEgg()/GetEggCore()`, `Persistent.instance.saveData`, `Boot.instance`, `ReInput.players.GetPlayer(0)`.
  → remote players must be puppets (visual clone), not a second EggController.
- Save: `SaveData` struct (player RigidData, progress main/sub, brokenObjects, rigidDatas, skin id, stamps...).
- Dev console (QFSW Quantum Console) enabled by launch arg `dev`: `tp`, `fly`, `freecam`, `crack`, `rebuild`, `save`, `load`, `snap`.
- Camera: Cinemachine vcam `World.view` follows local egg only.

## Plan direction
- BepInEx 5.4.23.x (proven on Unity 2022.3.62f2 Mono) + HarmonyX + LiteNetLib (direct IP or a VPN overlay such as Radmin).
- Each player owns/simulates own egg; remote egg = interpolated puppet. Shared world sync only later, host-authoritative.

## Networking decision (2026-10-20, user: 2–4 players, no VPN preferred)
Model after github.com/Karimov-Akbar/sa-coop (src/common/api/nat-traversal, src/server/shared/api/turn-relay):
1. LAN discovery broadcast (LiteNetLib built-in).
2. UPnP port map on host (Mono.Nat or hand-rolled SSDP+SOAP).
3. STUN → host public ip:port → invite code `XXXX-XXXX-XXXX` (base32: ver, IPv4, port, key).
4. No UPnP → UDP hole punch, signalling via ntfy.sh topic derived from the code.
5. Symmetric NAT / CGNAT on both sides → TURN relay (metered.ca free or own), opt-in in config.
Star topology: host relays between guests. Radmin/VPN = always-works fallback (direct IP field stays).

## References
- vendor/coop-anything: playbook/how-coop-mods-work.md, networking.md, multi-instance-testing.md, games/tainted-grail-the-fall-of-avalon/NOTES.md
- vendor/universal-modder: skills/mod-any-game/references/engines/unity.md, knowledge/games/tunic/seamless-coop-bepinex6-native-hooks.md
