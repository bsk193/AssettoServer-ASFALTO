using System.Numerics;

namespace AssettoServer.Network.ClientMessages;

/// <summary>
/// ASFALTO BetterTraffic, server to clients: a traffic car crashed / came to rest / was cleared.
/// Sent from the server (SessionId 255, CSP passes sender = nil); <see cref="CarIndex"/> is the traffic car.
/// </summary>
[OnlineEvent(Key = "ASFALTO_BetterTrafficCrash")]
public class BetterTrafficCrashPacket : OnlineEvent<BetterTrafficCrashPacket>
{
    [OnlineEventField(Name = "carIndex")]
    public byte CarIndex;
    /// <summary>0 = hit, 1 = came to rest, 2 = cleared</summary>
    [OnlineEventField(Name = "phase")]
    public byte Phase;
    /// <summary>AiCrashMode: 1 = pull over, 2 = sliding, 3 = wrecked</summary>
    [OnlineEventField(Name = "mode")]
    public byte Mode;
    [OnlineEventField(Name = "position")]
    public Vector3 Position;
    [OnlineEventField(Name = "impactKph")]
    public float ImpactKph;
    /// <summary>Session id of the player whose game simulates the crashed car (255 = the server does)</summary>
    [OnlineEventField(Name = "simulatedBy")]
    public byte SimulatedBy;
}

/// <summary>
/// ASFALTO BetterTraffic, client to server: the crashed traffic car as simulated by this player's game (CSP rigid body
/// physics against the track). The server shows it to everybody.
/// </summary>
[OnlineEvent(Key = "ASFALTO_BetterTrafficPhysics")]
public class BetterTrafficPhysicsPacket : OnlineEvent<BetterTrafficPhysicsPacket>
{
    [OnlineEventField(Name = "carIndex")]
    public byte CarIndex;
    [OnlineEventField(Name = "position")]
    public Vector3 Position;
    /// <summary>AC rotation (heading, pitch, roll), like CarStatus.Rotation</summary>
    [OnlineEventField(Name = "rotation")]
    public Vector3 Rotation;
    [OnlineEventField(Name = "velocity")]
    public Vector3 Velocity;
    /// <summary>Impact speed of the hit that started the simulation (km/h), only in the first message</summary>
    [OnlineEventField(Name = "impactKph")]
    public float ImpactKph;
    /// <summary>1 = the car has come to rest, the simulation stopped</summary>
    [OnlineEventField(Name = "resting")]
    public byte Resting;
}
