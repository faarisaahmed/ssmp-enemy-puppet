# SSMPEnemyPuppet

> **Experimental, and untested in multiplayer.** It compiles and deploys; nobody has yet run it
> with two players connected. See [Status](#status).

An addon for [SSMP](https://github.com/Extremelyd1/SSMP) that lets **both players take control of
enemies and fight each other with them**.

Click an enemy, claim it, and drive it: it walks where you click, and its attacks fire when *you*
decide — aimed at the other player, not at where you're pointing it.

The goal is a duel where two people each drive a boss, instead of two people fighting the same AI.

---

## Requirements

| | |
|---|---|
| Game | Hollow Knight: Silksong |
| Mod loader | [BepInExPack for Silksong](https://thunderstore.io/c/hollow-knight-silksong/p/BepInEx/BepInExPack_Silksong/) |
| Required | [SSMP](https://github.com/Extremelyd1/SSMP) **0.3.1.0** |
| Required | [SSMPEnemySync](https://github.com/faarisaahmed/SSMPEnemySync) |
| Required | [Enemy Behavior API](https://github.com/faarisaahmed/enemy-behavior-api) |

**SSMPEnemySync is not optional.** Without it each player fights a private copy of every enemy, so
there is no shared enemy for both of you to be fighting over. This addon assumes enemies are shared
and authoritative on the scene host, which is exactly what that mod provides.

**The Enemy Behavior API supplies the attacks.** It discovers each enemy's real attack and movement
states by reflection; this addon only decides who may fire them and routes the request. Without it
the addon disables itself at startup and says so.

> [!IMPORTANT]
> **Both players need every one of these, at matching versions.** SSMP pairs networked addons by
> name and version, so a mismatched pair will not connect at all.

## Install

1. Install BepInEx, SSMP and SSMPEnemySync first, and confirm multiplayer works on its own.
2. Install the Enemy Behavior API into `BepInEx/plugins/` (any subfolder — BepInEx scans recursively).
3. Drop `SSMPEnemyPuppet.dll` into the folder that already contains `SSMP.dll`:

```
Hollow Knight Silksong/
└── BepInEx/plugins/
    ├── EnemyBehaviorApi/
    │   └── EnemyBehaviorApi.dll
    └── SSMP/                      ← whatever folder your SSMP.dll lives in
        ├── SSMP.dll
        ├── SSMPEnemySync.dll
        └── SSMPEnemyPuppet.dll    ← here
```

**That exact folder matters.** SSMP's addon loader only scans the directory its own assembly sits
in. Anywhere else and the addon is silently never loaded.

4. Start the game. The BepInEx log should show `[EnemyPuppet] Initialised.`

### Uninstalling

Delete `SSMPEnemyPuppet.dll`. Nothing else is modified.

## Use

Press **F7** in game.

- **Right-click** an enemy to select it
- **Take control** to claim it
- **Left-click** to move it — it walks there using its own patrol walk
- **Attack buttons** fire that enemy's real attacks, aimed at the other player

## How it works

### The scene host owns everything

SSMP replicates enemy position, animation and FSM state outward from whichever player is *scene
host*. An enemy driven locally by anyone else is overwritten by the host's next sync.

So nothing is driven locally. Every command travels **client → server → scene host**, is applied
where authority already lives, and replicates back through the sync SSMP already performs. If you
are the scene host, your own commands skip the round trip.

### The server stamps who sent what

SSMP gives client-side packet handlers the packet alone; only *server* handlers receive a sender id.
Since the machine arbitrating claims is a client, the sender has to travel inside the packet — and
the server overwrites that field with the truth on relay. Without that, any client could drive an
enemy someone else was using simply by claiming to be them.

### The marker and the aim point are different

Where you click says where the puppet should **stand**. What its attacks are pointed at is the
**other player's actual position**, looked up through SSMP.

Both are expressed by moving Hornet, because enemy attacks in Silksong are built to target her and
nothing else. She sits on the marker normally and snaps to the opponent for the moment an attack
needs aiming — held for a short window rather than a single frame, since attacks sample their target
over several frames as they wind up.

### Teams

Each claim records the controller's SSMP team alongside their player id, so a driven enemy's hits
can be credited to whoever is driving it. The registry exposes `TeamFor(entityId)` for this.

**The damage side is not built yet.** Enemy attacks carry `DamageHero`, which only damages Hornet —
so a driven enemy cannot currently hurt another enemy. `HealthManager.Hit(HitInstance)` is the way
in, and the ownership plumbing above is what a damage layer will need. That is the next piece.

## Edge cases handled

| Case | Behaviour |
|---|---|
| Two players claim the same enemy | First wins; the second is refused and told |
| A player disconnects mid-fight | Their claims are released, not stranded |
| Scene change | All claims dropped — SSMP reassigns entity ids per scene |
| Another mod already holds Override | Claim is rolled back rather than half-applied |
| Enemy Behavior API missing | Addon disables itself at startup with a reason |
| SSMP internals changed | Addon disables itself rather than throwing in the game loop |
| Enemy is not a synced entity | Refused with a log line, since SSMPEnemySync must recognise it |
| Commands for an enemy you don't hold | Ignored by the host |

## Status

Written against SSMP 0.3.1.0 by reading its internals, like SSMPEnemySync. On a different SSMP build
it disables itself and logs why rather than breaking your game.

**Not multiplayer-tested.** It compiles, deploys and its logic is written against SSMP's actual
shape, but no two-player session has been run. The parts most likely to be wrong are the ones that
only exist over a network: claim arbitration under contention, the local-player-id inference, and
whether a command applied on the host visibly replicates back to the other player.

Issues and findings welcome.
