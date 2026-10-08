using JetBrains.Annotations;
using YamlDotNet.Serialization;

namespace AssettoServer.Server.Configuration.Extra;

/// <summary>
/// ASFALTO Traffic 2.0: livelier AI traffic. Everything is off unless <see cref="Enabled"/> is true, in which case
/// the AI behaves exactly like upstream AssettoServer.
/// </summary>
[UsedImplicitly(ImplicitUseKindFlags.Assign, ImplicitUseTargetFlags.WithMembers)]
public class Traffic2Params
{
    [YamlMember(Description = "Enable Traffic 2.0 (lane changes, yielding, lane sway, driver personalities)")]
    public bool Enabled { get; set; } = false;

    [YamlMember(Description = "Which side is the slow lane: Right for right-hand traffic (Europe, US), Left for left-hand traffic (Japan, UK)")]
    public TrafficSide SlowLaneSide { get; set; } = TrafficSide.Right;

    [YamlMember(Description = "Lane changes to overtake slower traffic and to go back to the slow lane")]
    public bool LaneChanges { get; set; } = true;
    [YamlMember(Description = "Seconds a lane change takes (picked per driver between min and max)")]
    public float LaneChangeSecondsMin { get; set; } = 2.5f;
    public float LaneChangeSecondsMax { get; set; } = 4.0f;
    [YamlMember(Description = "Overtake when the car ahead is at least this much slower than the driver wants to go (km/h)")]
    public float OvertakeSpeedDeltaKph { get; set; } = 8;
    [YamlMember(Description = "Look this far ahead for slower cars to overtake (m)")]
    public float OvertakeLookaheadMeters { get; set; } = 60;
    [YamlMember(Description = "Chance per second that a driver moves back to the slow lane when it is free (keep-slow-lane rule)")]
    public float ReturnToSlowLaneChance { get; set; } = 0.06f;
    [YamlMember(Description = "Target lane must be free this far behind and ahead (m); players are checked too (blind spot)")]
    public float LaneFreeBehindMeters { get; set; } = 25;
    public float LaneFreeAheadMeters { get; set; } = 30;
    [YamlMember(Description = "Minimum seconds between two lane changes of the same driver")]
    public float LaneChangeCooldownSeconds { get; set; } = 6;

    [YamlMember(Description = "Traffic moves over when a player flashes the high beams behind it")]
    public bool YieldOnFlash { get; set; } = true;
    [YamlMember(Description = "A flash = high beams on then off within this time (ms)")]
    public int FlashWindowMilliseconds { get; set; } = 1200;
    [YamlMember(Description = "A flash reaches traffic in the same lane up to this far ahead (m)")]
    public float YieldRangeMeters { get; set; } = 80;
    [YamlMember(Description = "Share of drivers who ignore a flash (stubborn drivers)")]
    public float StubbornShare { get; set; } = 0.1f;

    [YamlMember(Description = "Lane sway: drivers drift up to this far from the lane centre (m)")]
    public float SwayMeters { get; set; } = 0.25f;

    [YamlMember(Description = "Brake check: speed drop (km/h) and duration (s) for AiState.BrakeCheck(), e.g. used by plugins")]
    public float BrakeCheckDropKph { get; set; } = 35;
    public float BrakeCheckSeconds { get; set; } = 1.2f;
}

public enum TrafficSide
{
    Right,
    Left,
}
