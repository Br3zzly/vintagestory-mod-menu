# Mod Menu

A client-side utility menu for Vintage Story 1.22. Press **F2** in game to open it.

<img src="docs/screenshot1.png" alt="The Mod Menu window" width="560">

Four tabs - Player, Movement, Mining, Teleport - because as one column it outgrew the screen at
larger GUI scales. A tab that still does not fit splits into a second column rather than running
off the bottom. Greyed switches are the ones this server cannot honour; hovering says why.

## Features

- **Invincibility** — blocks all incoming damage
- **Flight** — free movement in any direction, with a speed slider from 1x to 3x in tenths and
  fall protection on landing whether or not *No fall damage* is on
- **No clip** — move through blocks; works on its own, no need to switch flight on first
- **Teleport to coordinates** — type X/Y/Z and go
- **Three saveable locations** — stand somewhere, press *Save*, rename the slot to whatever you like, press *Go* to return
- **Fullbright** — unlit caves become readable, no torches involved
- **Reach** — up to 100 blocks of extra reach for opening, breaking and placing. Attacking that
  far needs the mod on the server too, and then follows the slider without a switch of its own
- **Instant mine** — blocks break in a single tick
- **Vein miner** — breaking one block of a vein takes the rest of it, up to a limit you set
  between 1 and 400 blocks, with white outlines showing what the next swing would take, and a
  *AntiAbuse Safe* switch deciding whether it trickles or goes all at once
- **Drops at player** — what you mine lands at your feet instead of in the hole
- **Faster pickup** — lifts the server's 23-items-a-second collection rate, which is the real
  bottleneck after a large vein mine
- **One hit kill** — anything you hit dies
- **No hunger** — saturation stops draining
- **No durability loss** — tools, weapons and armour never wear down
- **Teleport from the world map** — right-click anywhere on the map for a small menu with the
  usual waypoint option plus *Teleport here*

Toggles and saved locations persist in `ModConfig/modmenu.json` between sessions.

## What actually works where

Vintage Story splits authority between client and server, and not every feature sits on the same
side of that line. This matters if you play on servers you do not control.

| Feature | Singleplayer | Server running this mod | Vanilla remote server |
| --- | --- | --- | --- |
| Flight / no-clip | yes | yes | yes |
| Teleport (coords and slots) | yes | yes | yes |
| Fullbright | yes | yes | yes |
| Reach, on blocks | yes | yes | **only with anti abuse off** |
| Instant mine | yes | yes | yes |
| Vein miner | yes | yes | yes |
| Invincibility (full) | yes | yes | **client-visual only** |
| No fall damage | yes | yes | **yes** |
| No durability loss | yes | yes | **client-visual only** |
| Drops at player | yes | yes | **no** |
| One hit kill | yes | yes | **no** |
| No hunger | yes | yes | **no** |
| Faster pickup | yes | yes | **no** |
| Reach, attacking entities | yes | yes | **no** |

Everything in that last block is decided by the server, so the menu greys those switches out
and explains why on hover when the server has never heard of this mod. It knows because the
network channel only reports connected when the server registered the same channel.

That name carries a version (`modmenu.v2`), which means a server running a *different* build of
this mod counts as not having it: the channel never pairs up, nothing is sent, and the
server-decided toggles grey out. That is deliberate. Before it, a newer client sent feature ids
an older server had no case for, the server threw inside its packet handler and disconnected the
sender mid-join, and the client died in the texture atlas nowhere near the cause. Both halves
have to be on the same build for the server-side features to work.

The reasoning:

- **Position is client-authoritative.** The client decides where it is and reports that upward,
  so flight and teleporting need no cooperation from the server at all.
- **Mining speed is client-authoritative.** `Block.OnGettingBroken` is documented as
  *"called only client side, every 40ms during breaking"*, and returning a remaining resistance
  of `<= 0` is what triggers the break. So the client genuinely decides when a block gives way.
- **Fullbright is baked into the chunk mesh, not painted over the screen.** Terrain light in
  Vintage Story is not a lamp shining at runtime: it is baked into vertex colours when a chunk
  is tesselated. `ChunkTesselator` builds a `ColorUtil.LightUtil` over the world's light level
  tables and calls `ToRgba` for every vertex, packing block light into RGB and sun light into
  A. The mod answers that call with "fully lit", so unlit rock tesselates exactly like rock in
  daylight. Meshes already built keep the light they were built with, so toggling it calls
  `ClientMain.RedrawAllBlocks` - the same thing the `/redraw` debug command does.

  Entities, items and anything held are lit by a different route - `GetLightRGBs` reads
  `SunLightLevels` and `BlockLightLevels` straight off the world - so those tables are
  flattened to 1.0 as well, and restored from a copy when the toggle goes off. They arrive with
  the world metadata rather than at startup, which is why it applies on player join.

  The first attempt at this drove the gamma setting instead, and it did not work: an unlit cave
  renders as black, `pow(0, anything)` is still 0, and no brightness curve turns black into
  something you can see. All that is left of it is a one-shot restore, in case that version
  crashed while holding somebody's gamma.
- **Reach is client-side to aim and server-side to accept, and the two disagree.** The aim ray
  is built in `PickingRayUtil` to exactly `player.WorldData.PickingRange` blocks, and the
  client's setter for that is a plain field write - nothing is sent, nothing asks permission.
  So putting a distant chest under the crosshair is free. What happens next depends entirely on
  which packet it turns into:

  | Action | Server check | Verdict |
  | --- | --- | --- |
  | Open / use a block | `HandleBlockInteract`, only when `AntiAbuse >= Basic` | full extra reach on a stock server |
  | Break or place | `TryModifyBlockInWorld`, only when `AntiAbuse >= Basic` | same |
  | Use an item on something | `HandleHandInteraction`, only when `AntiAbuse >= Basic` | same |
  | **Attack an entity** | `HandleEntityInteraction`, **always**, plus a stricter client-side gate | capped at ~1.5 blocks unless the server runs this mod |

  That last row is the one vanilla will not bend. `HandleEntityInteraction` rejects an attack when
  the entity's box is further than twice the held weapon's attack range from your eye, with no
  `AntiAbuse` condition on it and no privilege that skips it. Default attack range is 1.5, so
  killing things stops at about 3 blocks no matter where the slider sits. Right-clicking an
  entity is not covered by that check - only attacking is - so milking a goat from range works
  where hitting it does not. The server also only looks for the entity within its own
  `PickingRange + 10`, which is a second ceiling at around 14 blocks.

  The server's copy of your picking range never changes, incidentally: `RequestModeChange` only
  accepts a new one from a player holding the `pickingrange` privilege, so every check above
  measures against the stock 4.5.

  With the mod on both sides that row does bend, and without a switch of its own - but it takes
  three separate lifts, because the swing is blocked in three places:

  1. **The client never sends it.** `ClientMain.TryAttackEntity` measures the target against the
     held weapon's attack range - not twice it - and simply skips the packet when it is further.
     A stricter gate than the server's, and the one that matters first.
  2. **The server cannot find the target.** `HandleEntityInteraction` looks for the entity within
     `PickingRange + 10` before any range check runs, and the server's copy of picking range is
     always the stock 4.5.
  3. **The server rejects the distance**, the `2 x GetAttackRange` check above.

  All three are answered by lifting `GetAttackRange` for the length of one swing, plus the
  server's picking range for the length of one packet, both put straight back afterwards. A
  prefix marks who is swinging and the other patches answer while the mark is set - the same
  two-patch shape *Drops at player* uses, for the same reason: the value is decided somewhere
  that has no idea which player it is for.
- **Vein miner rides on that same client authority.** Every extra block goes through
  `ClientMain.OnPlayerTryDestroyBlock`, the one door a player-driven break uses, so the server
  sees nothing but an ordinary run of mining. Which is exactly why it is paced - see the
  block-break ban below.

  One wrinkle worth knowing about, because it crashes the game rather than failing quietly:
  `ClientMain.tryAccess` takes a `BlockSelection` parameter and never reads it, testing the
  ambient `ClientMain.BlockSelection` instead - confirmed in the IL, where the argument is
  never loaded. Mining by hand never notices, since the block under the crosshair is the block
  being broken. A vein miner breaks blocks nobody is aiming at, and the moment the ore under
  the crosshair is gone that field is null, which lands as a `NullReferenceException` inside
  the land claim check and takes the client down with it. So the block being broken is swapped
  into that field for the duration of the call and put back afterwards, which also points the
  claim check at the block that is actually being broken.
- **Drops are the server's to hand out.** `Block.SpawnDropsAndRemoveBlock` only spawns
  anything when `world.Side` is Server, so *Drops at player* needs the mod on the server. In
  singleplayer the internal server is in the same process, so it works there.
- **Health and inventory are server-authoritative.** The server keeps its own copy of your
  health and your item durability and syncs them back down. A client-only patch stops your
  client drawing the damage, but the next sync overwrites it. There is no client-side fix for
  this — it needs the mod on the server too. Confirmed in `EntityBehaviorHealth`
  (`OnEntityReceiveDamage` returns immediately when `entity.World.Side` is Client) and in
  `ServerSystemBlockSimulation`, which re-runs `OnBlockBrokenWith` on the server's own
  inventory copy after every break.

- **Fall damage is the exception**, and *No fall damage* exploits it. A remote server runs no
  real physics for a client-controlled player; it **reconstructs** the landing velocity from
  the position packets the client sends
  (`EntityBehaviorPlayerPhysics.HandleRemotePhysics`) and feeds that into the fall check. That
  check skips all damage when the landing velocity is gentler than about `-0.19`
  (`EntityBehaviorHealth.OnFallToGround`), *regardless of how far you fell*. So the mod simply
  eases the descent the server sees in the last blocks before the ground. Because the
  server owns no better information than what the client reports, this works without the mod
  being installed server-side. It only softens the very end of a fall, so normal movement is
  untouched.

  How short that ending can be is set by the server's own arithmetic. It builds one speed
  value per position packet, and the client sends one every 4th physics tick (`1/60`s each),
  so every `1/15`s. Landing damage uses the harsher of the last two of those, which means the
  final 8 physics ticks have to look gentle - about 1.25 blocks at the speed the mod holds.
  Above that stretch the mod only brakes as hard as it needs to arrive there in time, so a
  long fall stays at full speed until roughly 1.5 blocks up and the visibly slow part lasts
  around 0.18s.

  Flight rides on the same catch. A remote server cannot tell flying from falling - both are
  just a stream of dropping positions - so it charges for the landing either way, which is why
  the catch runs whenever flight is on, with or without the *No fall damage* toggle. It is
  also why fly speed stops at 3: past roughly 5 the downward acceleration between two catch
  runs outpaces what the last blocks can absorb, and the landing starts costing health again.

In singleplayer everything works because the client and the internal server run in the same
process, so the Harmony patches cover both halves at once.

## The block break ban, and why vein mining is paced

Breaking blocks quickly is not rate limited on a Vintage Story server. It is grounds for a ban.

`PlayerAntiAbuseMonitor` keeps a ring buffer of every block a player breaks and scans it once a
second. If any `AntiAbuseTriggerOnBlockBreakCount` consecutive breaks fall inside
`AntiAbuseTriggerOnDurationMs`, it calls `BanPlayer` directly - no warning, no kick first. The
server defaults are **40 breaks within 2000ms, banned for 14 days**. Players holding the
`gamemode` or `controlserver` privilege are exempt; nobody else is.

The same `AntiAbuse` setting also gates a reach check on every break
(`IsInInteractionRangeOf(pos, 0.7f)` in `TryModifyBlockInWorld`), so on a server that turns it
on, vein blocks further away than your normal reach are refused and logged as out-of-range
rather than broken.

Both are off in the stock server config (`AntiAbuse = EnumProtectionLevel.Off`) and the setting
is never sent to clients, so there is no way to look before leaping. That is what the
**AntiAbuse Safe** toggle is for. It defaults on, and the risky mode has to be chosen.

### With it on

The queue paces itself as though every server had anti abuse switched on:

- at least 65ms between breaks, which is `2000ms x 1.25 / 39` - spacing 40 breaks that far
  apart spans more than the window they would have to fit inside
- a running count of the last 39 breaks, checked before each one, which is the server's own
  test run one break ahead of it
- **manual mining counts too.** The patch that feeds the counter sees every block you break,
  not only the ones the vein miner breaks, so mining by hand while a vein drains cannot
  combine into a burst

Replaying the server's exact ring-buffer scan against this pacing, the tightest 40-break window
the mod can produce is around 2.5-3.1 seconds even with packets bunching up on the way, against
the 2 seconds that would trigger a ban. Without the pacing the same veins produce 40 breaks in
420-900ms, which is a ban in every scenario tested.

The cost is speed: a 50-block vein takes a little over three seconds to finish draining, and a
400-block one around half a minute.

### With it off

The whole vein goes in one pass, however many blocks that is. In singleplayer, and on any
server that leaves anti abuse at its default of off, this is free - there is nothing watching.
On a server that turned it on, 40-odd blocks at once is the exact shape it bans for, and the
ban is 14 days by default.

**Reach is not a separate problem to solve.** Nothing on the client limits how far away a block
can be: breaks go straight to `OnPlayerTryDestroyBlock` with a position, no aiming involved, so
the vein miner is already reaching as far as the vein goes. The only reach check anywhere is
the server's, and it is behind the same `AntiAbuse` switch as the ban - measured from the
server's copy of your eye position against `PickingRange + 0.7`, roughly 5 blocks, with
`PickingRange` itself only raisable by a privilege the server grants. So either a server has
anti abuse off, and there is no reach limit to get around, or it has it on, and breaking a vein
all at once is a ban wherever the blocks are. Beating the check would mean feeding the server
false positions between breaks, which is only worth building for the paced mode - say so if you
want it.

## Coordinates

The menu shows and accepts the **same coordinates the HUD and map show** — the ones relative
to world spawn.

Internally, entity positions are absolute: the world is roughly 1,024,000 blocks across and
spawn sits near the middle, so standing at a displayed `95, 117, -417` really means an entity
position of `512096.6, 115.9, 511584.6`. X and Z carry that offset; Y is identical in both
spaces.

Saved slots store the **absolute** position and display the relative one, so a slot keeps
pointing at the same place even if the world's spawn point is later moved.

## How teleporting places you

Every teleport — typed coordinates and saved slots alike — is cleaned up before you move:

1. **X and Z snap to the middle of the block column** (`floor + 0.5`), so you never land
   straddling an edge.
2. **Y rounds up to a whole block** (`115.9` → `116`), so your feet rest on top of a block
   rather than just inside the one below it.
3. **The destination is checked for clearance.** If your collision box would not fit, the
   target rises one block at a time until it does, up to 256 blocks.

If no free spot is found the teleport is cancelled rather than dropping you inside terrain.

Saved-slot labels show the **snapped** figures, so what the menu lists is where Go actually
puts you rather than the raw position that was recorded.

### Long distances

The server refuses any position update that moves a player more than **128 blocks on a single
axis** and answers with a correction — that is why a naive jump to somewhere far away flashes
and snaps straight back:

```csharp
// Vintagestory.Server.Systems.EntityPosExtensions.SetFromPacket
private const int maxMovePerPacket = 128;
if (Math.Abs(pos.X - num) > 128.0) return false;   // → server sends a correction
```

Anything beyond that is therefore covered in steps of 64 blocks, one per client tick, each
small enough to be accepted. A cross-map jump of ~10,000 blocks takes about 3 seconds, which
is roughly what the built-in `/tp` costs for the same distance.

Each step reads the live position rather than tracking its own, which makes it self-correcting:
if the server does bounce you back, the next step simply sets off again from wherever you
actually landed, so bad connections slow the teleport down instead of breaking it. Position
updates travel over UDP, and the 64-block step is sized so that even a lost packet leaves the
following one within the server's limit. If it somehow makes no progress for 20 seconds it
gives up and says so.

One honest limitation: the clearance check reads blocks through the **client's** block
accessor, and an unloaded chunk reports air whether or not there is really terrain there. For
long-distance jumps into unloaded territory the check cannot mean anything, so instead of
pretending, the mod teleports and tells you `(destination not loaded, clearance unchecked)`.
Jumping twice to the same place works — the second time the chunk is loaded and the check is
real.

## Installing

Drop `ModMenu.zip` (or just the built `ModMenu.dll` plus `modinfo.json`) into:

```
%APPDATA%\VintagestoryData\Mods
```

The mod is declared `Universal` but with `requiredOnServer: false`, so it installs and runs
client-side on its own. If you also run your own server, putting the same file in the server's
`Mods` folder promotes invincibility and durability from cosmetic to real — the client reports
its toggles over a `modmenu` network channel, and the server applies them per player.

## Teleporting from the world map

Right-clicking the map normally opens the add/edit waypoint dialog straight away. With this
mod it opens a short list instead:

- **Add waypoint** (empty map) or **Modify waypoint** (on an existing marker) — opens exactly
  the dialog the game would have opened
- **Teleport here** — goes there using the same stepped teleport as the rest of the menu, so
  it needs no privileges and is not limited to 128 blocks

The height comes from the map's own terrain data: `TranslateViewPosToWorldPos` fills in Y from
`GetRainMapHeightAt`, which is the surface at that spot, and the clearance search takes it from
there. Teleporting to a waypoint uses the waypoint's own stored position instead.

The game has a similar shortcut already — shift-clicking a waypoint in creative mode runs
`/tp` — but that needs creative mode and command privileges. This works in survival on a
server where you have neither.

These two hooks are the only part of the mod that reaches into `VSEssentials` rather than the
core API, so they are applied separately inside a try/catch. If a future game update renames
them, the map menu is skipped with a warning in the log and everything else still works.

## Updating the mod

**Close Vintage Story before replacing the zip, then start it again.** That is the whole
procedure — there is nothing to clean up, and no cache to touch.

If you swap the zip while the game is still running, the next world load fails with:

```
[Error] [modmenu] Could not load file or assembly 'ModMenu, Version=1.0.0.0'.
                  Assembly with same name is already loaded
```

and the mod is skipped entirely — the hotkey never registers, so the menu just does not open.

This is a .NET limitation, not a mod bug: once a process has loaded an assembly it cannot
load a different one with the same name, so the already-running game is stuck with the old
copy. The game detects this case and means to tell you *"Please restart the game"*, but the
check compares against the bare message text while the real exception arrives with a
`Could not load file or assembly ...` prefix, so the helpful line never prints and you get
the raw error instead.

Restarting the game is the fix. Bumping the mod version does not help — .NET rejects the
duplicate name whether the versions match or differ.

### What about the `Cache\unpack\ModMenu.zip_<hash>` folders?

Leave them alone. Each is keyed by a hash of the zip it came from, and `ModContainer` only
ever enumerates the folder matching the zip currently in `Mods`. Old folders are never
loaded — they are just a few KB of dead disk space that Vintage Story does not garbage
collect. Deleting them changes nothing.

## Building

Requires the .NET 10 SDK. The build finds Vintage Story via the `VINTAGE_STORY` environment
variable, falling back to `%APPDATA%\Vintagestory`.

```bash
dotnet build -c Release
```

The output lands in `bin/Release/`.

## Notes

- The hotkey is registered through the game's own keybind system, so it can be remapped in
  Settings → Controls if F2 collides with something.
- While the vein miner is on, aiming at a block outlines every other block the swing would
  take, in white. They are drawn with `WireframeCube`, the same class the game outlines the
  aimed-at block with, so they behave like the selection box does and follow its thickness
  setting. The block under the crosshair is left to the game's own outline.
- The outlines and the miner run the same search, so what lights up is what breaks.
- The vein miner limit counts the block you hit, so 10 means that one plus nine more. A vein
  already draining is left to finish: mining something else meanwhile breaks just that block.
- Connected means any of the 26 surrounding positions, diagonals included, since veins run
  diagonally as often as not.
- Flight and no-clip are independent switches; either one works on its own. No-clip turns free
  movement on by itself, because collision-off with gravity still running would just drop you
  through the floor.
- Flight always breaks your landing, so the *No fall damage* toggle only matters for falls you
  did not fly into. No-clip suspends the catch while it is on - you pass through the ground
  rather than landing on it, and holding every descent to landing speed would make no-clip a
  crawl.
- Using this on a multiplayer server you do not own is very likely against its rules.
