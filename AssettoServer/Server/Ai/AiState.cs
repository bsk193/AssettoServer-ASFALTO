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

    // ── ASFALTO BetterTraffic (AiParams.BetterTraffic) ──
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
    private TrafficPersonality _personality = new();
    private float _gapSeconds = 1.3f;
    private object? _leadRef;
    private float _perceivedLeadSpeed = -1;
    private long _lastObstacleTick;
    private float _tailgateSeconds;
    private long _brakeCheckCooldownUntil;
    // crashes
    private AiCrashMode _crashMode;
    private Vector3 _crashPosition;
    private Vector3 _crashVelocity;
    private float _crashYaw;
    private float _crashYawRate;
    private float _crashPitch;
    private float _crashRoll;
    private float _crashRollTarget;
    private float _crashImpactKph;
    private long _crashSince;
    private long _pullOverUntil;
    private byte _physicsOwner = 255;   // player whose game simulates the crashed car, 255 = none
    private long _physicsReportAt;
    private Vector3 _reportPosition;
    private Vector3 _reportVelocity;
    private bool _frozenAtReport;
    private long _tapUntil;
    private float _pullOverProgress;
    private bool _pullOverRejoining;
    private static int _rightSign; // sign that turns Cross(tangent, Y) into "towards the right-hand lane", 0 = not known yet

    /// <summary>Driver personality name (BetterTraffic), e.g. Calm, Normal, Aggressive, Distracted.</summary>
    public string PersonalityName => _personality.Name;
    /// <summary>What a crash did to this car (BetterTraffic).</summary>
    public AiCrashMode CrashMode => _crashMode;
    /// <summary>BetterTraffic crash notifications: (car, phase, impact speed in km/h). Raised on the server update loop.</summary>
    public static event Action<AiState, AiCrashPhase, float>? BetterTrafficCrash;

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
        ResetBetterTraffic();
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
        if (BT.Enabled && BT.NoSpawnNearLaneStartMeters > 0 && DistanceFromLaneStart(spawnPointId) < BT.NoSpawnNearLaneStartMeters)
            return false;

        return EntryCar.CanSpawnAiState(spawnPoint.Position, this);
    }

    // BetterTraffic: metres from the open start of each lane (on-ramps, side roads), float.MaxValue for looped lanes.
    // Lanes that start from nothing often start off the road, so cars spawned there come out of the dirt.
    private static float[]? _distanceFromLaneStart;
    private static readonly object DistanceFromLaneStartLock = new();

    private float DistanceFromLaneStart(int pointId)
    {
        var table = _distanceFromLaneStart;
        if (table == null)
        {
            lock (DistanceFromLaneStartLock)
            {
                table = _distanceFromLaneStart;
                if (table == null)
                {
                    var points = _spline.Points;
                    table = new float[points.Length];
                    Array.Fill(table, float.MaxValue);
                    var limit = BT.NoSpawnNearLaneStartMeters * 2;
                    for (var i = 0; i < points.Length; i++)
                    {
                        if (points[i].PreviousId >= 0) continue;
                        float distance = 0;
                        for (var id = i; id >= 0 && distance < limit && table[id] > distance; id = points[id].NextId)
                        {
                            table[id] = distance;
                            distance += points[id].Length;
                        }
                    }
                    _distanceFromLaneStart = table;
                }
            }
        }
        return pointId >= 0 && pointId < table.Length ? table[pointId] : float.MaxValue;
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

        if (_crashMode is AiCrashMode.Sliding or AiCrashMode.Wrecked)
        {
            SetTargetSpeed(0);
            return;
        }

        if (_crashMode == AiCrashMode.PullOver && !_pullOverRejoining)
        {
            // steer aside first (AdvancePullOver), then roll to a stop: no emergency braking
            SetTargetSpeed(0, BT.PullOverDeceleration, EntryCar.AiAcceleration);
            if (_sessionManager.ServerTimeMilliseconds > _pullOverUntil && CurrentSpeed == 0 && IsOwnLaneClearBehind(30))
            {
                _pullOverRejoining = true;
                Log.Debug("BetterTraffic: AI {SessionId} rejoins after pulling over", EntryCar.SessionId);
            }
            return;
        }

        if (BT.Enabled) UpdateTailgating();

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

        if (_sessionManager.ServerTimeMilliseconds < _tapUntil)
        {
            SetTargetSpeed(MathF.Max(WalkingSpeed, InitialMaxSpeed * 0.7f), 2.5f, EntryCar.AiAcceleration);
            return;
        }

        if (_sessionManager.ServerTimeMilliseconds < _brakeCheckUntil)
        {
            SetTargetSpeed(MathF.Max(WalkingSpeed, InitialMaxSpeed - BT.BrakeCheckDropKph / 3.6f), EntryCar.AiDeceleration * 1.3f, EntryCar.AiAcceleration);
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

        if (BT.Enabled && BT.FollowingGaps)
        {
            var follow = FollowingSpeed(splineLookahead.ClosestAiState, splineLookahead.ClosestAiStateDistance, playerObstacle);
            if (follow < targetSpeed)
            {
                targetSpeed = follow;
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

    private BetterTrafficParams BT => _configuration.Extra.AiParams.BetterTraffic;
    private int SlowSideDirection => BT.SlowLaneSide == TrafficSide.Right ? 1 : -1;

    /// <summary>New driver: personality for lane changes, yielding and sway.</summary>
    private void ResetBetterTraffic()
    {
        _lcDirection = 0;
        _lcProgress = 0;
        _lcRequest = 0;
        _lcCooldownUntil = _sessionManager.ServerTimeMilliseconds + 3000;
        _yieldUntil = 0;
        _brakeCheckUntil = 0;
        _laneChangeIndicator = 0;
        _crashMode = AiCrashMode.None;
        _physicsOwner = 255;
        _frozenAtReport = false;
        _tapUntil = 0;
        _crashRoll = 0;
        _crashRollTarget = 0;
        _pullOverProgress = 0;
        _pullOverRejoining = false;
        _tailgateSeconds = 0;
        _brakeCheckCooldownUntil = 0;
        _perceivedLeadSpeed = -1;
        _leadRef = null;
        _lastObstacleTick = 0;
        var bt = BT;
        _personality = PickPersonality(bt.Personalities);
        _gapSeconds = Random.Shared.NextSingle(_personality.GapSecondsMin, MathF.Max(_personality.GapSecondsMin, _personality.GapSecondsMax));
        _lcSeconds = Random.Shared.NextSingle(bt.LaneChangeSecondsMin, MathF.Max(bt.LaneChangeSecondsMin, bt.LaneChangeSecondsMax)) * _personality.LaneChangeTimeFactor;
        _stubborn = Random.Shared.NextSingle() < _personality.StubbornChance + bt.StubbornShare;
        _swayPhase = Random.Shared.NextSingle() * MathF.PI * 2;
        _swayFrequency = Random.Shared.NextSingle(0.05f, 0.15f);
        _swayAmplitude = bt.SwayMeters * Random.Shared.NextSingle(0.3f, 1f) * _personality.SwayFactor;
        if (bt.Enabled && _personality.SpeedFactor > 0)
        {
            InitialMaxSpeed *= _personality.SpeedFactor;
            CurrentSpeed = InitialMaxSpeed;
            TargetSpeed = InitialMaxSpeed;
            MaxSpeed = InitialMaxSpeed;
        }
    }

    private static TrafficPersonality PickPersonality(List<TrafficPersonality> personalities)
    {
        float total = 0;
        foreach (var p in personalities) total += MathF.Max(0, p.Weight);
        if (total <= 0) return personalities.Count > 0 ? personalities[0] : new TrafficPersonality();
        var roll = Random.Shared.NextSingle() * total;
        foreach (var p in personalities)
        {
            roll -= MathF.Max(0, p.Weight);
            if (roll <= 0) return p;
        }
        return personalities[^1];
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

    /// <summary>
    /// Speed that keeps this driver's time gap to the car ahead (AI or player). The driver sees speed changes of the car
    /// ahead only after its reaction time. Returns float.MaxValue when nothing needs following.
    /// </summary>
    private float FollowingSpeed(AiState? closestAi, float closestAiDistance, (EntryCar? entryCar, float distance) playerObstacle)
    {
        object? lead = null;
        float leadDistance = float.MaxValue, leadSpeed = 0;
        if (closestAi != null)
        {
            lead = closestAi;
            leadDistance = closestAiDistance;
            leadSpeed = closestAi.CurrentSpeed;
        }
        if (playerObstacle.entryCar != null)
        {
            var playerDistance = MathF.Max(0, playerObstacle.distance - EntryCar.VehicleLengthPreMeters - 2.3f);
            if (playerDistance < leadDistance)
            {
                lead = playerObstacle.entryCar;
                leadDistance = playerDistance;
                leadSpeed = playerObstacle.entryCar.Status.Velocity.Length();
            }
        }
        if (lead == null || leadDistance > 250)
        {
            _leadRef = null;
            _perceivedLeadSpeed = -1;
            return float.MaxValue;
        }

        if (!ReferenceEquals(lead, _leadRef) || _perceivedLeadSpeed < 0) _perceivedLeadSpeed = leadSpeed;
        else _perceivedLeadSpeed += (leadSpeed - _perceivedLeadSpeed) * MathF.Min(1, 0.1f / MathF.Max(0.05f, _personality.ReactionSeconds));
        _leadRef = lead;

        var gap = MathF.Max(BT.MinFollowingGapMeters, _gapSeconds * CurrentSpeed);
        var closing = MathF.Max(0, CurrentSpeed - _perceivedLeadSpeed);
        var reactDistance = gap + PhysicsUtils.CalculateBrakingDistance(closing, EntryCar.AiDeceleration) * 1.5f + closing * _personality.ReactionSeconds;
        if (leadDistance > reactDistance) return float.MaxValue;
        return MathF.Max(0, _perceivedLeadSpeed + Math.Clamp((leadDistance - gap) * 0.25f, -6f, 2f));
    }

    /// <summary>Brake-checks a player who sits too close behind for too long, if this driver's personality does that.</summary>
    private void UpdateTailgating()
    {
        var now = _sessionManager.ServerTimeMilliseconds;
        var dt = _lastObstacleTick > 0 ? Math.Clamp((now - _lastObstacleTick) / 1000f, 0, 0.5f) : 0.1f;
        _lastObstacleTick = now;
        var bt = BT;
        if (_personality.BrakeCheckChance <= 0 || CurrentSpeed < 40 / 3.6f || _crashMode != AiCrashMode.None)
        {
            _tailgateSeconds = 0;
            return;
        }

        var tailgated = false;
        // bumper gap: car lengths are often not configured (0), assume at least a normal car
        var maxDistance = bt.TailgateMeters + MathF.Max(2.2f, EntryCar.VehicleLengthPostMeters) + 2.3f;
        for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
        {
            var car = _entryCarManager.EntryCars[i];
            if (car.Client?.HasSentFirstUpdate != true) continue;
            if (Vector3.DistanceSquared(car.Status.Position, Status.Position) > maxDistance * maxDistance) continue;
            if (Math.Abs(car.Status.Position.Y - Status.Position.Y) > 1.5f) continue;
            if (car.Status.Velocity.Length() < 30 / 3.6f) continue;
            if (GetAngleToCar(car.Status) is > 12 and < 348) continue; // not right behind
            tailgated = true;
            break;
        }

        _tailgateSeconds = tailgated ? _tailgateSeconds + dt : MathF.Max(0, _tailgateSeconds - dt * 2);
        if (_tailgateSeconds < bt.TailgateSeconds || now < _brakeCheckCooldownUntil) return;
        _tailgateSeconds = 0;
        _brakeCheckCooldownUntil = now + (long)(bt.BrakeCheckCooldownSeconds * 1000);
        if (Random.Shared.NextSingle() < _personality.BrakeCheckChance)
        {
            Log.Debug("BetterTraffic: AI {SessionId} ({Personality}) brake-checks a tailgater", EntryCar.SessionId, _personality.Name);
            BrakeCheck();
        }
    }

    /// <summary>No player close behind in this car's own lane (to rejoin after pulling over).</summary>
    private bool IsOwnLaneClearBehind(float meters)
    {
        for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
        {
            var car = _entryCarManager.EntryCars[i];
            if (car.Client?.HasSentFirstUpdate != true) continue;
            if (Vector3.DistanceSquared(car.Status.Position, Status.Position) > meters * meters) continue;
            if (GetAngleToCar(car.Status) is < 30 or > 330) return false;
        }
        return true;
    }

    /// <summary>
    /// BetterTraffic: a player car at <paramref name="otherPosition"/> moving at <paramref name="otherVelocity"/> hit this
    /// car. Small knocks: brake and carry on. Medium: pull over with hazards, then rejoin. Big: the car is pushed, slides,
    /// spins and can roll over (simulated here, so every player sees the same), and stays as a wreck.
    /// Call on the server update loop.
    /// </summary>
    /// <param name="impactKph">Impact speed reported by the game (CollisionEventArgs.Speed); negative = work it out from the velocities.</param>
    /// <param name="contactPosition">World position of the contact, if known (spins the car when off-centre).</param>
    public AiCrashMode Crash(Vector3 otherPosition, Vector3 otherVelocity, float impactKph = -1, Vector3? contactPosition = null)
    {
        if (!Initialized) return AiCrashMode.None;
        var bt = BT;

        var rel = otherVelocity - Status.Velocity;
        rel.Y = 0;
        var normal = Status.Position - otherPosition;
        normal.Y = 0;
        if (normal.LengthSquared() > 0.01f) normal = Vector3.Normalize(normal);
        else if (rel.LengthSquared() > 0.01f) normal = Vector3.Normalize(rel);
        else normal = Vector3.UnitX;
        var closing = MathF.Max(Vector3.Dot(rel, normal), rel.Length() * 0.5f);
        // positions reach the server late, the game's own impact speed is more accurate
        if (impactKph >= 0) closing = impactKph / 3.6f;
        var kph = closing * 3.6f;
        var contact = contactPosition is { } cp && cp != Vector3.Zero && Vector3.DistanceSquared(cp, Status.Position) < 6 * 6 ? cp : otherPosition;

        if (_crashMode is AiCrashMode.Sliding or AiCrashMode.Wrecked)
        {
            // a player's game simulates this car: it handles the hit itself
            if (IsClientSimulated) return _crashMode;
            // hit again: push it further
            if (bt.CrashPhysics) Push(contact, normal, closing);
            if (_crashMode == AiCrashMode.Wrecked && closing > 2)
            {
                _crashMode = AiCrashMode.Sliding;
                BetterTrafficCrash?.Invoke(this, AiCrashPhase.Hit, kph);
            }
            return _crashMode;
        }

        if (!bt.CrashPhysics)
        {
            StopForCollision();
            return AiCrashMode.None;
        }
        if (kph < bt.MinorCrashKph)
        {
            // a tap: lift off, hazards on, carry on (no emergency stop)
            if (_crashMode == AiCrashMode.None) _tapUntil = _sessionManager.ServerTimeMilliseconds + (long)(bt.TapSlowdownSeconds * 1000);
            return AiCrashMode.None;
        }

        _crashImpactKph = kph;
        _crashSince = _sessionManager.ServerTimeMilliseconds;
        if (kph < bt.HeavyCrashKph)
        {
            if (bt.PullOverAfterCrash)
            {
                _crashMode = AiCrashMode.PullOver;
                _pullOverRejoining = false;
                _pullOverUntil = _crashSince + (long)(bt.PullOverSeconds * 1000);
                Log.Debug("BetterTraffic: AI {SessionId} hit at {Kph:F0} km/h, pulling over", EntryCar.SessionId, kph);
            }
            else
            {
                StopForCollision();
            }
            BetterTrafficCrash?.Invoke(this, AiCrashPhase.Hit, kph);
            return _crashMode;
        }

        // big crash: from here on the car is simulated freely
        StartFreeMotion();
        Push(contact, normal, closing);
        if (kph >= bt.RolloverKph && Random.Shared.NextSingle() < bt.RolloverChance)
        {
            var side = Random.Shared.Next(2) == 0 ? -1 : 1;
            _crashRollTarget = _crashRoll + side * (Random.Shared.Next(3) == 0 ? MathF.PI : MathF.PI / 2);
        }
        Log.Debug("BetterTraffic: AI {SessionId} crashed at {Kph:F0} km/h{Rollover}", EntryCar.SessionId, kph, _crashRollTarget != 0 ? ", rolling over" : "");
        BetterTrafficCrash?.Invoke(this, AiCrashPhase.Hit, kph);
        return _crashMode;
    }

    /// <summary>Leaves the spline: from now on the car moves freely (simulated by the server or a player's game).</summary>
    private void StartFreeMotion()
    {
        _lcDirection = 0;
        _lcRequest = 0;
        _laneChangeIndicator = 0;
        _crashMode = AiCrashMode.Sliding;
        _crashPosition = Status.Position;
        _crashVelocity = Status.Velocity with { Y = 0 };
        _crashYaw = Status.Rotation.X;
        _crashPitch = Status.Rotation.Y;
        _crashRoll = Status.Rotation.Z;
        _crashYawRate = 0;
        _crashRollTarget = 0;
        CurrentSpeed = 0;
        TargetSpeed = 0;
        Acceleration = 0;
    }

    /// <summary>A player's game is simulating this crashed car and reported recently.</summary>
    public bool IsClientSimulated => _physicsOwner != 255 && _sessionManager.ServerTimeMilliseconds - _physicsReportAt < 600;

    /// <summary>Session id of the player whose game simulates this crashed car, 255 if none (the server does).</summary>
    public byte SimulatedBy => IsClientSimulated ? _physicsOwner : (byte)255;

    /// <summary>
    /// BetterTraffic: the crashed car as simulated by a player's game (CSP rigid body against the real track), shown to
    /// everybody. The first player to report owns the simulation until they stop reporting. Call on the server update loop.
    /// </summary>
    public void ApplyClientPhysics(byte owner, Vector3 position, Vector3 rotation, Vector3 velocity, float impactKph, bool resting)
    {
        if (!Initialized || !BT.Enabled || !BT.CrashPhysics) return;
        var now = _sessionManager.ServerTimeMilliseconds;
        if (IsClientSimulated && _physicsOwner != owner) return;

        var started = false;
        if (_crashMode is not (AiCrashMode.Sliding or AiCrashMode.Wrecked))
        {
            if (impactKph <= 0) return; // a stale message for a car that's back on the road
            StartFreeMotion();
            _crashImpactKph = impactKph;
            _crashSince = now;
            started = true;
        }
        else if (_crashMode == AiCrashMode.Wrecked && !resting)
        {
            _crashMode = AiCrashMode.Sliding; // knocked again
            started = impactKph > 0;
        }

        _physicsOwner = owner;
        _physicsReportAt = now;
        _reportPosition = position;
        _reportVelocity = velocity;
        _crashPosition = position;
        _crashVelocity = velocity;
        _crashYaw = rotation.X;
        _crashPitch = rotation.Y;
        _crashRoll = rotation.Z;
        _crashYawRate = 0;
        _crashRollTarget = 0;
        if (started)
        {
            Log.Debug("BetterTraffic: AI {SessionId} crashed at {Kph:F0} km/h, simulated by player {Owner}", EntryCar.SessionId, impactKph, owner);
            BetterTrafficCrash?.Invoke(this, AiCrashPhase.Hit, impactKph);
        }
        if (resting && _crashMode == AiCrashMode.Sliding)
        {
            _crashMode = AiCrashMode.Wrecked;
            _crashVelocity = Vector3.Zero;
            _reportVelocity = Vector3.Zero;
            _crashSince = now;
            Log.Debug("BetterTraffic: AI {SessionId} came to rest", EntryCar.SessionId);
            BetterTrafficCrash?.Invoke(this, AiCrashPhase.Settled, _crashImpactKph);
        }
    }

    /// <summary>Momentum from the player's car (lighter than traffic: TrafficMassKg vs PlayerMassKg), plus spin from an off-centre hit.</summary>
    private void Push(Vector3 contact, Vector3 normal, float closing)
    {
        var bt = BT;
        var share = bt.PlayerMassKg / MathF.Max(1, bt.PlayerMassKg + bt.TrafficMassKg);
        var dv = normal * (closing * share * 1.3f); // 1.3 = 1 + restitution
        _crashVelocity += dv;
        var r = contact - Status.Position;
        r.Y = 0;
        if (r.LengthSquared() > 2.2f * 2.2f) r = Vector3.Normalize(r) * 2.2f;
        // yaw (rotation X) grows from +X towards +Z, the opposite of a positive rotation about +Y
        var spin = -(r.Z * dv.X - r.X * dv.Z) / 2.0f;
        _crashYawRate = Math.Clamp(_crashYawRate + spin + Random.Shared.NextSingle(-0.4f, 0.4f), -5, 5);
    }

    private void UpdateCrash(float dt, long now)
    {
        var bt = BT;
        if (_crashMode == AiCrashMode.Wrecked && (now - _crashSince > bt.WreckMaxSeconds * 1000 || !IsPlayerWithin(bt.WreckClearMeters)))
        {
            Log.Debug("BetterTraffic: AI {SessionId} wreck cleared", EntryCar.SessionId);
            BetterTrafficCrash?.Invoke(this, AiCrashPhase.Cleared, _crashImpactKph);
            Despawn();
            return;
        }

        if (_physicsOwner != 255)
        {
            if (IsClientSimulated)
            {
                // a player's game simulates it: show its last report, extrapolated a little
                var ahead = MathF.Min(0.3f, (now - _physicsReportAt) / 1000f);
                WriteCrashStatus(now, _reportPosition + _reportVelocity * ahead, new Vector3(_crashYaw, _crashPitch, _crashRoll), _reportVelocity);
                return;
            }

            // the player stopped reporting (left, or too far): leave it where it was last seen
            _physicsOwner = 255;
            if (_crashMode == AiCrashMode.Sliding)
            {
                _crashMode = AiCrashMode.Wrecked;
                _crashSince = now;
                BetterTrafficCrash?.Invoke(this, AiCrashPhase.Settled, _crashImpactKph);
            }
            _reportVelocity = Vector3.Zero;
            WriteCrashStatus(now, _reportPosition, new Vector3(_crashYaw, _crashPitch, _crashRoll), Vector3.Zero);
            _frozenAtReport = true;
            return;
        }
        if (_frozenAtReport)
        {
            WriteCrashStatus(now, _reportPosition, new Vector3(_crashYaw, _crashPitch, _crashRoll), Vector3.Zero);
            return;
        }

        // the server simulates it (no player's game did): a simple slide that stays near the road
        var speed = _crashVelocity.Length();
        if (_crashMode == AiCrashMode.Sliding)
        {
            // off the road (where the server doesn't know the ground) cars stop much sooner
            var offRoad = Vector3.DistanceSquared(_crashPosition with { Y = 0 }, _spline.Points[CurrentSplinePointId].Position with { Y = 0 })
                          > MathF.Pow(_configuration.Extra.AiParams.LaneWidthMeters * 1.2f, 2);
            var deceleration = bt.CrashSlideDeceleration * (offRoad ? 3 : 1);
            var newSpeed = MathF.Max(0, speed - deceleration * dt);
            if (offRoad) _crashYawRate *= MathF.Exp(-3f * dt);
            if (speed > 0) _crashVelocity *= newSpeed / speed;
            _crashPosition += _crashVelocity * dt;
            _crashYaw += _crashYawRate * dt;
            _crashYawRate *= MathF.Exp(-1.4f * dt);
            if (_crashRollTarget != 0 && _crashRoll != _crashRollTarget)
            {
                var step = 4.5f * dt * MathF.Sign(_crashRollTarget - _crashRoll);
                _crashRoll = MathF.Abs(_crashRollTarget - _crashRoll) <= MathF.Abs(step) ? _crashRollTarget : _crashRoll + step;
            }
            speed = newSpeed;
            if (speed < 0.3f && MathF.Abs(_crashYawRate) < 0.15f && (_crashRollTarget == 0 || _crashRoll == _crashRollTarget))
            {
                _crashMode = AiCrashMode.Wrecked;
                _crashVelocity = Vector3.Zero;
                _crashYawRate = 0;
                speed = 0;
                _crashSince = now;
                Log.Debug("BetterTraffic: AI {SessionId} came to rest", EntryCar.SessionId);
                BetterTrafficCrash?.Invoke(this, AiCrashPhase.Settled, _crashImpactKph);
            }
        }

        FollowSplineHeight(_crashPosition);
        var groundY = _spline.Points[CurrentSplinePointId].Position.Y + EntryCar.AiSplineHeightOffsetMeters;
        // rolled cars sit higher: half the width on their side, the height on the roof
        var lift = MathF.Abs(MathF.Sin(_crashRoll)) * 0.9f + MathF.Max(0, -MathF.Cos(_crashRoll)) * 1.4f;
        WriteCrashStatus(now, _crashPosition with { Y = groundY + lift }, new Vector3(_crashYaw, _crashPitch, _crashRoll), _crashVelocity);
    }

    private void WriteCrashStatus(long now, Vector3 position, Vector3 rotation, Vector3 velocity)
    {
        Status.Timestamp = now;
        Status.Position = position;
        Status.Rotation = rotation;
        Status.Velocity = velocity;
        Status.SteerAngle = 127;
        Status.WheelAngle = 127;
        for (var i = 0; i < 4; i++) Status.TyreAngularSpeed[i] = 100; // locked wheels
        Status.EngineRpm = (ushort)EntryCar.AiIdleEngineRpm;
        Status.StatusFlag = GetLights(_configuration.Extra.AiParams.EnableDaytimeLights, _weatherManager.CurrentSunPosition, _randomTwilight)
                            | (_crashRollTarget == 0 ? CarStatusFlags.HazardsOn : 0)
                            | CarStatusFlags.BrakeLightsOn;
        Status.Gear = 1;
    }

    /// <summary>Keeps CurrentSplinePointId on the spline point nearest to a free-moving (crashed) car, for ground height and obstacle lookups.</summary>
    private void FollowSplineHeight(Vector3 position)
    {
        var points = _spline.Points;
        for (var i = 0; i < 4; i++)
        {
            var current = CurrentSplinePointId;
            var best = current;
            var bestDistance = Vector3.DistanceSquared(points[current].Position, position);
            foreach (var candidate in new[] { points[current].NextId, points[current].PreviousId })
            {
                if (candidate < 0) continue;
                var d = Vector3.DistanceSquared(points[candidate].Position, position);
                if (d < bestDistance)
                {
                    best = candidate;
                    bestDistance = d;
                }
            }
            if (best == current) break;
            CurrentSplinePointId = best;
        }
    }

    private bool IsPlayerWithin(float meters)
    {
        for (var i = 0; i < _entryCarManager.EntryCars.Length; i++)
        {
            var car = _entryCarManager.EntryCars[i];
            if (car.Client?.HasSentFirstUpdate == true && Vector3.DistanceSquared(car.Status.Position, Status.Position) < meters * meters) return true;
        }
        return false;
    }

    /// <summary>Sideways offset for pulling over after a minor crash: onto the shoulder from the slow lane, otherwise to the slow side of the lane.</summary>
    private Vector3 AdvancePullOver(float dt, Vector3 tangent)
    {
        if (_pullOverRejoining)
        {
            _pullOverProgress -= dt / 3.0f;
            if (_pullOverProgress <= 0)
            {
                _pullOverProgress = 0;
                _crashMode = AiCrashMode.None;
                _lcCooldownUntil = _sessionManager.ServerTimeMilliseconds + 3000;
                return Vector3.Zero;
            }
        }
        else
        {
            _pullOverProgress = MathF.Min(1, _pullOverProgress + dt / 2.0f);
        }

        var slow = SlowSideDirection;
        Vector3 direction;
        float meters;
        var slowAdjacent = AdjacentPoint(CurrentSplinePointId, slow);
        var fastAdjacent = AdjacentPoint(CurrentSplinePointId, -slow);
        if (slowAdjacent >= 0)
        {
            direction = LateralLaneVector(slowAdjacent, tangent);
            meters = 0.6f; // other lanes on that side: just keep to the edge of this lane
        }
        else
        {
            direction = fastAdjacent >= 0 ? -LateralLaneVector(fastAdjacent, tangent) : RightVector(tangent) * slow;
            meters = _configuration.Extra.AiParams.LaneWidthMeters * 0.8f;
        }
        if (direction.LengthSquared() < 1e-4f) return Vector3.Zero;
        return Vector3.Normalize(direction) * (meters * Smooth(_pullOverProgress));
    }

    /// <summary>Unit vector to the right of the direction of travel (worked out once from the spline's lane links).</summary>
    private Vector3 RightVector(Vector3 tangent)
    {
        var cross = Vector3.Cross(tangent with { Y = 0 }, Vector3.UnitY);
        if (cross.LengthSquared() < 1e-6f) return Vector3.Zero;
        if (_rightSign == 0)
        {
            var points = _spline.Points;
            _rightSign = 1;
            for (var i = 0; i < points.Length; i++)
            {
                if (points[i].RightId < 0 || points[i].NextId < 0) continue;
                var t = points[points[i].NextId].Position - points[i].Position;
                var c = Vector3.Cross(t with { Y = 0 }, Vector3.UnitY);
                _rightSign = Vector3.Dot(c, points[points[i].RightId].Position - points[i].Position) >= 0 ? 1 : -1;
                break;
            }
        }
        return Vector3.Normalize(cross) * _rightSign;
    }

    /// <summary>A player behind asked to pass (high-beam flash). Returns false if this driver ignores it.</summary>
    public bool RequestYield()
    {
        if (!Initialized || !BT.Enabled || !BT.YieldOnFlash || _stubborn) return false;
        _yieldUntil = _sessionManager.ServerTimeMilliseconds + 5000;
        Log.Debug("BetterTraffic: AI {SessionId} asked to yield", EntryCar.SessionId);
        return true;
    }

    /// <summary>Brake check: a short, sharp slowdown (not a stop) for AiParams.BetterTraffic.BrakeCheckSeconds.</summary>
    public void BrakeCheck()
    {
        if (!Initialized || !BT.Enabled || _crashMode != AiCrashMode.None) return;
        _brakeCheckUntil = _sessionManager.ServerTimeMilliseconds + (long)(BT.BrakeCheckSeconds * 1000);
    }

    /// <summary>Lane change decisions, called from obstacle detection (every 100 ms).</summary>
    private void DecideLaneChange(AiState? closestAi, float closestAiDistance, (EntryCar? entryCar, float distance) playerObstacle)
    {
        var bt = BT;
        var now = _sessionManager.ServerTimeMilliseconds;
        if (!bt.Enabled || !bt.LaneChanges || _lcDirection != 0 || _lcRequest != 0 || now < _lcCooldownUntil) return;
        if (now < _stoppedForCollisionUntil || CurrentSpeed < 30 / 3.6f || _crashMode != AiCrashMode.None) return;

        var slow = SlowSideDirection;
        var fast = -slow;

        // 1. yield to a player who flashed: move to the slow side
        if (now < _yieldUntil)
        {
            if (AdjacentPoint(CurrentSplinePointId, slow) < 0)
            {
                // already in the slow lane: the player can pass on the other side
                Log.Debug("BetterTraffic: AI {SessionId} already in the slow lane, nothing to yield", EntryCar.SessionId);
                _yieldUntil = 0;
            }
            else if (IsLaneFree(slow, bt.LaneFreeBehindMeters, bt.LaneFreeAheadMeters))
            {
                _lcRequest = slow;
                _yieldUntil = 0;
            }
            return; // slow lane busy: keep trying until the request expires
        }

        // 2. overtake a slower car (AI or player) ahead
        var deltaMs = bt.OvertakeSpeedDeltaKph * _personality.OvertakeFactor / 3.6f;
        var slowAhead = closestAi != null && closestAiDistance < bt.OvertakeLookaheadMeters
                        && Math.Min(closestAi.CurrentSpeed, closestAi.TargetSpeed) < InitialMaxSpeed - deltaMs;
        var playerAhead = playerObstacle.entryCar != null && playerObstacle.distance < bt.OvertakeLookaheadMeters
                          && playerObstacle.entryCar.Status.Velocity.Length() < InitialMaxSpeed - deltaMs;
        if (slowAhead || playerAhead)
        {
            if (IsLaneFree(fast, bt.LaneFreeBehindMeters, bt.LaneFreeAheadMeters)) _lcRequest = fast;
            else if (IsLaneFree(slow, bt.LaneFreeBehindMeters, bt.LaneFreeAheadMeters)) _lcRequest = slow; // undertake as a last resort
            return;
        }

        // 3. keep to the slow lane when it's free for a good while ahead
        if (AdjacentPoint(CurrentSplinePointId, slow) >= 0
            && Random.Shared.NextSingle() < bt.ReturnToSlowLaneChance * _personality.ReturnFactor * 0.1f
            && IsLaneFree(slow, bt.LaneFreeBehindMeters, bt.LaneFreeAheadMeters * 3))
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
        if (!BT.Enabled) return Vector3.Zero;

        if (_lcDirection == 0 && _lcRequest != 0)
        {
            _lcDirection = _lcRequest;
            _lcRequest = 0;
            _lcProgress = 0;
            _laneChangeIndicator = Random.Shared.NextSingle() >= _personality.IndicatorUse ? 0
                : _lcDirection < 0 ? CarStatusFlags.IndicateLeft : CarStatusFlags.IndicateRight;
            Log.Debug("BetterTraffic: AI {SessionId} lane change {Direction} at {Speed:F0} km/h", EntryCar.SessionId, _lcDirection < 0 ? "left" : "right", CurrentSpeed * 3.6f);
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
        _lcCooldownUntil = _sessionManager.ServerTimeMilliseconds + (long)(BT.LaneChangeCooldownSeconds * 1000);
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

        if (_crashMode is AiCrashMode.Sliding or AiCrashMode.Wrecked)
        {
            UpdateCrash(dt / 1000.0f, currentTime);
            return;
        }

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
            
        if (_configuration.Extra.AiParams.BetterTraffic.Enabled)
        {
            var lateral = AdvanceLaneChange(dt / 1000.0f, smoothPos.Position, smoothPos.Tangent, out var lateralVelocity);
            if (_swayAmplitude > 0)
            {
                var right = Vector3.Cross(smoothPos.Tangent, Vector3.UnitY);
                if (right.LengthSquared() > 0.0001f)
                    lateral += Vector3.Normalize(right) * (MathF.Sin(currentTime / 1000.0f * _swayFrequency * MathF.PI * 2 + _swayPhase) * _swayAmplitude);
            }
            if (_crashMode == AiCrashMode.PullOver) lateral += AdvancePullOver(dt / 1000.0f, smoothPos.Tangent);
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
                            | (_sessionManager.ServerTimeMilliseconds < _stoppedForCollisionUntil || CurrentSpeed < 20 / 3.6f || _crashMode != AiCrashMode.None || _sessionManager.ServerTimeMilliseconds < _tapUntil ? CarStatusFlags.HazardsOn : 0)
                            | (CurrentSpeed == 0 || Acceleration < 0 ? CarStatusFlags.BrakeLightsOn : 0)
                            | (_stoppedForObstacle && _sessionManager.ServerTimeMilliseconds > _obstacleHonkStart && _sessionManager.ServerTimeMilliseconds < _obstacleHonkEnd ? CarStatusFlags.Horn : 0)
                            | GetWiperSpeed(_weatherManager.CurrentWeather.RainIntensity)
                            | (_crashMode != AiCrashMode.None ? 0 : _laneChangeIndicator != 0 ? _laneChangeIndicator : _indicator);
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

/// <summary>BetterTraffic: what a crash did to an AI car.</summary>
public enum AiCrashMode
{
    None,
    PullOver,
    Sliding,
    Wrecked,
}

public enum AiCrashPhase
{
    Hit,
    Settled,
    Cleared,
}
