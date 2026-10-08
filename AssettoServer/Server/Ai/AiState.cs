using System;
using System.Collections.Generic;
using System.Drawing;
using System.Numerics;
using AssettoServer.Server.Ai.Splines;
using AssettoServer.Server.Configuration;
using AssettoServer.Server.Configuration.Extra;
using AssettoServer.Server.Weather;
using AssettoServer.Shared.Model;
using AssettoServer.Shared.Network.Packets.Outgoing;
using AssettoServer.Shared.Utils;
using AssettoServer.Utils;
using JPBotelho;
using Serilog;
using SunCalcNet.Model;

namespace AssettoServer.Server.Ai;

public class AiState : IDisposable
{
    public CarStatus Status { get; } = new();
    public bool Initialized { get; private set; }

    public int CurrentSplinePointId
    {
        get;
        private set
        {
            _spline.SlowestAiStates.Enter(value, this);
            _spline.SlowestAiStates.Leave(field, this);
            field = value;
        }
    }

    public long SpawnProtectionEnds { get; set; }
    public float SafetyDistanceSquared { get; set; } = 20 * 20;
    public float Acceleration { get; set; }
    public float CurrentSpeed { get; private set; }
    public float TargetSpeed { get; private set; }
    public float InitialMaxSpeed { get; private set; }
    public float MaxSpeed { get; private set; }
    public Color Color { get; private set; }
    public byte SpawnCounter { get; private set; }
    public float ClosestAiObstacleDistance { get; private set; }
    public EntryCar EntryCar { get; }

    private const float WalkingSpeed = 10 / 3.6f;

    private Vector3 _startTangent;
    private Vector3 _endTangent;

    private float _currentVecLength;
    private float _currentVecProgress;
    private long _lastTick;
    private bool _stoppedForObstacle;
    private long _stoppedForObstacleSince;
    private long _ignoreObstaclesUntil;
    private long _stoppedForCollisionUntil;
    private long _obstacleHonkStart;
    private long _obstacleHonkEnd;
    private CarStatusFlags _indicator = 0;
    private int _nextJunctionId;
    private bool _junctionPassed;
    private float _endIndicatorDistance;
    private float _minObstacleDistance;
    private double _randomTwilight;

    // ── ASFALTO Traffic 2.0 (AiParams.Traffic2) ──
    private int _lcDirection;          // 0 = none, -1 = towards LeftId, +1 = towards RightId
    private float _lcProgress;         // 0..1
    private float _lcSeconds;          // this driver's lane change duration
    private long _lcCooldownUntil;
    private int _lcRequest;            // set by obstacle detection / yield, started in Update
    private long _yieldUntil;
    private long _brakeCheckUntil;
    private bool _stubborn;
    private float _swayPhase;
    private float _swayFrequency;
    private float _swayAmplitude;
    private CarStatusFlags _laneChangeIndicator;

    /// <summary>A lane change is in progress (direction -1 left, +1 right).</summary>
    public int LaneChangeDirection => _lcDirection;

    private readonly ACServerConfiguration _configuration;
    private readonly SessionManager _sessionManager;
    private readonly EntryCarManager _entryCarManager;
    private readonly WeatherManager _weatherManager;
    private readonly AiSpline _spline;
    private readonly JunctionEvaluator _junctionEvaluator;

    private static readonly List<Color> CarColors =
    [
        Color.FromArgb(13, 17, 22),
        Color.FromArgb(19, 24, 31),
        Color.FromArgb(28, 29, 33),
        Color.FromArgb(12, 13, 24),
        Color.FromArgb(11, 20, 33),
        Color.FromArgb(151, 154, 151),
        Color.FromArgb(153, 157, 160),
        Color.FromArgb(194, 196, 198),
        Color.FromArgb(234, 234, 234),
        Color.FromArgb(255, 255, 255),
        Color.FromArgb(182, 17, 27),
        Color.FromArgb(218, 25, 24),
        Color.FromArgb(73, 17, 29),
        Color.FromArgb(35, 49, 85),
        Color.FromArgb(28, 53, 81),
        Color.FromArgb(37, 58, 167),
        Color.FromArgb(21, 92, 45),
        Color.FromArgb(18, 46, 43)
    ];

    public AiState(EntryCar entryCar, SessionManager sessionManager, WeatherManager weatherManager, ACServerConfiguration configuration, EntryCarManager entryCarManager, AiSpline spline)
    {
        EntryCar = entryCar;
        _sessionManager = sessionManager;
        _weatherManager = weatherManager;
        _configuration = configuration;
        _entryCarManager = entryCarManager;
        _spline = spline;
        _junctionEvaluator = new JunctionEvaluator(spline);

        _lastTick = _sessionManager.ServerTimeMilliseconds;
    }

    ~AiState()
    {
        Despawn();
    }
    
    public void Dispose()
    {
        Despawn();
        GC.SuppressFinalize(this);
    }

    public void Despawn()
    {
        Initialized = false;
        _spline.SlowestAiStates.Leave(CurrentSplinePointId, this);
    }

    private void SetRandomSpeed()
    {
        float variation = _configuration.Extra.AiParams.MaxSpeedMs * _configuration.Extra.AiParams.MaxSpeedVariationPercent;

        float fastLaneOffset = 0;
        if (_spline.Points[CurrentSplinePointId].LeftId >= 0)
        {
            fastLaneOffset = _configuration.Extra.AiParams.RightLaneOffsetMs;
        }
        InitialMaxSpeed = _configuration.Extra.AiParams.MaxSpeedMs + fastLaneOffset - (variation / 2) + (float)Random.Shared.NextDouble() * variation;
        CurrentSpeed = InitialMaxSpeed;
        TargetSpeed = InitialMaxSpeed;
        MaxSpeed = InitialMaxSpeed;
    }

    private void SetRandomColor()
    {
        Color = CarColors[Random.Shared.Next(CarColors.Count)];
    }

    public void Teleport(int pointId)
    {
        _junctionEvaluator.Clear();
        CurrentSplinePointId = pointId;
        if (!_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPointId))
            throw new InvalidOperationException($"Cannot get next spline point for {CurrentSplinePointId}");
        _currentVecLength = (_spline.Points[nextPointId].Position - _spline.Points[CurrentSplinePointId].Position).Length();
        _currentVecProgress = 0;
            
        CalculateTangents();
        
        SetRandomSpeed();
        SetRandomColor();

        var minDist = _configuration.Extra.AiParams.MinAiSafetyDistanceSquared;
        var maxDist = _configuration.Extra.AiParams.MaxAiSafetyDistanceSquared;
        if (_configuration.Extra.AiParams.LaneCountSpecificOverrides.TryGetValue(_spline.GetLanes(CurrentSplinePointId).Length, out var overrides))
        {
            minDist = overrides.MinAiSafetyDistanceSquared;
            maxDist = overrides.MaxAiSafetyDistanceSquared;
        }
        
        if (EntryCar.MinAiSafetyDistanceMetersSquared.HasValue)
            minDist = EntryCar.MinAiSafetyDistanceMetersSquared.Value;
        if (EntryCar.MaxAiSafetyDistanceMetersSquared.HasValue)
            maxDist = EntryCar.MaxAiSafetyDistanceMetersSquared.Value;

        SpawnProtectionEnds = _sessionManager.ServerTimeMilliseconds + Random.Shared.Next(EntryCar.AiMinSpawnProtectionTimeMilliseconds, EntryCar.AiMaxSpawnProtectionTimeMilliseconds);
        SafetyDistanceSquared = Random.Shared.Next((int)Math.Round(minDist * (1.0f / _configuration.Extra.AiParams.TrafficDensity)),
            (int)Math.Round(maxDist * (1.0f / _configuration.Extra.AiParams.TrafficDensity)));
        _stoppedForCollisionUntil = 0;
        _ignoreObstaclesUntil = 0;
        _obstacleHonkEnd = 0;
        _obstacleHonkStart = 0;
        _indicator = 0;
        _randomTwilight = Random.Shared.NextSingle(0, 12) * Math.PI / 180.0;
        _nextJunctionId = -1;
        _junctionPassed = false;
        _endIndicatorDistance = 0;
        _lastTick = _sessionManager.ServerTimeMilliseconds;
        _minObstacleDistance = Random.Shared.Next(8, 13);
        ResetTraffic2();
        SpawnCounter++;
        Initialized = true;
        Update();
    }

    private void CalculateTangents()
    {
        if (!_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPointId))
            throw new InvalidOperationException("Cannot get next spline point");

        var points = _spline.Points;
        
        if (_junctionEvaluator.TryPrevious(CurrentSplinePointId, out var previousPointId))
        {
            _startTangent = (points[nextPointId].Position - points[previousPointId].Position) * 0.5f;
        }
        else
        {
            _startTangent = (points[nextPointId].Position - points[CurrentSplinePointId].Position) * 0.5f;
        }

        if (_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextNextPointId, 2))
        {
            _endTangent = (points[nextNextPointId].Position - points[CurrentSplinePointId].Position) * 0.5f;
        }
        else
        {
            _endTangent = (points[nextPointId].Position - points[CurrentSplinePointId].Position) * 0.5f;
        }
    }

    private bool Move(float progress)
    {
        var points = _spline.Points;
        var junctions = _spline.Junctions;
        
        bool recalculateTangents = false;
        while (progress > _currentVecLength)
        {
            progress -= _currentVecLength;
                
            if (!_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPointId)
                || !_junctionEvaluator.TryNext(nextPointId, out var nextNextPointId))
            {
                return false;
            }

            CurrentSplinePointId = nextPointId;
            _currentVecLength = (points[nextNextPointId].Position - points[CurrentSplinePointId].Position).Length();
            recalculateTangents = true;

            if (_junctionPassed)
            {
                _endIndicatorDistance -= _currentVecLength;

                if (_endIndicatorDistance < 0)
                {
                    _indicator = 0;
                    _junctionPassed = false;
                    _endIndicatorDistance = 0;
                }
            }
                
            if (_nextJunctionId >= 0 && points[CurrentSplinePointId].JunctionEndId == _nextJunctionId)
            {
                _junctionPassed = true;
                _endIndicatorDistance = junctions[_nextJunctionId].IndicateDistancePost;
                _nextJunctionId = -1;
            }
        }

        if (recalculateTangents)
        {
            CalculateTangents();
        }

        _currentVecProgress = progress;

        return true;
    }

    public bool CanSpawn(int spawnPointId, AiState? previousAi, AiState? nextAi)
    {
        var ops = _spline.Operations;
        ref readonly var spawnPoint = ref ops.Points[spawnPointId];

        if (!IsAllowedLaneCount(spawnPointId))
            return false;
        if (!IsAllowedLane(in spawnPoint))
            return false;
        if (!IsKeepingSafetyDistances(in spawnPoint, previousAi, nextAi))
            return false;

        return EntryCar.CanSpawnAiState(spawnPoint.Position, this);
    }

    private bool IsKeepingSafetyDistances(in SplinePoint spawnPoint, AiState? previousAi, AiState? nextAi)
    {
        if (previousAi != null)
        {
            var distance = MathF.Max(0, Vector3.Distance(spawnPoint.Position, previousAi.Status.Position)
                           - previousAi.EntryCar.VehicleLengthPreMeters
                           - EntryCar.VehicleLengthPostMeters);

            var distanceSquared = distance * distance;
            if (distanceSquared < previousAi.SafetyDistanceSquared || distanceSquared < SafetyDistanceSquared)
                return false;
        }
        
        if (nextAi != null)
        {
            var distance = MathF.Max(0, Vector3.Distance(spawnPoint.Position, nextAi.Status.Position)
                                        - nextAi.EntryCar.VehicleLengthPostMeters
                                        - EntryCar.VehicleLengthPreMeters);

            var distanceSquared = distance * distance;
            if (distanceSquared < nextAi.SafetyDistanceSquared || distanceSquared < SafetyDistanceSquared)
                return false;
        }

        return true;
    }

    private bool IsAllowedLaneCount(int spawnPointId)
    {
        var laneCount = _spline.GetLanes(spawnPointId).Length;
        if (EntryCar.MinLaneCount.HasValue && laneCount < EntryCar.MinLaneCount.Value)
            return false;
        if (EntryCar.MaxLaneCount.HasValue && laneCount > EntryCar.MaxLaneCount.Value)
            return false;
        
        return true;
    }

    private bool IsAllowedLane(in SplinePoint spawnPoint)
    {
        var isAllowedLane = true;
        if (EntryCar.AiAllowedLanes != null)
        {
            isAllowedLane = (EntryCar.AiAllowedLanes.Contains(LaneSpawnBehavior.Middle) && spawnPoint.LeftId >= 0 && spawnPoint.RightId >= 0)
                            || (EntryCar.AiAllowedLanes.Contains(LaneSpawnBehavior.Left) && spawnPoint.LeftId < 0)
                            || (EntryCar.AiAllowedLanes.Contains(LaneSpawnBehavior.Right) && spawnPoint.RightId < 0);
        }

        return isAllowedLane;
    }

    private (AiState? ClosestAiState, float ClosestAiStateDistance, float MaxSpeed) SplineLookahead()
    {
        var points = _spline.Points;
        var junctions = _spline.Junctions;
        
        float maxBrakingDistance = PhysicsUtils.CalculateBrakingDistance(CurrentSpeed, EntryCar.AiDeceleration) * 2 + 20;
        AiState? closestAiState = null;
        float closestAiStateDistance = float.MaxValue;
        bool junctionFound = false;
        float distanceTravelled = 0;
        var pointId = CurrentSplinePointId;
        ref readonly var point = ref points[pointId]; 
        float maxSpeed = float.MaxValue;
        float currentSpeedSquared = CurrentSpeed * CurrentSpeed;
        while (distanceTravelled < maxBrakingDistance)
        {
            distanceTravelled += point.Length;
            pointId = _junctionEvaluator.Next(pointId);
            if (pointId < 0)
                break;

            point = ref points[pointId];

            if (!junctionFound && point.JunctionStartId >= 0 && distanceTravelled < junctions[point.JunctionStartId].IndicateDistancePre)
            {
                ref readonly var jct = ref junctions[point.JunctionStartId];
                
                var indicator = _junctionEvaluator.WillTakeJunction(point.JunctionStartId) ? jct.IndicateWhenTaken : jct.IndicateWhenNotTaken;
                if (indicator != 0)
                {
                    _indicator = indicator;
                    _nextJunctionId = point.JunctionStartId;
                    junctionFound = true;
                }
            }

            if (closestAiState == null)
            {
                var slowest = _spline.SlowestAiStates[pointId];

                if (slowest != null)
                {
                    closestAiState = slowest;
                    closestAiStateDistance = MathF.Max(0, Vector3.Distance(Status.Position, closestAiState.Status.Position)
                                                          - EntryCar.VehicleLengthPreMeters
                                                          - closestAiState.EntryCar.VehicleLengthPostMeters);
                }
            }

            float maxCorneringSpeedSquared = PhysicsUtils.CalculateMaxCorneringSpeedSquared(point.Radius, EntryCar.AiCorneringSpeedFactor);
            if (maxCorneringSpeedSquared < currentSpeedSquared)
            {
                float maxCorneringSpeed = MathF.Sqrt(maxCorneringSpeedSquared);
                float brakingDistance = PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - maxCorneringSpeed,
                                            EntryCar.AiDeceleration * EntryCar.AiCorneringBrakeForceFactor)
                                        * EntryCar.AiCorneringBrakeDistanceFactor;

                if (brakingDistance > distanceTravelled)
                {
                    maxSpeed = Math.Min(maxCorneringSpeed, maxSpeed);
                }
            }
        }

        return (closestAiState, closestAiStateDistance, maxSpeed);
    }

    private bool ShouldIgnorePlayerObstacles()
    {
        if (_configuration.Extra.AiParams.IgnorePlayerObstacleSpheres != null)
        {
            foreach (var sphere in _configuration.Extra.AiParams.IgnorePlayerObstacleSpheres)
            {
                if (Vector3.DistanceSquared(Status.Position, sphere.Center) < sphere.RadiusMeters * sphere.RadiusMeters)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private (EntryCar? entryCar, float distance) FindClosestPlayerObstacle()
    {
        if (!ShouldIgnorePlayerObstacles())
        {
            EntryCar? closestCar = null;
            float minDistance = float.MaxValue;
            for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
            {
                var playerCar = _entryCarManager.EntryCars[i];
                if (playerCar.Client?.HasSentFirstUpdate == true)
                {
                    float distance = Vector3.DistanceSquared(playerCar.Status.Position, Status.Position);

                    if (distance < minDistance
                        && Math.Abs(playerCar.Status.Position.Y - Status.Position.Y) < 1.5
                        && GetAngleToCar(playerCar.Status) is > 166 and < 194)
                    {
                        minDistance = distance;
                        closestCar = playerCar;
                    }
                }
            }

            if (closestCar != null)
            {
                return (closestCar, MathF.Sqrt(minDistance));
            }
        }

        return (null, float.MaxValue);
    }

    public void DetectObstacles()
    {
        if (!Initialized) return;
            
        if (_sessionManager.ServerTimeMilliseconds < _ignoreObstaclesUntil)
        {
            SetTargetSpeed(MaxSpeed);
            return;
        }

        if (_sessionManager.ServerTimeMilliseconds < _stoppedForCollisionUntil)
        {
            SetTargetSpeed(0);
            return;
        }

        if (_sessionManager.ServerTimeMilliseconds < _brakeCheckUntil)
        {
            SetTargetSpeed(MathF.Max(WalkingSpeed, InitialMaxSpeed - T2.BrakeCheckDropKph / 3.6f), EntryCar.AiDeceleration * 1.3f, EntryCar.AiAcceleration);
            return;
        }
            
        float targetSpeed = InitialMaxSpeed;
        float maxSpeed = InitialMaxSpeed;
        bool hasObstacle = false;

        var splineLookahead = SplineLookahead();
        var playerObstacle = FindClosestPlayerObstacle();

        ClosestAiObstacleDistance = splineLookahead.ClosestAiState != null ? splineLookahead.ClosestAiStateDistance : -1;
        DecideLaneChange(splineLookahead.ClosestAiState, splineLookahead.ClosestAiStateDistance, playerObstacle);

        if (playerObstacle.distance < _minObstacleDistance || splineLookahead.ClosestAiStateDistance < _minObstacleDistance)
        {
            targetSpeed = 0;
            hasObstacle = true;
        }
        else if (playerObstacle.distance < splineLookahead.ClosestAiStateDistance && playerObstacle.entryCar != null)
        {
            float playerSpeed = playerObstacle.entryCar.Status.Velocity.Length();

            if (playerSpeed < 0.1f)
            {
                playerSpeed = 0;
            }

            if ((playerSpeed < CurrentSpeed || playerSpeed == 0)
                && playerObstacle.distance < PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - playerSpeed, EntryCar.AiDeceleration) * 2 + 20)
            {
                targetSpeed = Math.Max(WalkingSpeed, playerSpeed);
                hasObstacle = true;
            }
        }
        else if (splineLookahead.ClosestAiState != null)
        {
            float closestTargetSpeed = Math.Min(splineLookahead.ClosestAiState.CurrentSpeed, splineLookahead.ClosestAiState.TargetSpeed);
            if ((closestTargetSpeed < CurrentSpeed || splineLookahead.ClosestAiState.CurrentSpeed == 0)
                && splineLookahead.ClosestAiStateDistance < PhysicsUtils.CalculateBrakingDistance(CurrentSpeed - closestTargetSpeed, EntryCar.AiDeceleration) * 2 + 20)
            {
                targetSpeed = Math.Max(WalkingSpeed, closestTargetSpeed);
                hasObstacle = true;
            }
        }

        targetSpeed = Math.Min(splineLookahead.MaxSpeed, targetSpeed);

        if (CurrentSpeed == 0 && !_stoppedForObstacle)
        {
            _stoppedForObstacle = true;
            _stoppedForObstacleSince = _sessionManager.ServerTimeMilliseconds;
            _obstacleHonkStart = _stoppedForObstacleSince + Random.Shared.Next(3000, 7000);
            _obstacleHonkEnd = _obstacleHonkStart + Random.Shared.Next(500, 1500);
            Log.Verbose("AI {SessionId} stopped for obstacle", EntryCar.SessionId);
        }
        else if (CurrentSpeed > 0 && _stoppedForObstacle)
        {
            _stoppedForObstacle = false;
            Log.Verbose("AI {SessionId} no longer stopped for obstacle", EntryCar.SessionId);
        }
        else if (_stoppedForObstacle && _sessionManager.ServerTimeMilliseconds - _stoppedForObstacleSince > _configuration.Extra.AiParams.IgnoreObstaclesAfterMilliseconds)
        {
            _ignoreObstaclesUntil = _sessionManager.ServerTimeMilliseconds + 10_000;
            Log.Verbose("AI {SessionId} ignoring obstacles until {IgnoreObstaclesUntil}", EntryCar.SessionId, _ignoreObstaclesUntil);
        }

        float deceleration = EntryCar.AiDeceleration;
        if (!hasObstacle)
        {
            deceleration *= EntryCar.AiCorneringBrakeForceFactor;
        }
        
        MaxSpeed = maxSpeed;
        SetTargetSpeed(targetSpeed, deceleration, EntryCar.AiAcceleration);
    }

    private Traffic2Params T2 => _configuration.Extra.AiParams.Traffic2;
    private int SlowSideDirection => T2.SlowLaneSide == TrafficSide.Right ? 1 : -1;

    /// <summary>New driver: personality for lane changes, yielding and sway.</summary>
    private void ResetTraffic2()
    {
        _lcDirection = 0;
        _lcProgress = 0;
        _lcRequest = 0;
        _lcCooldownUntil = _sessionManager.ServerTimeMilliseconds + 3000;
        _yieldUntil = 0;
        _brakeCheckUntil = 0;
        _laneChangeIndicator = 0;
        var t2 = T2;
        _lcSeconds = Random.Shared.NextSingle(t2.LaneChangeSecondsMin, MathF.Max(t2.LaneChangeSecondsMin, t2.LaneChangeSecondsMax));
        _stubborn = Random.Shared.NextSingle() < t2.StubbornShare;
        _swayPhase = Random.Shared.NextSingle() * MathF.PI * 2;
        _swayFrequency = Random.Shared.NextSingle(0.05f, 0.15f);
        _swayAmplitude = t2.SwayMeters * Random.Shared.NextSingle(0.3f, 1f);
    }

    private int AdjacentPoint(int pointId, int direction)
    {
        if (pointId < 0) return -1;
        ref readonly var point = ref _spline.Points[pointId];
        return direction < 0 ? point.LeftId : point.RightId;
    }

    /// <summary>
    /// Is the lane next to us (direction -1 left, +1 right) free from <paramref name="behind"/> m behind to
    /// <paramref name="ahead"/> m ahead? Checks other AI on that lane's spline and players near it (blind spot).
    /// </summary>
    private bool IsLaneFree(int direction, float behind, float ahead)
    {
        var points = _spline.Points;
        var adjacent = AdjacentPoint(CurrentSplinePointId, direction);
        if (adjacent < 0) return false;

        // AI on the target lane, walking its spline both ways
        float distance = 0;
        for (var id = adjacent; id >= 0 && distance < ahead; id = points[id].NextId)
        {
            var other = _spline.SlowestAiStates[id];
            if (other != null && other != this) return false;
            distance += points[id].Length;
        }
        distance = 0;
        for (var id = points[adjacent].PreviousId; id >= 0 && distance < behind; id = points[id].PreviousId)
        {
            var other = _spline.SlowestAiStates[id];
            if (other != null && other != this) return false;
            distance += points[id].Length;
        }

        // players: anyone beside or close behind / ahead in the target lane
        var laneVector = points[adjacent].Position - points[CurrentSplinePointId].Position;
        var laneWidth = laneVector.Length();
        if (laneWidth < 0.5f) return false;
        var side = laneVector / laneWidth;
        var forward = Status.Velocity.LengthSquared() > 0.25f ? Vector3.Normalize(Status.Velocity) : side;
        for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
        {
            var car = _entryCarManager.EntryCars[i];
            if (car.Client?.HasSentFirstUpdate != true) continue;
            var rel = car.Status.Position - Status.Position;
            if (MathF.Abs(rel.Y) > 3) continue;
            var lon = Vector3.Dot(rel, forward);
            var lat = Vector3.Dot(rel, side);
            if (lon > -behind && lon < ahead && lat > laneWidth * 0.35f && lat < laneWidth * 1.65f) return false;
        }
        return true;
    }

    /// <summary>A player behind asked to pass (high-beam flash). Returns false if this driver ignores it.</summary>
    public bool RequestYield()
    {
        if (!Initialized || !T2.Enabled || !T2.YieldOnFlash || _stubborn) return false;
        _yieldUntil = _sessionManager.ServerTimeMilliseconds + 5000;
        Log.Debug("Traffic2: AI {SessionId} asked to yield", EntryCar.SessionId);
        return true;
    }

    /// <summary>Brake check: a short, sharp slowdown (not a stop) for AiParams.Traffic2.BrakeCheckSeconds.</summary>
    public void BrakeCheck()
    {
        if (!Initialized || !T2.Enabled) return;
        _brakeCheckUntil = _sessionManager.ServerTimeMilliseconds + (long)(T2.BrakeCheckSeconds * 1000);
    }

    /// <summary>Lane change decisions, called from obstacle detection (every 100 ms).</summary>
    private void DecideLaneChange(AiState? closestAi, float closestAiDistance, (EntryCar? entryCar, float distance) playerObstacle)
    {
        var t2 = T2;
        var now = _sessionManager.ServerTimeMilliseconds;
        if (!t2.Enabled || !t2.LaneChanges || _lcDirection != 0 || _lcRequest != 0 || now < _lcCooldownUntil) return;
        if (now < _stoppedForCollisionUntil || CurrentSpeed < 30 / 3.6f) return;

        var slow = SlowSideDirection;
        var fast = -slow;

        // 1. yield to a player who flashed: move to the slow side
        if (now < _yieldUntil)
        {
            if (AdjacentPoint(CurrentSplinePointId, slow) < 0)
            {
                // already in the slow lane: the player can pass on the other side
                Log.Debug("Traffic2: AI {SessionId} already in the slow lane, nothing to yield", EntryCar.SessionId);
                _yieldUntil = 0;
            }
            else if (IsLaneFree(slow, t2.LaneFreeBehindMeters, t2.LaneFreeAheadMeters))
            {
                _lcRequest = slow;
                _yieldUntil = 0;
            }
            return; // slow lane busy: keep trying until the request expires
        }

        // 2. overtake a slower car (AI or player) ahead
        var deltaMs = t2.OvertakeSpeedDeltaKph / 3.6f;
        var slowAhead = closestAi != null && closestAiDistance < t2.OvertakeLookaheadMeters
                        && Math.Min(closestAi.CurrentSpeed, closestAi.TargetSpeed) < InitialMaxSpeed - deltaMs;
        var playerAhead = playerObstacle.entryCar != null && playerObstacle.distance < t2.OvertakeLookaheadMeters
                          && playerObstacle.entryCar.Status.Velocity.Length() < InitialMaxSpeed - deltaMs;
        if (slowAhead || playerAhead)
        {
            if (IsLaneFree(fast, t2.LaneFreeBehindMeters, t2.LaneFreeAheadMeters)) _lcRequest = fast;
            else if (IsLaneFree(slow, t2.LaneFreeBehindMeters, t2.LaneFreeAheadMeters)) _lcRequest = slow; // undertake as a last resort
            return;
        }

        // 3. keep to the slow lane when it's free for a good while ahead
        if (AdjacentPoint(CurrentSplinePointId, slow) >= 0
            && Random.Shared.NextSingle() < t2.ReturnToSlowLaneChance * 0.1f
            && IsLaneFree(slow, t2.LaneFreeBehindMeters, t2.LaneFreeAheadMeters * 3))
        {
            _lcRequest = slow;
        }
    }

    /// <summary>Sideways vector from this lane to the neighbouring lane at the current point (no along-road component).</summary>
    private Vector3 LateralLaneVector(int adjacent, Vector3 tangent)
    {
        var laneVector = _spline.Points[adjacent].Position - _spline.Points[CurrentSplinePointId].Position;
        var flatTangent = tangent with { Y = 0 };
        if (flatTangent.LengthSquared() < 1e-6f) return laneVector;
        flatTangent = Vector3.Normalize(flatTangent);
        return laneVector - flatTangent * Vector3.Dot(laneVector, flatTangent);
    }

    /// <summary>Starts / advances a lane change and returns the lateral offset to add to the lane position.</summary>
    private Vector3 AdvanceLaneChange(float dtSeconds, Vector3 lanePosition, Vector3 tangent, out Vector3 lateralVelocity)
    {
        lateralVelocity = Vector3.Zero;
        if (!T2.Enabled) return Vector3.Zero;

        if (_lcDirection == 0 && _lcRequest != 0)
        {
            _lcDirection = _lcRequest;
            _lcRequest = 0;
            _lcProgress = 0;
            _laneChangeIndicator = _lcDirection < 0 ? CarStatusFlags.IndicateLeft : CarStatusFlags.IndicateRight;
            Log.Debug("Traffic2: AI {SessionId} lane change {Direction} at {Speed:F0} km/h", EntryCar.SessionId, _lcDirection < 0 ? "left" : "right", CurrentSpeed * 3.6f);
        }
        if (_lcDirection == 0) return Vector3.Zero;

        var adjacent = AdjacentPoint(CurrentSplinePointId, _lcDirection);
        if (adjacent < 0)
        {
            // target lane ended (lane drop): abort and drift back
            _lcProgress -= dtSeconds / _lcSeconds * 2;
            if (_lcProgress <= 0) EndLaneChange();
            return Vector3.Zero;
        }

        var before = Smooth(_lcProgress);
        _lcProgress = MathF.Min(1, _lcProgress + dtSeconds / _lcSeconds);
        var after = Smooth(_lcProgress);
        var laneVector = LateralLaneVector(adjacent, tangent);
        if (dtSeconds > 0) lateralVelocity = laneVector * ((after - before) / dtSeconds);

        if (_lcProgress >= 1)
        {
            // the car is now on the other lane: continue on its spline from the closest place
            SwitchToLane(adjacent, lanePosition + laneVector);
            EndLaneChange();
            return laneVector; // this frame still uses the old lane's position + the full offset
        }
        return laneVector * after;
    }

    private static float Smooth(float t) => t * t * (3 - 2 * t);

    private void EndLaneChange()
    {
        _lcDirection = 0;
        _lcProgress = 0;
        _laneChangeIndicator = 0;
        _lcCooldownUntil = _sessionManager.ServerTimeMilliseconds + (long)(T2.LaneChangeCooldownSeconds * 1000);
    }

    /// <summary>
    /// Continues on the neighbouring lane's spline at the place closest to <paramref name="position"/>, keeping speed,
    /// colour and everything else.
    /// </summary>
    private void SwitchToLane(int pointId, Vector3 position)
    {
        var points = _spline.Points;
        // neighbouring points can be offset along the road: step to the segment that contains the car
        for (var i = 0; i < 8; i++)
        {
            var prev = points[pointId].PreviousId;
            var next = points[pointId].NextId;
            if (prev >= 0 && Vector3.Dot(position - points[pointId].Position, points[pointId].Position - points[prev].Position) < 0) pointId = prev;
            else if (next >= 0 && points[next].NextId >= 0 && Vector3.Dot(position - points[next].Position, points[next].Position - points[pointId].Position) > 0) pointId = next;
            else break;
        }

        _junctionEvaluator.Clear();
        CurrentSplinePointId = pointId;
        if (!_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPointId)) return;
        var segment = points[nextPointId].Position - points[CurrentSplinePointId].Position;
        _currentVecLength = segment.Length();
        _currentVecProgress = _currentVecLength > 0
            ? Math.Clamp(Vector3.Dot(position - points[CurrentSplinePointId].Position, segment / _currentVecLength), 0, _currentVecLength * 0.999f)
            : 0;
        CalculateTangents();
    }

    public void StopForCollision()
    {
        if (!ShouldIgnorePlayerObstacles())
        {
            _stoppedForCollisionUntil = _sessionManager.ServerTimeMilliseconds + Random.Shared.Next(EntryCar.AiMinCollisionStopTimeMilliseconds, EntryCar.AiMaxCollisionStopTimeMilliseconds);
        }
    }

    /// <returns>0 is the rear <br/> Angle is counterclockwise</returns>
    public float GetAngleToCar(CarStatus car)
    {
        float challengedAngle = (float) (Math.Atan2(Status.Position.X - car.Position.X, Status.Position.Z - car.Position.Z) * 180 / Math.PI);
        if (challengedAngle < 0)
            challengedAngle += 360;
        float challengedRot = Status.GetRotationAngle();

        challengedAngle += challengedRot;
        challengedAngle %= 360;

        return challengedAngle;
    }

    private void SetTargetSpeed(float speed, float deceleration, float acceleration)
    {
        TargetSpeed = speed;
        if (speed < CurrentSpeed)
        {
            Acceleration = -deceleration;
        }
        else if (speed > CurrentSpeed)
        {
            Acceleration = acceleration;
        }
        else
        {
            Acceleration = 0;
        }
    }

    private void SetTargetSpeed(float speed)
    {
        SetTargetSpeed(speed, EntryCar.AiDeceleration, EntryCar.AiAcceleration);
    }

    public void Update()
    {
        if (!Initialized)
            return;

        var ops = _spline.Operations;

        long currentTime = _sessionManager.ServerTimeMilliseconds;
        long dt = currentTime - _lastTick;
        _lastTick = currentTime;

        if (Acceleration != 0)
        {
            CurrentSpeed += Acceleration * (dt / 1000.0f);
                
            if ((Acceleration < 0 && CurrentSpeed < TargetSpeed) || (Acceleration > 0 && CurrentSpeed > TargetSpeed))
            {
                CurrentSpeed = TargetSpeed;
                Acceleration = 0;
            }
        }

        float moveMeters = (dt / 1000.0f) * CurrentSpeed;
        if (!Move(_currentVecProgress + moveMeters) || !_junctionEvaluator.TryNext(CurrentSplinePointId, out var nextPoint))
        {
            Log.Debug("Car {SessionId} reached spline end, despawning", EntryCar.SessionId);
            Despawn();
            return;
        }

        CatmullRom.CatmullRomPoint smoothPos = CatmullRom.Evaluate(ops.Points[CurrentSplinePointId].Position, 
            ops.Points[nextPoint].Position, 
            _startTangent, 
            _endTangent, 
            _currentVecProgress / _currentVecLength);
            
        if (_configuration.Extra.AiParams.Traffic2.Enabled)
        {
            var lateral = AdvanceLaneChange(dt / 1000.0f, smoothPos.Position, smoothPos.Tangent, out var lateralVelocity);
            if (_swayAmplitude > 0)
            {
                var right = Vector3.Cross(smoothPos.Tangent, Vector3.UnitY);
                if (right.LengthSquared() > 0.0001f)
                    lateral += Vector3.Normalize(right) * (MathF.Sin(currentTime / 1000.0f * _swayFrequency * MathF.PI * 2 + _swayPhase) * _swayAmplitude);
            }
            smoothPos.Position += lateral;
            if (CurrentSpeed > 1 && lateralVelocity != Vector3.Zero)
                smoothPos.Tangent = Vector3.Normalize(smoothPos.Tangent * CurrentSpeed + lateralVelocity);
        }

        Vector3 rotation = new Vector3
        {
            X = MathF.Atan2(smoothPos.Tangent.Z, smoothPos.Tangent.X) - MathF.PI / 2,
            Y = (MathF.Atan2(new Vector2(smoothPos.Tangent.Z, smoothPos.Tangent.X).Length(), smoothPos.Tangent.Y) - MathF.PI / 2) * -1f,
            Z = ops.GetCamber(CurrentSplinePointId, _currentVecProgress / _currentVecLength)
        };

        float tyreAngularSpeed = GetTyreAngularSpeed(CurrentSpeed, EntryCar.TyreDiameterMeters);
        byte encodedTyreAngularSpeed =  (byte) (Math.Clamp(MathF.Round(MathF.Log10(tyreAngularSpeed + 1.0f) * 20.0f) * Math.Sign(tyreAngularSpeed), -100.0f, 154.0f) + 100.0f);

        Status.Timestamp = _sessionManager.ServerTimeMilliseconds;
        Status.Position = smoothPos.Position with { Y = smoothPos.Position.Y + EntryCar.AiSplineHeightOffsetMeters };
        Status.Rotation = rotation;
        Status.Velocity = smoothPos.Tangent * CurrentSpeed;
        Status.SteerAngle = 127;
        Status.WheelAngle = 127;
        Status.TyreAngularSpeed[0] = encodedTyreAngularSpeed;
        Status.TyreAngularSpeed[1] = encodedTyreAngularSpeed;
        Status.TyreAngularSpeed[2] = encodedTyreAngularSpeed;
        Status.TyreAngularSpeed[3] = encodedTyreAngularSpeed;
        Status.EngineRpm = (ushort)MathUtils.Lerp(EntryCar.AiIdleEngineRpm, EntryCar.AiMaxEngineRpm, CurrentSpeed / _configuration.Extra.AiParams.MaxSpeedMs);
        Status.StatusFlag = GetLights(_configuration.Extra.AiParams.EnableDaytimeLights, _weatherManager.CurrentSunPosition, _randomTwilight)
                            | (_sessionManager.ServerTimeMilliseconds < _stoppedForCollisionUntil || CurrentSpeed < 20 / 3.6f ? CarStatusFlags.HazardsOn : 0)
                            | (CurrentSpeed == 0 || Acceleration < 0 ? CarStatusFlags.BrakeLightsOn : 0)
                            | (_stoppedForObstacle && _sessionManager.ServerTimeMilliseconds > _obstacleHonkStart && _sessionManager.ServerTimeMilliseconds < _obstacleHonkEnd ? CarStatusFlags.Horn : 0)
                            | GetWiperSpeed(_weatherManager.CurrentWeather.RainIntensity)
                            | (_laneChangeIndicator != 0 ? _laneChangeIndicator : _indicator);
        Status.Gear = 2;
    }
        
    private static float GetTyreAngularSpeed(float speed, float wheelDiameter)
    {
        return speed / (MathF.PI * wheelDiameter) * 6;
    }

    private static CarStatusFlags GetWiperSpeed(float rainIntensity)
    {
        return rainIntensity switch
        {
            < 0.05f => 0,
            < 0.25f => CarStatusFlags.WiperLevel1,
            < 0.5f => CarStatusFlags.WiperLevel2,
            _ => CarStatusFlags.WiperLevel3
        };
    }
    
    private static CarStatusFlags GetLights(bool daytimeLights, SunPosition? sunPosition, double twilight)
    {
        const CarStatusFlags lightFlags = CarStatusFlags.LightsOn | CarStatusFlags.HighBeamsOff;
        if (daytimeLights || sunPosition == null) return lightFlags;

        return sunPosition.Value.Altitude < twilight ? lightFlags : 0;
    }
}
