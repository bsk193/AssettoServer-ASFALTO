using System;
using System.Collections.Generic;
using System.Numerics;
using AssettoServer.Server.Configuration;
using AssettoServer.Shared.Network.Packets.Outgoing;

namespace AssettoServer.Server.Ai;

/// <summary>
/// ASFALTO Traffic 2.0: a player flashing the high beams (on, then off within FlashWindowMilliseconds) asks the
/// nearest traffic car ahead in the same lane to move over (<see cref="AiState.RequestYield"/>).
/// </summary>
public class Traffic2FlashDetector
{
    private readonly ACServerConfiguration _configuration;
    private readonly EntryCarManager _entryCarManager;
    private readonly SessionManager _sessionManager;
    private readonly Dictionary<byte, (bool HighBeams, long OnSince)> _state = new();
    private readonly List<AiState> _states = [];

    public Traffic2FlashDetector(ACServerConfiguration configuration, EntryCarManager entryCarManager, SessionManager sessionManager, ACServer server)
    {
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _sessionManager = sessionManager;
        server.Update += OnUpdate;
    }

    /// <summary>Raised when a flash made a traffic car yield (player session id, AI state).</summary>
    public event Action<EntryCar, AiState>? Yielded;

    private void OnUpdate(object? sender, EventArgs args)
    {
        var t2 = _configuration.Extra.AiParams.Traffic2;
        if (!t2.Enabled || !t2.YieldOnFlash) return;
        var now = _sessionManager.ServerTimeMilliseconds;

        foreach (var car in _entryCarManager.EntryCars)
        {
            if (car.AiControlled || car.Client?.HasSentFirstUpdate != true) continue;
            var highBeams = (car.Status.StatusFlag & CarStatusFlags.LightsOn) != 0
                            && (car.Status.StatusFlag & CarStatusFlags.HighBeamsOff) == 0;
            _state.TryGetValue(car.SessionId, out var previous);
            if (highBeams && !previous.HighBeams)
            {
                _state[car.SessionId] = (true, now);
            }
            else if (!highBeams && previous.HighBeams)
            {
                _state[car.SessionId] = (false, 0);
                if (now - previous.OnSince <= t2.FlashWindowMilliseconds) OnFlash(car, t2.YieldRangeMeters);
            }
        }
    }

    private void OnFlash(EntryCar player, float range)
    {
        var velocity = player.Status.Velocity;
        if (velocity.LengthSquared() < 25) return; // only while driving
        var forward = Vector3.Normalize(velocity);

        AiState? target = null;
        var best = range;
        foreach (var slot in _entryCarManager.EntryCars)
        {
            if (!slot.AiControlled) continue;
            _states.Clear();
            slot.GetInitializedStates(_states);
            foreach (var ai in _states)
            {
                var rel = ai.Status.Position - player.Status.Position;
                if (MathF.Abs(rel.Y) > 3) continue;
                var lon = Vector3.Dot(rel, forward);
                if (lon < 4 || lon > best) continue;
                var lat = (rel - forward * lon).Length();
                if (lat > 2.0f) continue; // same lane
                if (ai.Status.Velocity.LengthSquared() > 1 && Vector3.Dot(Vector3.Normalize(ai.Status.Velocity), forward) < 0.7f) continue;
                best = lon;
                target = ai;
            }
        }

        var accepted = target != null && target.RequestYield();
        Serilog.Log.Debug("Traffic2: flash by {Player}: {Result}", player.Client?.Name, target == null ? "no car ahead in lane" : accepted ? "yield requested" : "driver ignores it");
        if (accepted) Yielded?.Invoke(player, target!);
    }
}
