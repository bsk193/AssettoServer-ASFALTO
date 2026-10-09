# AssettoServer: ASFALTO fork

This is a fork of [compujuckel/AssettoServer](https://github.com/compujuckel/AssettoServer), used by ASFALTO traffic servers. It's licensed under AGPL-3.0, like upstream; this repository is the source for any server running this build.

- **Branch:** `asfalto/bettertraffic`, based on upstream `v0.0.55-pre44`.
- **Default behaviour:** with `AiParams.BetterTraffic.Enabled: false` (the default), the server behaves exactly like upstream.

## BetterTraffic

Livelier AI traffic, in the spirit of No Hesi's Traffic 2.0 plus our own ideas. Set it up in `extra_cfg.yml`:

```yaml
AiParams:
  BetterTraffic:
    Enabled: true
    SlowLaneSide: Right          # Right for right-hand traffic (EU/US), Left for left-hand traffic (JP/UK)
    DensityPreset: Realistic     # None, Light, Normal, Heavy, Realistic (rush hours), RushHour
```

Every setting below sits under `AiParams.BetterTraffic`, with the defaults shown.

### Driving

| Feature | What traffic does | Settings |
|---|---|---|
| **Personalities** | Each driver gets a personality when it spawns: Calm 30 %, Normal 45 %, Aggressive 15 % or Distracted 10 %. It sets the following gap, cruising speed, lane-change style, reaction time, indicator use, stubbornness and brake checks. You can add or change personalities in config. | `Personalities` (list: `Name`, `Weight`, `GapSecondsMin/Max`, `SpeedFactor`, `LaneChangeTimeFactor`, `OvertakeFactor`, `ReturnFactor`, `ReactionSeconds`, `IndicatorUse`, `StubbornChance`, `BrakeCheckChance`, `SwayFactor`) |
| **Following gaps** | Keeps a time gap to the car ahead, AI or player: Calm 1.5–2.0 s, Normal 1.1–1.6 s, Aggressive 0.7–1.0 s. The driver notices the car ahead slowing only after their reaction time, so distracted drivers brake late. | `FollowingGaps: true`, `MinFollowingGapMeters: 6` |
| **Lane changes** | Moves sideways into the next lane over 2.5–4 s (scaled by personality), with indicators unless the driver forgets them. Before moving it checks the lane is free of AI and players, blind spot included. It keeps its speed and colour, and backs out smoothly if the lane ends. Only lanes going the same way count: on a two-way road it never moves into the oncoming lane. | `LaneChanges: true`, `LaneChangeSecondsMin/Max: 2.5/4`, `LaneFreeBehindMeters: 25`, `LaneFreeAheadMeters: 30`, `LaneChangeCooldownSeconds: 6` |
| **Overtaking** | Pulls out to pass a slower car or player ahead, using the slow side only if that's the only free lane. Aggressive drivers pull out sooner, calm drivers later. | `OvertakeSpeedDeltaKph: 8`, `OvertakeLookaheadMeters: 60` |
| **Keep to the slow lane** | Drifts back to the slow lane when it's clear well ahead. | `ReturnToSlowLaneChance: 0.06` (per second) |
| **Yield on flash** | Flash your high beams (on, then off within 1.2 s) behind a car in your lane, up to 80 m ahead, and it moves to the slow lane once there's room. Stubborn drivers ignore it; how many depends on personality. | `YieldOnFlash: true`, `FlashWindowMilliseconds: 1200`, `YieldRangeMeters: 80`, `StubbornShare: 0` (extra share) |
| **Lane sway** | Each driver drifts slightly around the lane centre, at their own rhythm. Distracted drivers drift more. | `SwayMeters: 0.25` |
| **Brake checks** | Aggressive drivers, and occasionally normal ones, may brake-check a player who tailgates them: under 8 m behind for 2.5 s. It's a short −35 km/h slowdown, not a stop. Plugins can trigger one too. | `TailgateMeters: 8`, `TailgateSeconds: 2.5`, `BrakeCheckCooldownSeconds: 20`, `BrakeCheckDropKph: 35`, `BrakeCheckSeconds: 1.2` |
| **Spawning** | Traffic doesn't spawn in the first 150 m of a lane that starts from nothing, such as an on-ramp or side road. Those lanes often start off the road, so cars spawned there would come out of the dirt. | `NoSpawnNearLaneStartMeters: 150` |
| **Side roads** | No spawning on side roads either: open lanes shorter than 1 km that mostly have no neighbouring lane, such as connectors, service roads and ramps through the dirt. Traffic still turns onto them from the main road. The server log shows how many lane starts and side roads it found. | `NoSpawnOnSideRoads: true`, `SideRoadMaxMeters: 1000` |
| **Spawn jumps** | A traffic car that respawns, or switches to another traffic car, jumps to its new place; the game may also glide it there from the old spot. Players don't see that: the car stays hidden until it drives normally (about half a second). | `HideSpawnJumps: true` |
| **Density presets** | Fills the hourly traffic density, which AssettoServer blends between hours. Realistic is light at night with rush hours at 7–9 and 16–19; RushHour is busy all day. | `DensityPreset: None` |

### Crashes

Every player sees the same crash. Big crashes are simulated by the hitting player's game with CSP's rigid-body physics, so the car hits the real track: walls, kerbs, the ground. That game sends the car's position about 15 times a second, and the server shows it to everyone. If that player's game can't simulate it, or stops reporting, the server takes over with a simpler slide.

| Impact speed | What happens |
|---|---|
| Under 25 km/h | A tap: the car lifts off to 70 % speed with hazards on for 3 s, then carries on. No emergency stop. |
| 25–50 km/h | It steers aside with hazards on, onto the shoulder from the slow lane or otherwise to the slow edge of its lane, and rolls to a stop rather than braking hard. It waits 8 s, then rejoins when no player is close behind. |
| 50 km/h and up | The car is pushed, using momentum with traffic at 3000 kg against the player's car. It tumbles under real physics on the hitting player's game; the server's fallback slide spins it, and above 90 km/h can roll it (35 % chance). Parts come off. It then stays as a wreck. It's cleared once no player is within 250 m, or after 90 s. Hitting a wreck knocks it again. |

**Settings:** `CrashPhysics: true`, `TrafficMassKg: 3000`, `PlayerMassKg: 1400`, `MinorCrashKph: 25`, `HeavyCrashKph: 50`, `RolloverKph: 90`, `RolloverChance: 0.35`, `CrashSlideDeceleration: 6`, `PullOverAfterCrash: true`, `PullOverSeconds: 8`, `PullOverDeceleration: 2.5`, `PullOverShoulderMeters: 1.4`, `TapSlowdownSeconds: 3`, `WreckClearMeters: 250`, `WreckMaxSeconds: 90`, `ClientCrashPhysics: true`.

### Client effects

The server sends players a small script (`bettertraffic.lua`). Settings: `ClientEffects: true`, `ContactWeightFactor: 0.6`.
- **Sparks** whenever you hit traffic, and sparks plus dust for every crash anyone has, because the server tells everyone.
- **Damage:** crashed traffic looks damaged and dirty, and wrecks keep smoking until they're cleared.
- **Loose parts:** bumpers, mirrors, spoilers, lights and other parts come off crashed traffic and fly off, tumble, bounce with sparks and slide to a stop, on every player's screen. The script moves them itself, so they don't depend on CSP physics. If no part names match a traffic model, it uses its mid-sized pieces instead. Big hits also throw glass. They go back on when the wreck is cleared. Settings: `LooseParts: true`, `MaxLooseParts: 6`, and `LoosePartNames`, words matched against the traffic car's mesh names.
- **No floating wrecks:** if a simulated car comes to rest above the track (for example on the traffic car's own collider, when CSP can't switch it off), the game stops the simulation and puts it on the ground, measured with a track raycast. The server also won't leave a reported wreck hanging well above the road.
- **Smoke stays with the wreck:** effects follow the wreck's position, not the car slot. When the same slot later shows a fresh car of that model elsewhere, the smoke and damage don't follow it and its parts come back.
- **Log:** the script writes what it does, and anything that fails, to CSP's Lua debug log with the prefix `BetterTraffic:`.
- **Weight:** hitting traffic gives your own car an extra push back, as if the traffic car weighed `TrafficMassKg`. `ContactWeightFactor: 0` switches this off.

### For plugins

- `AiState.RequestYield()`
- `AiState.BrakeCheck()`
- `AiState.Crash(position, velocity, impactKph, contact)`, `AiState.ApplyClientPhysics(...)`, `AiState.SimulatedBy`
- `AiState.LaneChangeDirection`
- `AiState.PersonalityName`
- `AiState.CrashMode`
- `AiState.BetterTrafficCrash` (static event: car, phase, impact speed)
- `BetterTrafficFlashDetector.Yielded` (event)

### Changed files

- `Server/Ai/AiState.cs`
- `Server/Ai/AiBehavior.cs`
- `Server/Ai/AiModule.cs`
- `Server/Ai/BetterTrafficFlashDetector.cs`
- `Server/Ai/BetterTrafficCrashes.cs`
- `Server/Ai/bettertraffic.lua`
- `Network/ClientMessages/BetterTrafficCrashPacket.cs`
- `Server/Configuration/Extra/AiParams.cs`
- `Server/Configuration/Extra/BetterTrafficParams.cs`
- `AssettoServer.csproj` (embeds the Lua script)

### Testing

Checked with a headless test driver on a local server, not yet in a real game session. In one 2.5-minute run with flashing, tailgating and crashing all on:

| What | Result |
|---|---|
| Lane changes | 78 |
| Cars asked to yield | 10 |
| Brake checks | 1 |
| Pull-overs | 4, with 2 rejoins |
| Big crashes | 3, with 1 rollover; all came to rest |
| Errors | none |

The client script was checked against a stub of CSP's Lua API.

### Not done

- **Damaged models:** damaged ("Copart") skins need damaged art for each traffic model, so the client darkens and dents the existing model instead.
