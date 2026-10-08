using System.Collections.Generic;
using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace AssettoServer.Server.Configuration.Extra;

/// <summary>
/// ASFALTO BetterTraffic: livelier AI traffic. Everything is off unless <see cref="Enabled"/> is true; when it is off
/// the AI behaves exactly like upstream AssettoServer.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class BetterTrafficParams
{
    [YamlMember(Description = "Enable BetterTraffic (lane changes, yielding, personalities, following gaps, crashes, client effects)")]
    public bool Enabled { get; set; } = false;

    [YamlMember(Description = "Which side is the slow lane: Right for right-hand traffic (Europe, US), Left for left-hand traffic (Japan, UK)")]
    public TrafficSide SlowLaneSide { get; set; } = TrafficSide.Right;

    // ── lane changes ──
    [YamlMember(Description = "Lane changes to overtake slower traffic and to go back to the slow lane")]
    public bool LaneChanges { get; set; } = true;
    [YamlMember(Description = "Seconds a lane change takes (picked per driver between min and max, scaled by personality)")]
    public float LaneChangeSecondsMin { get; set; } = 2.5f;
    public float LaneChangeSecondsMax { get; set; } = 4.0f;
    [YamlMember(Description = "Overtake when the car ahead is at least this much slower than the driver wants to go (km/h, scaled by personality)")]
    public float OvertakeSpeedDeltaKph { get; set; } = 8;
    [YamlMember(Description = "Look this far ahead for slower cars to overtake (m)")]
    public float OvertakeLookaheadMeters { get; set; } = 60;
    [YamlMember(Description = "Chance per second that a driver moves back to the slow lane when it is free (scaled by personality)")]
    public float ReturnToSlowLaneChance { get; set; } = 0.06f;
    [YamlMember(Description = "Target lane must be free this far behind and ahead (m); players are checked too (blind spot)")]
    public float LaneFreeBehindMeters { get; set; } = 25;
    public float LaneFreeAheadMeters { get; set; } = 30;
    [YamlMember(Description = "Minimum seconds between two lane changes of the same driver")]
    public float LaneChangeCooldownSeconds { get; set; } = 6;

    // ── yielding ──
    [YamlMember(Description = "Traffic moves over when a player flashes the high beams behind it")]
    public bool YieldOnFlash { get; set; } = true;
    [YamlMember(Description = "A flash = high beams on then off within this time (ms)")]
    public int FlashWindowMilliseconds { get; set; } = 1200;
    [YamlMember(Description = "A flash reaches traffic in the same lane up to this far ahead (m)")]
    public float YieldRangeMeters { get; set; } = 80;
    [YamlMember(Description = "Extra share of drivers who ignore a flash, on top of the personality's own")]
    public float StubbornShare { get; set; } = 0.0f;

    // ── sway ──
    [YamlMember(Description = "Lane sway: drivers drift up to this far from the lane centre (m, scaled by personality)")]
    public float SwayMeters { get; set; } = 0.25f;

    // ── following ──
    [YamlMember(Description = "Keep a time gap to the car ahead (AI or player) instead of only braking when about to hit it")]
    public bool FollowingGaps { get; set; } = true;
    [YamlMember(Description = "The gap is never shorter than this (m), even at low speed")]
    public float MinFollowingGapMeters { get; set; } = 6;

    // ── brake checks ──
    [YamlMember(Description = "Brake check: speed drop (km/h) and duration (s), used by tailgated drivers and by plugins through AiState.BrakeCheck()")]
    public float BrakeCheckDropKph { get; set; } = 35;
    public float BrakeCheckSeconds { get; set; } = 1.2f;
    [YamlMember(Description = "A player is tailgating when closer than this behind in the same lane (m) ...")]
    public float TailgateMeters { get; set; } = 8;
    [YamlMember(Description = "... for this long (s)")]
    public float TailgateSeconds { get; set; } = 2.5f;
    [YamlMember(Description = "Minimum seconds between two brake checks of the same driver")]
    public float BrakeCheckCooldownSeconds { get; set; } = 20;

    // ── personalities ──
    [YamlMember(Description = "Driver personalities, picked at each spawn by Weight. Factors scale the settings above.")]
    public List<TrafficPersonality> Personalities { get; set; } =
    [
        new() { Name = "Calm", Weight = 30, GapSecondsMin = 1.5f, GapSecondsMax = 2.0f, SpeedFactor = 0.94f, LaneChangeTimeFactor = 1.25f, OvertakeFactor = 1.6f, ReturnFactor = 1.5f, ReactionSeconds = 0.5f, IndicatorUse = 1.0f, StubbornChance = 0.02f, BrakeCheckChance = 0, SwayFactor = 0.6f },
        new() { Name = "Normal", Weight = 45, GapSecondsMin = 1.1f, GapSecondsMax = 1.6f, SpeedFactor = 1.0f, LaneChangeTimeFactor = 1.0f, OvertakeFactor = 1.0f, ReturnFactor = 1.0f, ReactionSeconds = 0.7f, IndicatorUse = 0.95f, StubbornChance = 0.08f, BrakeCheckChance = 0.05f, SwayFactor = 1.0f },
        new() { Name = "Aggressive", Weight = 15, GapSecondsMin = 0.7f, GapSecondsMax = 1.0f, SpeedFactor = 1.08f, LaneChangeTimeFactor = 0.7f, OvertakeFactor = 0.5f, ReturnFactor = 0.4f, ReactionSeconds = 0.5f, IndicatorUse = 0.6f, StubbornChance = 0.3f, BrakeCheckChance = 0.5f, SwayFactor = 0.8f },
        new() { Name = "Distracted", Weight = 10, GapSecondsMin = 0.9f, GapSecondsMax = 2.0f, SpeedFactor = 0.97f, LaneChangeTimeFactor = 1.4f, OvertakeFactor = 1.3f, ReturnFactor = 0.7f, ReactionSeconds = 1.3f, IndicatorUse = 0.4f, StubbornChance = 0.25f, BrakeCheckChance = 0, SwayFactor = 2.0f },
    ];

    // ── crashes ──
    [YamlMember(Description = "Crash physics on the server (everyone sees the same thing): traffic that is hit slides, spins and can roll over")]
    public bool CrashPhysics { get; set; } = true;
    [YamlMember(Description = "How heavy traffic is in a crash (kg); the player car is assumed to weigh PlayerMassKg")]
    public float TrafficMassKg { get; set; } = 3000;
    public float PlayerMassKg { get; set; } = 1400;
    [YamlMember(Description = "Below this impact speed (km/h) a hit is a tap: traffic lifts off to 70 % speed with hazards for TapSlowdownSeconds and carries on")]
    public float MinorCrashKph { get; set; } = 25;
    public float TapSlowdownSeconds { get; set; } = 3;
    [YamlMember(Description = "Braking while pulling over (m/s^2): low = steers aside and rolls to a stop, high = stops dead")]
    public float PullOverDeceleration { get; set; } = 2.5f;
    [YamlMember(Description = "From this impact speed (km/h) traffic is pushed, slides and spins, and stays as a wreck; below it, it pulls over")]
    public float HeavyCrashKph { get; set; } = 50;
    [YamlMember(Description = "Above this impact speed (km/h) traffic can roll over, with RolloverChance")]
    public float RolloverKph { get; set; } = 90;
    public float RolloverChance { get; set; } = 0.35f;
    [YamlMember(Description = "Tyre grip while sliding after a crash (m/s^2)")]
    public float CrashSlideDeceleration { get; set; } = 6;
    [YamlMember(Description = "After a minor crash, pull over onto the shoulder (or the slow side of the lane) with hazards, then rejoin")]
    public bool PullOverAfterCrash { get; set; } = true;
    [YamlMember(Description = "Seconds a car stays pulled over after a minor crash")]
    public float PullOverSeconds { get; set; } = 8;
    [YamlMember(Description = "Wrecked cars (big crash) stay with hazards on until no player is within WreckClearMeters, at most WreckMaxSeconds")]
    public float WreckClearMeters { get; set; } = 250;
    public float WreckMaxSeconds { get; set; } = 90;

    // ── spawning ──
    [YamlMember(Description = "Don't spawn traffic within this distance of the start of a lane that begins from nothing (on-ramps, side roads): these often start off the road, so cars would come out of the dirt (0 = off)")]
    public float NoSpawnNearLaneStartMeters { get; set; } = 150;

    // ── density ──
    [YamlMember(Description = "Traffic density preset: None (use HourlyTrafficDensity / TrafficDensity as set), Light, Normal, Heavy, Realistic (follows the time of day with rush hours), RushHour")]
    public TrafficDensityPreset DensityPreset { get; set; } = TrafficDensityPreset.None;

    // ── client effects ──
    [YamlMember(Description = "Send the BetterTraffic client script: sparks and smoke on crashes, damaged look on crashed traffic, heavier contact")]
    public bool ClientEffects { get; set; } = true;
    [YamlMember(Description = "Extra push on your own car when you hit traffic, to feel the traffic's weight (0 = off, 1 = full TrafficMassKg)")]
    public float ContactWeightFactor { get; set; } = 0.6f;
    [YamlMember(Description = "Big crashes are simulated by the hitting player's game (real physics against the track: tumbling, walls, ground) and shown to everybody. Off = the server's simple slide only")]
    public bool ClientCrashPhysics { get; set; } = true;
    [YamlMember(Description = "Parts come off crashed traffic (bumpers, mirrors, spoilers, ...) and bounce around")]
    public bool LooseParts { get; set; } = true;
    [YamlMember(Description = "Name parts of traffic car models that can come off (matched case-insensitively against mesh and node names)")]
    public List<string> LoosePartNames { get; set; } = ["bumper", "mirror", "spoiler", "wing", "lip", "splitter", "diffuser", "skirt", "plate", "exhaust", "light", "lamp", "hood", "bonnet", "door"];
    [YamlMember(Description = "At most this many parts come off one car (fewer for slower crashes)")]
    public int MaxLooseParts { get; set; } = 6;
}

[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class TrafficPersonality
{
    public string Name { get; set; } = "Normal";
    [YamlMember(Description = "Relative chance of this personality")]
    public float Weight { get; set; } = 1;
    [YamlMember(Description = "Following gap to the car ahead (s), picked per driver between min and max")]
    public float GapSecondsMin { get; set; } = 1.1f;
    public float GapSecondsMax { get; set; } = 1.6f;
    [YamlMember(Description = "Multiplies the driver's cruising speed")]
    public float SpeedFactor { get; set; } = 1;
    [YamlMember(Description = "Multiplies the lane change duration")]
    public float LaneChangeTimeFactor { get; set; } = 1;
    [YamlMember(Description = "Multiplies OvertakeSpeedDeltaKph (lower = overtakes sooner)")]
    public float OvertakeFactor { get; set; } = 1;
    [YamlMember(Description = "Multiplies ReturnToSlowLaneChance")]
    public float ReturnFactor { get; set; } = 1;
    [YamlMember(Description = "Reaction time (s): how late the driver starts braking for a slower car ahead")]
    public float ReactionSeconds { get; set; } = 0.7f;
    [YamlMember(Description = "Chance the driver uses indicators for a lane change")]
    public float IndicatorUse { get; set; } = 1;
    [YamlMember(Description = "Chance the driver ignores a flash")]
    public float StubbornChance { get; set; } = 0.1f;
    [YamlMember(Description = "Chance the driver brake-checks a tailgating player")]
    public float BrakeCheckChance { get; set; } = 0;
    [YamlMember(Description = "Multiplies SwayMeters")]
    public float SwayFactor { get; set; } = 1;
}

public enum TrafficSide
{
    Right,
    Left,
}

public enum TrafficDensityPreset
{
    None,
    Light,
    Normal,
    Heavy,
    Realistic,
    RushHour,
}

public static class TrafficDensityPresets
{
    /// <summary>24 hourly densities for a preset, or null for <see cref="TrafficDensityPreset.None"/>.</summary>
    public static List<float>? Hourly(TrafficDensityPreset preset) => preset switch
    {
        TrafficDensityPreset.Light => Flat(0.45f),
        TrafficDensityPreset.Normal => Flat(0.75f),
        TrafficDensityPreset.Heavy => Flat(1.0f),
        //                0     1     2     3     4     5     6     7     8     9    10    11    12    13    14    15    16    17    18    19    20    21    22    23
        TrafficDensityPreset.Realistic =>
            [0.25f, 0.18f, 0.15f, 0.15f, 0.2f, 0.35f, 0.6f, 0.9f, 1.0f, 0.8f, 0.65f, 0.65f, 0.7f, 0.7f, 0.65f, 0.7f, 0.85f, 1.0f, 1.0f, 0.8f, 0.6f, 0.5f, 0.4f, 0.3f],
        TrafficDensityPreset.RushHour =>
            [0.5f, 0.4f, 0.35f, 0.35f, 0.45f, 0.65f, 0.9f, 1.0f, 1.0f, 1.0f, 0.85f, 0.85f, 0.9f, 0.9f, 0.85f, 0.9f, 1.0f, 1.0f, 1.0f, 1.0f, 0.85f, 0.75f, 0.65f, 0.55f],
        _ => null,
    };

    private static List<float> Flat(float value)
    {
        var list = new List<float>(24);
        for (var i = 0; i < 24; i++) list.Add(value);
        return list;
    }
}
