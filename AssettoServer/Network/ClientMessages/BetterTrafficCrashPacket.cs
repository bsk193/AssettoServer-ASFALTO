using System.Numerics;

namespace AssettoServer.Network.ClientMessages;

/// <summary>ASFALTO BetterTraffic: a traffic car crashed / came to rest / was cleared (SessionId = the AI car).</summary>
[OnlineEvent(Key = "ASFALTO_BetterTrafficCrash")]
public class BetterTrafficCrashPacket : OnlineEvent<BetterTrafficCrashPacket>
{
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
}
