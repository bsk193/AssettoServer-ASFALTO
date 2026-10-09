using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Numerics;
using System.Reflection;
using AssettoServer.Network.ClientMessages;
using AssettoServer.Network.Tcp;
using AssettoServer.Server.Configuration;
using Serilog;

namespace AssettoServer.Server.Ai;

/// <summary>
/// ASFALTO BetterTraffic crashes:
/// - player-vs-traffic collisions become crashes (<see cref="AiState.Crash"/>);
/// - big crashes are simulated by the hitting player's game (CSP rigid body against the real track) and relayed to
///   everybody (<see cref="BetterTrafficPhysicsPacket"/>), with a server-side fallback;
/// - every client is told about crashes (<see cref="BetterTrafficCrashPacket"/>) and gets the effects script.
/// Everything runs on the server update loop. Inactive unless AiParams.BetterTraffic.Enabled.
/// </summary>
public class BetterTrafficCrashes
{
    private readonly ACServerConfiguration _configuration;
    private readonly EntryCarManager _entryCarManager;
    private readonly ConcurrentQueue<(ACTcpClient Client, CollisionEventArgs Args)> _collisions = new();
    private readonly ConcurrentQueue<(byte Owner, BetterTrafficPhysicsPacket Packet)> _physics = new();

    public BetterTrafficCrashes(ACServerConfiguration configuration, EntryCarManager entryCarManager, CSPServerScriptProvider scriptProvider,
        CSPClientMessageTypeManager messageTypes, ACServer server)
    {
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        var bt = configuration.Extra.AiParams.BetterTraffic;
        if (!bt.Enabled) return;

        if (bt.CrashPhysics)
        {
            _entryCarManager.ClientConnected += (client, _) => client.Collision += (sender, args) => _collisions.Enqueue((sender, args));
            messageTypes.RegisterOnlineEvent<BetterTrafficPhysicsPacket>((client, packet) => _physics.Enqueue((client.SessionId, packet)));
            server.Update += (_, _) => ProcessQueues();
            AiState.BetterTrafficCrash += OnCrash;
        }

        if (bt.ClientEffects)
        {
            // "|" separated: CSP would split a comma separated value into a list
            var aiSlots = string.Join("|", _entryCarManager.EntryCars.Where(c => c.AiMode != AiMode.None).Select(c => c.SessionId));
            scriptProvider.AddScript(Assembly.GetExecutingAssembly().GetManifestResourceStream("AssettoServer.Server.Ai.bettertraffic.lua")!, "bettertraffic.lua",
                new Dictionary<string, object>
                {
                    ["AI_SLOTS"] = aiSlots,
                    ["TRAFFIC_MASS"] = bt.TrafficMassKg.ToString(CultureInfo.InvariantCulture),
                    ["CONTACT_WEIGHT"] = bt.ContactWeightFactor.ToString(CultureInfo.InvariantCulture),
                    ["CLIENT_PHYSICS"] = bt.CrashPhysics && bt.ClientCrashPhysics ? 1 : 0,
                    ["HEAVY_KPH"] = bt.HeavyCrashKph.ToString(CultureInfo.InvariantCulture),
                    ["PARTS"] = bt.LooseParts ? 1 : 0,
                    ["PART_NAMES"] = string.Join("|", bt.LoosePartNames),
                    ["MAX_PARTS"] = bt.MaxLooseParts,
                    ["HIDE_JUMPS"] = bt.HideSpawnJumps ? 1 : 0,
                });
        }
    }

    private void ProcessQueues()
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

        while (_physics.TryDequeue(out var report))
        {
            try
            {
                var (owner, p) = report;
                if (p.CarIndex >= _entryCarManager.EntryCars.Length) continue;
                var car = _entryCarManager.EntryCars[p.CarIndex];
                if (!car.AiControlled) continue;
                var (state, distanceSquared) = car.GetClosestAiState(p.Position);
                if (state == null || distanceSquared > 40 * 40) continue;
                state.ApplyClientPhysics(owner, p.Position, p.Rotation, p.Velocity, p.ImpactKph, p.Resting != 0);
            }
            catch (Exception ex)
            {
                Log.Error(ex, "BetterTraffic: crash physics report failed");
            }
        }
    }

    private void OnCrash(AiState state, AiCrashPhase phase, float impactKph)
    {
        _entryCarManager.BroadcastPacket(new BetterTrafficCrashPacket
        {
            CarIndex = state.EntryCar.SessionId,
            Phase = (byte)phase,
            Mode = (byte)state.CrashMode,
            Position = state.Status.Position,
            ImpactKph = impactKph,
            SimulatedBy = state.SimulatedBy,
        });
    }
}
