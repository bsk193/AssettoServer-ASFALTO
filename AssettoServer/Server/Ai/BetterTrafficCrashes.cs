using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using AssettoServer.Network.ClientMessages;
using AssettoServer.Network.Tcp;
using AssettoServer.Server.Configuration;
using Serilog;

namespace AssettoServer.Server.Ai;

/// <summary>
/// ASFALTO BetterTraffic: turns player-vs-traffic collisions into crashes (<see cref="AiState.Crash"/>) on the server
/// update loop, tells every client about them, and sends the client script with the crash effects.
/// Inactive unless AiParams.BetterTraffic.Enabled.
/// </summary>
public class BetterTrafficCrashes
{
    private readonly ACServerConfiguration _configuration;
    private readonly EntryCarManager _entryCarManager;
    private readonly ConcurrentQueue<(ACTcpClient Client, CollisionEventArgs Args)> _collisions = new();

    public BetterTrafficCrashes(ACServerConfiguration configuration, EntryCarManager entryCarManager, CSPServerScriptProvider scriptProvider, ACServer server)
    {
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        var bt = configuration.Extra.AiParams.BetterTraffic;
        if (!bt.Enabled) return;

        if (bt.CrashPhysics)
        {
            _entryCarManager.ClientConnected += (client, _) => client.Collision += (sender, args) => _collisions.Enqueue((sender, args));
            server.Update += (_, _) => ProcessCollisions();
            AiState.BetterTrafficCrash += OnCrash;
        }

        if (bt.ClientEffects)
        {
            var aiSlots = string.Join(",", _entryCarManager.EntryCars.Where(c => c.AiMode != AiMode.None).Select(c => c.SessionId));
            scriptProvider.AddScript(Assembly.GetExecutingAssembly().GetManifestResourceStream("AssettoServer.Server.Ai.bettertraffic.lua")!, "bettertraffic.lua",
                new Dictionary<string, object>
                {
                    ["AI_SLOTS"] = aiSlots,
                    ["TRAFFIC_MASS"] = bt.TrafficMassKg,
                    ["CONTACT_WEIGHT"] = bt.ContactWeightFactor,
                });
        }
    }

    private void ProcessCollisions()
    {
        while (_collisions.TryDequeue(out var collision))
        {
            try
            {
                var (client, args) = collision;
                if (args.TargetCar?.AiControlled != true) continue;
                var (state, distanceSquared) = args.TargetCar.GetClosestAiState(client.EntryCar.Status.Position);
                if (state == null || distanceSquared > 25 * 25) continue;
                state.Crash(client.EntryCar.Status.Position, client.EntryCar.Status.Velocity, args.Speed, args.Position);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "BetterTraffic: crash handling failed");
            }
        }
    }

    private void OnCrash(AiState state, AiCrashPhase phase, float impactKph)
    {
        _entryCarManager.BroadcastPacket(new BetterTrafficCrashPacket
        {
            SessionId = state.EntryCar.SessionId,
            Phase = (byte)phase,
            Mode = (byte)state.CrashMode,
            Position = state.Status.Position,
            ImpactKph = impactKph,
        });
    }
}
