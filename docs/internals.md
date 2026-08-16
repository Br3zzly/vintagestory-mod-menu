# How it works

Reference notes for working on the mod. Everything here is derived from the decompiled game
code; where a claim rests on a specific method, that method is named so it can be checked again
after a game update.

The README is for people installing the mod. This is the file to update when the *reasons*
change.

## Contents

- [Client and server authority](#client-and-server-authority)
- [Reach](#reach)
- [Vein miner, and the block break ban](#vein-miner-and-the-block-break-ban)
- [ESP](#esp)
- [Transparent world](#transparent-world)
- [Fullbright](#fullbright)
- [Coordinates and teleporting](#coordinates-and-teleporting)
- [World map hooks](#world-map-hooks)
- [Updating a running game](#updating-a-running-game)

## Client and server authority

Vintage Story splits authority between client and server, and not every feature sits on the same
side of that line.

| Feature | Singleplayer | Server running this mod | Vanilla remote server |
| --- | --- | --- | --- |
| Flight / no-clip | yes | yes | yes |
| Teleport (coords and slots) | yes | yes | yes |
| Fullbright | yes | yes | yes |
| ESP / transparent world | yes | yes | yes |
| Instant mine | yes | yes | yes |
| Vein miner | yes | yes | yes |
| No fall damage | yes | yes | yes |
| Reach, on blocks | yes | yes | only with anti abuse off |
| Invincibility (full) | yes | yes | client-visual only |
| No durability loss | yes | yes | client-visual only |
| Drops at player | yes | yes | no |
| One hit kill | yes | yes | no |
| No hunger | yes | yes | no |
| Faster pickup | yes | yes | no |
| Reach, attacking entities | yes | yes | no |

The menu greys out the server-decided switches when the server has never heard of the mod. It
knows because the network channel only reports connected when the server registered the same
channel.

That channel name carries a version (`modmenu.v2`), which means a server running a *different*
build counts as not having it: the channel never pairs up, nothing is sent, and those toggles
grey out. This is deliberate. Before it, a newer client sent feature ids an older server had no
case for, the server threw inside its packet handler and disconnected the sender mid-join, and
the client died in the texture atlas nowhere near the cause.

The reasoning behind each row:

- **Position is client-authoritative.** The client decides where it is and reports that upward,
  so flight and teleporting need no cooperation from the server.
- **Mining speed is client-authoritative.** `Block.OnGettingBroken` is documented as *"called
  only client side, every 40ms during breaking"*, and returning a remaining resistance of `<= 0`
  is what triggers the break. So the client genuinely decides when a block gives way.
- **Rendering is entirely the client's.** ESP, transparent world and fullbright never ask the
  server anything. Nothing is sent, so there is nothing for a server to accept or refuse.
- **Drops are the server's to hand out.** `Block.SpawnDropsAndRemoveBlock` only spawns anything
  when `world.Side` is Server, so *Drops at player* needs the mod on the server. In singleplayer
  the internal server is in the same process, so it works there.
- **Health and inventory are server-authoritative.** The server keeps its own copy of health and
  item durability and syncs them back down. A client-only patch stops the client drawing the
  damage, but the next sync overwrites it. Confirmed in `EntityBehaviorHealth`
  (`OnEntityReceiveDamage` returns immediately when `entity.World.Side` is Client) and in
  `ServerSystemBlockSimulation`, which re-runs `OnBlockBrokenWith` on the server's own inventory
  copy after every break.

In singleplayer everything works because the client and the internal server run in the same
process, so the Harmony patches cover both halves at once.

### Fall damage is the exception

A remote server runs no real physics for a client-controlled player; it **reconstructs** the
landing velocity from the position packets the client sends
(`EntityBehaviorPlayerPhysics.HandleRemotePhysics`) and feeds that into the fall check. That
check skips all damage when the landing velocity is gentler than about `-0.19`
(`EntityBehaviorHealth.OnFallToGround`), *regardless of how far you fell*. So the mod eases the
descent the server sees in the last blocks before the ground. The server owns no better
information than what the client reports, so this works without the mod being installed
server-side.

How short that ending can be is set by the server's own arithmetic. It builds one speed value per
position packet, and the client sends one every 4th physics tick (`1/60`s each), so every
`1/15`s. Landing damage uses the harsher of the last two, which means the final 8 physics ticks
have to look gentle — about 1.25 blocks at the speed the mod holds. Above that stretch the mod
only brakes as hard as it needs to arrive there in time, so a long fall stays at full speed until
roughly 1.5 blocks up and the visibly slow part lasts around 0.18s.

Flight rides on the same catch. A remote server cannot tell flying from falling — both are just a
stream of dropping positions — so it charges for the landing either way, which is why the catch
runs whenever flight is on, with or without the *No fall damage* toggle. It is also why fly speed
stops at 3: past roughly 5 the downward acceleration between two catch runs outpaces what the
last blocks can absorb, and the landing starts costing health again.

## Reach

Client-side to aim and server-side to accept, and the two disagree. The aim ray is built in
`PickingRayUtil` to exactly `player.WorldData.PickingRange` blocks, and the client's setter for
that is a plain field write — nothing is sent, nothing asks permission. So putting a distant
chest under the crosshair is free. What happens next depends on which packet it turns into:

| Action | Server check | Verdict |
| --- | --- | --- |
| Open / use a block | `HandleBlockInteract`, only when `AntiAbuse >= Basic` | full extra reach on a stock server |
| Break or place | `TryModifyBlockInWorld`, only when `AntiAbuse >= Basic` | same |
| Use an item on something | `HandleHandInteraction`, only when `AntiAbuse >= Basic` | same |
| **Attack an entity** | `HandleEntityInteraction`, **always**, plus a stricter client-side gate | capped at ~1.5 blocks unless the server runs this mod |

That last row is the one vanilla will not bend. `HandleEntityInteraction` rejects an attack when
the entity's box is further than twice the held weapon's attack range from your eye, with no
`AntiAbuse` condition and no privilege that skips it. Default attack range is 1.5, so killing
things stops at about 3 blocks no matter where the slider sits. Right-clicking an entity is not
covered by that check — only attacking is — so milking a goat from range works where hitting it
does not. The server also only looks for the entity within its own `PickingRange + 10`, a second
ceiling at around 14 blocks.

The server's copy of your picking range never changes: `RequestModeChange` only accepts a new one
from a player holding the `pickingrange` privilege, so every check above measures against the
stock 4.5.

With the mod on both sides that row does bend, but it takes three separate lifts, because the
swing is blocked in three places:

1. **The client never sends it.** `ClientMain.TryAttackEntity` measures the target against the
   held weapon's attack range — not twice it — and skips the packet when it is further. A
   stricter gate than the server's, and the one that matters first.
2. **The server cannot find the target.** `HandleEntityInteraction` looks for the entity within
   `PickingRange + 10` before any range check runs.
3. **The server rejects the distance**, the `2 x GetAttackRange` check above.

All three are answered by lifting `GetAttackRange` for the length of one swing, plus the server's
picking range for the length of one packet, both put straight back afterwards. A prefix marks who
is swinging and the other patches answer while the mark is set — the same two-patch shape *Drops
at player* uses, for the same reason: the value is decided somewhere that has no idea which
player it is for.

## Vein miner, and the block break ban

Every extra block goes through `ClientMain.OnPlayerTryDestroyBlock`, the one door a player-driven
break uses, so the server sees nothing but an ordinary run of mining.

One wrinkle crashes the game rather than failing quietly: `ClientMain.tryAccess` takes a
`BlockSelection` parameter and never reads it, testing the ambient `ClientMain.BlockSelection`
instead — confirmed in the IL, where the argument is never loaded. Mining by hand never notices,
since the block under the crosshair is the block being broken. A vein miner breaks blocks nobody
is aiming at, and the moment the ore under the crosshair is gone that field is null, which lands
as a `NullReferenceException` inside the land claim check and takes the client down with it. So
the block being broken is swapped into that field for the duration of the call and put back
afterwards, which also points the claim check at the block actually being broken.

### The ban

Breaking blocks quickly is not rate limited on a Vintage Story server. It is grounds for a ban.

`PlayerAntiAbuseMonitor` keeps a ring buffer of every block a player breaks and scans it once a
second. If any `AntiAbuseTriggerOnBlockBreakCount` consecutive breaks fall inside
`AntiAbuseTriggerOnDurationMs`, it calls `BanPlayer` directly — no warning, no kick first. The
server defaults are **40 breaks within 2000ms, banned for 14 days**. Players holding the
`gamemode` or `controlserver` privilege are exempt; nobody else is.

The same `AntiAbuse` setting also gates a reach check on every break
(`IsInInteractionRangeOf(pos, 0.7f)` in `TryModifyBlockInWorld`), so on a server that turns it
on, vein blocks further away than your normal reach are refused and logged as out-of-range rather
than broken.

Both are off in the stock server config (`AntiAbuse = EnumProtectionLevel.Off`) and the setting
is never sent to clients, so there is no way to look before leaping. That is what **AntiAbuse
Safe** is for. It defaults on, and the risky mode has to be chosen.

### With it on

The queue paces itself as though every server had anti abuse switched on:

- at least 65ms between breaks, which is `2000ms x 1.25 / 39` — spacing 40 breaks that far apart
  spans more than the window they would have to fit inside
- a running count of the last 39 breaks, checked before each one, which is the server's own test
  run one break ahead of it
- **manual mining counts too.** The patch that feeds the counter sees every block you break, not
  only the ones the vein miner breaks, so mining by hand while a vein drains cannot combine into
  a burst

Replaying the server's exact ring-buffer scan against this pacing, the tightest 40-break window
the mod can produce is around 2.5–3.1 seconds even with packets bunching up on the way, against
the 2 seconds that would trigger a ban. Without the pacing the same veins produce 40 breaks in
420–900ms, which is a ban in every scenario tested.

The cost is speed: a 50-block vein takes a little over three seconds to drain, a 400-block one
around half a minute.

### With it off

The whole vein goes in one pass. In singleplayer, and on any server that leaves anti abuse at its
default of off, this is free — there is nothing watching. On a server that turned it on, 40-odd
blocks at once is the exact shape it bans for.

**Reach is not a separate problem to solve.** Nothing on the client limits how far away a block
can be: breaks go straight to `OnPlayerTryDestroyBlock` with a position, no aiming involved, so
the vein miner already reaches as far as the vein goes. The only reach check anywhere is the
server's, and it is behind the same `AntiAbuse` switch as the ban. So either a server has anti
abuse off, and there is no reach limit to get around, or it has it on, and breaking a vein all at
once is a ban wherever the blocks are.

## ESP

### The catalogue

Built once at `LevelFinalize`, never while a GUI is composing. `Lang.GetMatching` falls back to a
wildcard scan plus a regex match per entry on a miss, and thousands of those while a tab composes
is a visible freeze — that was the cause of the first three attempts locking up the game.

Entries are grouped by display name, which is why one *Native copper ore* row appears rather than
the twenty block codes it covers, one per host rock. Lowercase forms are precomputed into flat
parallel arrays, so a keystroke costs a `Contains` over a few thousand short strings and no
string building at all. Searching matches names rather than codes because that is what people
type.

### Scanning

Work grows with the cube of the range, which runs to 500 blocks, so the scan is arranged never to
repeat itself:

- chunks are read straight out of their own block array (`Unpack_ReadOnly` then `chunk.Data[i]`,
  index `(y * 32 + z) * 32 + x`) rather than through the block accessor, which turns a delegate
  call and a chunk lookup per block into an array index. `chunk.Empty` rejects air chunks outright
- what is found is kept per chunk as a bitmap — one bit per block position, 4KB a chunk — so it
  survives moving and changing the range. Only changing what you are looking for throws it away.
  The bitmap is what makes the six neighbour tests per hit a shift and a mask rather than a hash
  lookup on an allocated position object
- scanning runs on worker threads, one chunk each so no two threads touch the same chunk's data,
  nearest first, cancellable, capped at half the cores
- every chunk gets its own mesh as soon as it is read, so results appear as they are found. Mesh
  uploads are rationed per frame, since uploading is main-thread work

Faces between two blocks of the same target are dropped, so a vein arrives as one shape rather
than a pile of cubes. That is why indexing a chunk marks its six neighbours for a mesh rebuild: a
face on a shared boundary is only correct once both sides are known.

### Keeping it current

`capi.Event.BlockChanged` fires after the world is updated, for every route a block can change
by: broken or placed here, and single or bulk updates from the server. The handler is O(1) — one
dictionary lookup for the chunk, one bit test for "was this tracked", one hash lookup for "is
what's there now tracked". Blocks that were never tracked stop there.

That alone is not enough. The client's plain `SetBlock` announces nothing — only `MarkBlockDirty`
and `MarkBlockModified` do — so when grass loses its soil the client removes it locally through
the silent path, and the server's confirmation then arrives saying "this is air" when the client
already has air there, so the packet handler's changed-check skips and no event fires either. The
block vanishes having never been announced once. Falling sand and decaying leaves reach the same
place by their own routes.

So any announced change also queues its chunk to be **read again**, which catches everything that
went with the break regardless of cause. Kept cheap by three things: only chunks that hold
something tracked are queued; a chunk queues once however many blocks changed in it; and at most
two re-reads happen per frame, with a re-read that finds no difference (512 word comparisons)
stopping before it touches the mesh.

### Colours

Targets take the first colour nobody is using from a palette of twenty, ordered so that any
prefix of it is as distinguishable as possible. Removing a target frees its colour. Past twenty
they repeat.

Red is not in the palette. It belongs to the vein miner's preview.

**Colours are packed with `ColorUtil.ColorFromRgba(r, g, b, a)`, not `ToRgba`.** A mesh's `Rgba`
buffer is bound as four unsigned bytes in buffer order (`GL.VertexAttribPointer(slot, 4,
UnsignedByte, normalized)` — size 4, not `GL_BGRA`), and `MeshData.AddVertexSkipTex` writes the
int straight into that buffer, so on a little-endian machine the shader's red channel is the
**lowest** byte. `ColorUtil.ToRgba(a, r, g, b)` packs the ARGB form, whose lowest byte is blue —
using it swaps red and blue in everything drawn. The API says as much on `ColorFromRgba`.

The index stores a **slot byte** per hit rather than the colour: there are at most twenty targets
and a chunk of common stone can hold thirty thousand hits. Slots only mean anything for the
selection they were built from, which is exactly as long as the index lives.

The wireframe shader passes vertex colour through untouched, despite appearances:
`applyLightWithoutPointLight(color, color, 0)` with both arguments equal cancels to the input,
and the fragment stage is `outColor = color`. No fog, no light, at any distance. It does divide
by the colour's own brightness, so nothing in the palette is near black.

## Transparent world

Two separate things decide whether a face reaches a chunk mesh, and both have to be answered:

- the block's own `DrawType`. `ChunkTesselator.TesselateBlock` returns immediately for `Empty`,
  which is what air is, so a block set to `Empty` contributes no geometry at all
- the **neighbour's** `SideOpaque`. A face is culled where the block beyond it is opaque, so
  hiding stone without also clearing its opacity leaves buried ore with all six faces culled:
  invisible, inside an invisible world

Both are plain fields on the client's own block list, read while a chunk is tesselated and nowhere
that decides what the world *is*. Collision and aiming read the collision and selection boxes,
which are left alone, so the world stays solid to stand on and to mine.

The originals are kept per block id and put back on the way out. Either direction needs
`ClientMain.RedrawAllBlocks`, the same call the game makes when a graphics setting changes what
meshes contain.

A handful of blocks draw through block-entity renderers rather than the chunk mesh, and those
bypass `DrawType` entirely, so a few animated or decorative ones still appear.

## Fullbright

Three separate things make the world dark, and lifting the light only answers one of them.

**Terrain light is baked into the chunk mesh**, not painted over the screen. `ChunkTesselator`
builds a `ColorUtil.LightUtil` over the world's light level tables and calls `ToRgba` for every
vertex, packing block light into RGB and sun light into A. The mod answers that call with "fully
lit", so unlit rock tesselates exactly like rock in daylight. Meshes already built keep the light
they were built with, so toggling calls `ClientMain.RedrawAllBlocks`.

**Entities, items and held things are lit by a different route.**
`BlockAccessorReadLockfree.GetLightRGBs` reads `SunLightLevels` and `BlockLightLevels` straight
off the world, so those tables are flattened to 1.0 and restored from a copy when the toggle goes
off. They arrive with the world metadata rather than at startup, which is why that half applies on
player join.

**The distance is painted black separately, and this was the twenty-block wall.** `AmbientManager`
keeps a modifier called `blackfogincaves` whose weight it drives from the sunlight reaching the
player, so underground it goes to full. The `night` modifier drives `SceneBrightness` and
`FogBrightness` down as daylight falls, and those multiply the blended ambient and fog colours for
the whole scene. No amount of brightening blocks beats that, because it is applied after them.

So the mod puts a modifier of its own at the end of the ambient stack with full weight on fog
density, flat fog density, fog minimum, ambient colour, scene brightness and fog brightness.
Modifiers blend in the order they are held — each lerps the running value toward its own by its
weight — so weight 1 at the end is the last word. It re-asserts itself on a tick, because anything
registered later (weather, an ambient the server pushes) would blend back over it. Fog *colour* is
left alone deliberately: with no fog to draw it only tints the background, and a dark background
behind fully lit blocks is what makes them read.

An earlier attempt drove the gamma setting instead and did not work: an unlit cave renders as
black, `pow(0, anything)` is still 0, and no brightness curve turns black into something visible.
All that is left of it is a one-shot restore, in case that version crashed while holding somebody's
gamma.

**View distance is the ceiling.** Nothing can draw a chunk the client does not have. The default is
256 blocks, and servers cap chunk radius separately (`MaxChunkRadius`, 12 chunks by default).

## Coordinates and teleporting

The menu shows and accepts the same coordinates the HUD and map show — the ones relative to world
spawn. Internally, entity positions are absolute: the world is roughly 1,024,000 blocks across and
spawn sits near the middle, so a displayed `95, 117, -417` is really `512096.6, 115.9, 511584.6`.
X and Z carry that offset; Y is identical in both spaces.

Saved slots store the **absolute** position and display the relative one, so a slot keeps pointing
at the same place even if the world's spawn point is later moved.

Every teleport is cleaned up before the move:

1. **X and Z snap to the middle of the block column** (`floor + 0.5`), so you never land
   straddling an edge.
2. **Y rounds up to a whole block** (`115.9` → `116`), so feet rest on top of a block rather than
   just inside the one below.
3. **The destination is checked for clearance.** If the collision box would not fit, the target
   rises one block at a time until it does, up to 256 blocks.

If no free spot is found the teleport is cancelled rather than dropping the player inside terrain.
Saved-slot labels show the snapped figures, so what the menu lists is where *Go* actually puts you.

### Long distances

The server refuses any position update that moves a player more than **128 blocks on a single
axis** and answers with a correction — which is why a naive long jump flashes and snaps back:

```csharp
// Vintagestory.Server.Systems.EntityPosExtensions.SetFromPacket
private const int maxMovePerPacket = 128;
if (Math.Abs(pos.X - num) > 128.0) return false;   // → server sends a correction
```

So anything beyond that is covered in steps of 64 blocks, one per client tick. A cross-map jump of
~10,000 blocks takes about 3 seconds, roughly what the built-in `/tp` costs for the same distance.

Each step reads the live position rather than tracking its own, which makes it self-correcting: if
the server bounces the player back, the next step sets off again from wherever they actually
landed, so bad connections slow the teleport instead of breaking it. Position updates travel over
UDP, and the 64-block step is sized so that even a lost packet leaves the following one within the
server's limit. If it makes no progress for 20 seconds it gives up and says so.

The clearance check reads blocks through the **client's** block accessor, and an unloaded chunk
reports air whether or not there is terrain there. For long jumps into unloaded territory the check
cannot mean anything, so the mod teleports and says `(destination not loaded, clearance
unchecked)`. Jumping twice to the same place works — the second time the chunk is loaded.

## World map hooks

`TranslateViewPosToWorldPos` fills in Y from `GetRainMapHeightAt`, the surface at that spot, and
the clearance search takes it from there. Teleporting to a waypoint uses the waypoint's own stored
position instead.

These two hooks are the only part of the mod that reaches into `VSEssentials` rather than the core
API, so they are applied separately inside a try/catch. If a future game update renames them, the
map menu is skipped with a warning in the log and everything else still works.

## Updating a running game

Replacing the zip while the game is running makes the next world load fail with `Could not load
file or assembly 'ModMenu' ... Assembly with same name is already loaded`, and the mod is skipped
entirely — the hotkey never registers, so the menu simply does not open.

This is a .NET limitation: once a process has loaded an assembly it cannot load a different one
with the same name. The game detects the case and means to say *"Please restart the game"*, but
the check compares against the bare message text while the real exception arrives with a `Could
not load file or assembly ...` prefix, so the helpful line never prints. Bumping the mod version
does not help — .NET rejects the duplicate name whether the versions match or differ.

The `Cache\unpack\ModMenu.zip_<hash>` folders are harmless. Each is keyed by a hash of the zip it
came from, and `ModContainer` only ever enumerates the folder matching the zip currently in `Mods`.
Old folders are never loaded; they are a few KB of dead disk space the game does not collect.

The build's `Deploy` target refuses to run while the game is open for exactly this reason, and
clears both the stale unpack folders and any previously deployed `ModMenu` zip so two versions
cannot sit in the `Mods` folder at once.
