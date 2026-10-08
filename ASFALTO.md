# AssettoServer: ASFALTO fork

This is a fork of [compujuckel/AssettoServer](https://github.com/compujuckel/AssettoServer), used by ASFALTO traffic servers. It's licensed under AGPL-3.0, like upstream; this repository is the source for any server running this build.

- **Branch:** `asfalto/traffic2`, based on upstream `v0.0.55-pre44`.
- **Default behaviour:** with `AiParams.Traffic2.Enabled: false` (the default), the server behaves exactly like upstream.

## Traffic 2.0

Livelier AI traffic, set up in `extra_cfg.yml`:

```yaml
AiParams:
  Traffic2:
    Enabled: true
    SlowLaneSide: Right          # Right for right-hand traffic (EU/US), Left for left-hand traffic (JP/UK)
```

| Feature | What it does | Settings |
|---|---|---|
| **Lane changes** | Cars blend sideways into the neighbouring lane over 2.5–4 s (a duration picked per driver), with indicators. Before moving they check that the target lane is free of AI and of players, including the blind spot. They then continue on that lane's spline at the matching place, keeping their speed and colour. A lane change aborts smoothly if the target lane ends. | `LaneChanges`, `LaneChangeSecondsMin/Max`, `LaneFreeBehindMeters`, `LaneFreeAheadMeters`, `LaneChangeCooldownSeconds` |
| **Overtaking** | A driver blocked by a slower car or player ahead moves to the fast side, or undertakes if that's the only free lane. | `OvertakeSpeedDeltaKph`, `OvertakeLookaheadMeters` |
| **Keep to the slow lane** | Drivers drift back to the slow side when it's free well ahead. | `ReturnToSlowLaneChance` |
| **Yield on flash** | Flash your high beams (on, then off within 1.2 s) behind a car in the same lane, and it moves to the slow side as soon as that lane is free. Some drivers are stubborn and ignore it. | `YieldOnFlash`, `FlashWindowMilliseconds`, `YieldRangeMeters`, `StubbornShare` |
| **Lane sway** | Each driver drifts slowly around the lane centre, with their own amplitude and rhythm. | `SwayMeters` |
| **Brake check** | `AiState.BrakeCheck()` makes a car slow down sharply for a moment. It isn't a stop. Plugins can trigger it. | `BrakeCheckDropKph`, `BrakeCheckSeconds` |

**APIs for plugins:**
- `AiState.RequestYield()`
- `AiState.BrakeCheck()`
- `AiState.LaneChangeDirection`
- `Traffic2FlashDetector.Yielded` (event)

**Changed files:**
- `Server/Ai/AiState.cs`
- `Server/Ai/Traffic2FlashDetector.cs`
- `Server/Ai/AiModule.cs`
- `Server/Configuration/Extra/AiParams.cs`
- `Server/Configuration/Extra/Traffic2Params.cs`

**Testing:** checked with a headless test driver against a local server.
- **Lane changes:** about 70 in 2.5 minutes.
- **Smoothness:** position glitches between updates at the same rate as upstream.
- **Flashes:** 13 flashes led to 7 cars moving over. Four were already in the slow lane; the rest found the slow lane busy.

**Not yet:** pulling over to the shoulder after a crash; crash physics mirrored to all clients; damaged variants.
