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

## Stage 1 results (2026-10-21) — verified on one PC, two lab copies
- Game version on title screen: 0.1.8654.
- PASS: direct IP join, invite-code join (resolved via LAN discovery), ghost follows: lag 140 ms, mean error 0.01 m, worst 0.13 m (tools/compare_probe.py).
- NOT verified: real internet join (STUN + ntfy hole punch) — needs a friend on another network; F9 menu by hand; Rewired keyboard block while typing; skin swap on ghost with a different skin.
- Lessons:
  - The game destroys BepInEx's manager object → `HideManagerGameObject = true` in BepInEx.cfg (shipped in the zip).
  - Editing app.info only moves Player.log; settings.prefs/controls.bin/saves still go to the real LocalLow folder.
    Real isolation = Harmony postfix on `Application.get_persistentDataPath` (`-coop-data <dir>`), fail-closed kill.
  - Russian locale writes floats with commas: use invariant formatting in any text output.
  - The owner's PC runs a VPN: STUN address ≠ router WAN address. Code prefers the UPnP address and warns.
  - `dev` launch arg skips saving and starts at a dev marker; good for lab runs.

## Bad-network sweep (2026-10-21, tools/net_sweep.py, coop-anything chaos relay host←→guest)
Moving-egg samples only; error = ghost vs real egg at best-fit lag.

| profile | delay 50 | delay 100 | delay 200 | delay 300 |
|---|---|---|---|---|
| lan | 2.2 cm / p95 7 / freeze 4% | 2.2 / 7 / 3% | 1.9 / 7 / 6% | 1.8 / 6 / 3% |
| good_wifi | 2.2 / 8 / 2% | 1.8 / 6 / 2% | 1.7 / 6 / 1% | 1.4 / 4 / 1% |
| bad_wifi | 2.3 / 10 / 3% | 2.0 / 7 / 5% | 1.9 / 5 / 4% | 2.5 / 8 / 3% |
| mobile_tether | 3.4 / 12 / 2% | 2.8 / 9 / 1% | 3.3 / 11 / 6% | 2.5 / 9 / 0% |
| transatlantic | 2.3 / 10 / 2% | 1.7 / 7 / 2% | 1.6 / 5 / 3% | 1.4 / 5 / 3% |
| hell | 7.2 / 24 / 39% | 6.2 / 22 / 18% | 4.8 / 17 / 9% | 8.5 / 28 / 9% |

Decision: default GhostDelayMs = 100 (realistic links all ≤3 cm mean, ≤2–5% freeze). 200 for very bad links (config).
Receive-time stamping is good enough; sender timestamps + clock offset not needed (revisit only if real play shows judder).
Lesson: the game auto-pauses after 5 min of no buttons while the egg rests (EggController.afkTimeout) — lab build resets it.

## Saves with the mod (2026-10-21, lab p1 without `dev`)
- PASS: played 60 s, closed the window normally (WM_CLOSE = Alt+F4 / "Save & Quit"), relaunched → egg at the exact last position (0.00 m).
- The shipped build has `constantAutoSave = false`. The game saves on quit, at SaveTrigger zones (≤ once/min),
  on a full crack, achievements and narrator choices. A killed process (Task Manager, crash) loses play since the
  last of those — vanilla behaviour, not the mod.
- Autosave also waits for the first real movement key (`saveData.eggMoved`); the lab shove sets it.

## Stage 2: collisions (0.2.0, 2026-10-21, tools/stand_test.ps1)
- Ghost = kinematic Rigidbody moved in FixedUpdate (MovePosition) + copy of the egg's MeshCollider on layer "Dynamic".
  Collider stays off while it overlaps our egg (spawn/teleport) to avoid a depenetration launch.
- Host toggles (F9, sent to guests, MsgSettings): Collide (default on), CrackOnHit (default off).
  CrackOnHit off = ghost collider tagged "FallCancel" → the game caps impact damage at damageThreshold/2 (never cracks).
- PASS: ghost mode → no contact at all, egg falls through (0.16–0.23 m from friend's centre).
- PASS: collide on → contacts logged ("Lab hit friend"), dropping on the friend's head pushes you off (0.7–2.5 m).
  You cannot balance on a friend: both are eggs, you slide off.
- PASS: crack off → zero cracks caused by the friend (all cracks in the runs were planks/boxes of the henhouse).
- PASS (by hand, owner, open area, CrackOnHit on): host fell onto the guest — host log "SemiCrack after hitting
  collider(friend) at 23.1 m/s". On the guest's side the falling host's ghost hit at 35.4 m/s and the guest did NOT
  crack: the game only damages an egg that moves into something (its own fall/air state), not one that gets hit.
  Owner accepted this: the faller cracks, the one standing does not.

## Lab incident (2026-10-21) — real saves overwritten, restored
- Two copies (~7.7 GB private each) exhausted 16 GB RAM, the PC crashed. On the next run one copy started WITHOUT
  BepInEx (old copy still held LogOutput.log; the launcher found the stale "Lab data dir" line and believed it).
  The plain game wrote the owner's real sav_2.sav / savm.sav. Restored from backups/LocalLow-EggingOn-2026-10-20.zip
  (hashes match); the overwritten versions are in backups/real-after-crash-2026-10-21.
- lab.ps1 now: waits for old copies to exit, refuses a locked log, kills a copy that does not report isolation,
  needs 5 GB free RAM per copy, snapshots + verifies the real save folder each run and auto-restores on change.
- Lab copies run the game's Steam Deck low preset + texture mip limit 3: ~4.5 GB private each.

## First real session (2026-10-22, 3 players, full game in ~4 h, collide + crack on) → 0.3.0
Owner's report and what 0.3.0 does about it:
- Invite code never worked; direct IP over Radmin did (80–140 ms). Host log showed connects + timeouts only;
  guest logs missing. Host runs a VPN (STUN ≠ router IP) → suspected asymmetric routing. 0.3: F9 lists the host's
  join addresses (Radmin first, Hyper-V/fake-IP tunnels filtered), louder VPN warning, host logs every join request.
  NEXT: get a guest's LogOutput.log from a failed code join.
- Invisible friend egg (name visible). ROOT CAUSE: after a full crack the new shell's shards are scaled to 0 and
  grow back (EggShell.FadeOut / show curve); a ghost built in that window copied scale 0. Also the sender stopped
  sending while cracked (core inactive) → ghost timed out after 5 s and was rebuilt, often in that window.
  Fix: ghost copies shards at their rest pose (originalLocalPositions/Rotation/Scale); a cracked player keeps
  sending (yolk ghost), and viewers no longer hide ghosts while they themselves are cracked.
- Teleport kicked the friend or yourself. Fix: teleport lands on the friend's spot, both sides pause collisions
  (tpSeq in state) until the eggs are 1.3 m apart; all ghost contacts get ≤1 cm penetration and a capped impulse
  (Physics.ContactModifyEvent) so a kinematic ghost can push but never fling. Lab: 6/6 teleports, victim 0.0 m/s.
- Pots jammed / flew. Partly ours: ghost colliders pushed our world's props. Fix: ghost collider
  excludeLayers = everything but the Egg layer. The rest (each world's pots move on their own) is stage 4.
- Teleport hotkey (T, rebindable; picker by digit for 2+ friends), toasts explaining refusals, F9 locks camera
  (Rewired mouse off), restyled scrollable F9, teleport counters (persisted in eggcoop.cfg [Stats], others' totals
  in the state packet), summary on the end credits (EndCredits.Play postfix).
- Lab now tests from a saved spot (`lab.ps1 up -extra ""`, owner rolled both eggs out of the henhouse).

## Release checklist (before sending a zip to friends)
- [ ] Build with `tools/lab.ps1 pack` only (Release). It must not contain the lab auto-shove:
      `ilspycmd -t EggCoop.Plugin <zip>/BepInEx/plugins/EggCoop/EggCoop.dll | Select-String AddForce` → 0 hits.
      Owner's rule: a host who steps away must never find their egg moved by the mod.
- [ ] When the mod is finished: delete the `#if LAB` shove block from Plugin.cs entirely.

## References
- vendor/coop-anything: playbook/how-coop-mods-work.md, networking.md, multi-instance-testing.md, games/tainted-grail-the-fall-of-avalon/NOTES.md
- vendor/universal-modder: skills/mod-any-game/references/engines/unity.md, knowledge/games/tunic/seamless-coop-bepinex6-native-hooks.md

## Stage 4a/4c: shared world (0.4.0, 2026-10-23)
- Recon (lab -coop-worlddump): 65 RigidDataHolders (game IDs, 2 duplicates), ~140 more loose bodies (hinged pots
  "big_pot", 36 buttons, boards, signs, pool balls), 61 RayFire fragments (skipped), 10 KinematicCurveMovement.
- WorldSync.cs: every non-kinematic world body (208 in the start area) keyed by FNV-1a of scene + hierarchy path
  (+sibling index); joint-connected bodies form one group. Ownership per group: the egg that touches a free group
  claims it (optimistic, host decides: first claim wins, an owner that stopped touching for 0.5 s loses it), bodies hit
  by an owned body join that owner, owner streams poses at 20 Hz, releases after 1 s at rest with final poses
  (reliable). Followers hold bodies kinematic, interpolate 100 ms behind, mark RigidDataHolder as moved (else the
  game's stabilizer snaps it home). Guest gets a host snapshot (all poses + owners + brokenObjects) on join.
  Breakables: new saveData.brokenObjects entries are mirrored; receivers just hide the object (no shatter fx).
- SharedClock: host sends Time.time every second; KinematicCurveMovement.FixedUpdate is transpiled to use it.
- PASS (lab): guest-thrown bucket ends at the same pose on both copies (to the cm, 3/3); dropping the egg on an
  object claims it through the real collision path; guest clock matches host clock within 0.01 s.
- NOT VERIFIED: hinged pots in the kitchen, claim races between two players, breakables across PCs, platforms
  actually in phase (the mover near spawn was culled), anything over real internet.
