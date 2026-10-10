using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using AssettoServer.Server.Ai.Configuration;
using AssettoServer.Shared.Network.Packets.Outgoing;
using Serilog;
using Supercluster.KDTree;

namespace AssettoServer.Server.Ai.Splines;

public class MutableAiSpline
{
    public Dictionary<string, FastLane> Splines { get; }
    public SplinePoint[] Points { get; }
    public List<SplineJunction> Junctions { get; } = [];
    public KDTree<int> KdTree { get; }
    public List<int[]> Lanes { get; }

    private readonly ILogger _logger;

    private readonly float smoothJoinMeters;

    /// <summary>
    /// BetterTraffic: the point on the target lane to join, starting at the map's join point and going further along
    /// it (up to <paramref name="maxMeters"/>) until the step from the lane's last point is mostly forwards, not sideways.
    /// </summary>
    private int SmoothJoin(int fromId, int target, float maxMeters)
    {
        var last = Points[fromId].Position;
        var previousId = Points[fromId].PreviousId;
        if (previousId < 0) return target;
        var beforeLast = Points[previousId].Position;
        var direction = (last - beforeLast) with { Y = 0 };
        if (direction.LengthSquared() < 1e-6f) return target;
        direction = Vector3.Normalize(direction);

        static float Sideways(Vector3 step, Vector3 dir, out float along)
        {
            step = step with { Y = 0 };
            along = Vector3.Dot(step, dir);
            return (step - dir * along).Length();
        }

        var sideways = Sideways(Points[target].Position - last, direction, out var forward);
        if (sideways <= 1 && forward > 0) return target; // already a clean join
        float travelled = 0;
        for (var id = target; id >= 0 && travelled <= maxMeters; id = Points[id].NextId)
        {
            sideways = Sideways(Points[id].Position - last, direction, out forward);
            // a point can only be the end of one junction: skip points other joins already use
            var free = id == target || (Points[id].JunctionEndId < 0 && Points[id].JunctionStartId < 0);
            if (free && forward > 0 && sideways <= forward * 0.25f) return id;
            travelled += Points[id].Length;
            if (Points[id].NextId == target) break; // looped
        }
        return target;
    }

    internal MutableAiSpline(Dictionary<string, FastLane> splines, float laneWidth, bool twoWayTraffic = false, TrafficConfiguration? configuration = null, ILogger? logger = null, float smoothJoinMeters = 0)
    {
        this.smoothJoinMeters = smoothJoinMeters;
        _logger = logger ?? Log.Logger;
        Splines = splines;

        int total = splines.Values.Sum(s => s.Points.Length);
        Points = new SplinePoint[total];
        
        int i = 0;
        foreach (var point in splines.Values.SelectMany(spline => spline.Points))
        {
            if (i != point.Id)
            {
                throw new InvalidOperationException("Mismatched ID");
            }

            Points[i] = point;
            i++;
        }
            
        var treeData = CreateTreeData();
        var treeNodes = Enumerable.Range(0, Points.Length).ToArray();

        KdTree = new KDTree<int>(treeData, treeNodes);
            
        AdjacentLaneDetector.DetectAdjacentLanes(this, laneWidth, twoWayTraffic);

        var ops = new SplinePointOperations(Points);
        Lanes = new List<int[]>();
        int offset = 0;
        for (i = 0; i < Points.Length; i++)
        {
            if (Points[i].LanesId == -1)
            {
                var lanes = ops.GetLanes(i, twoWayTraffic).ToArray();
                Lanes.Add(lanes);
                foreach (var lane in lanes)
                {
                    Points[lane].LanesId = offset;
                }

                offset += sizeof(int) + lanes.Length * sizeof(int);
            }
        }
        
        if (configuration != null)
        {
            ApplyConfiguration(configuration);
        }
    }

    private Vector3[] CreateTreeData()
    {
        return Points.Select(point => point.Position).ToArray();
    }

    private ref SplinePoint GetByIdentifier(string identifier)
    {
        int separator = identifier.IndexOf('@');
        string splineName = identifier.Substring(0, separator);
        int id = int.Parse(identifier.Substring(separator + 1));
        int globalId = Splines[splineName].Points[id].Id;
        return ref Points[globalId];
    }

    public (int PointId, float DistanceSquared) WorldToSpline(Vector3 position)
    {
        var nearest = KdTree.NearestNeighbors(position, 1);
        if (nearest.Length == 0)
        {
            return (-1, float.PositiveInfinity);
        }

        float dist = Vector3.DistanceSquared(position, Points[nearest[0].Item2].Position);
        return (nearest[0].Item2, dist);
    }

    private void ApplyConfiguration(TrafficConfiguration config)
    {
        int junctionsIndex = 0;
        
        foreach (var spline in config.Splines)
        {
            var startSpline = Splines[spline.Name];

            if (spline.ConnectEnd != null)
            {
                ref var endPoint = ref GetByIdentifier(spline.ConnectEnd);
                ref var startPoint = ref Points[startSpline.Points[^1].Id];
                if (smoothJoinMeters > 0)
                {
                    var better = SmoothJoin(startPoint.Id, endPoint.Id, smoothJoinMeters);
                    if (better != endPoint.Id)
                    {
                        _logger.Information("BetterTraffic: lane {Name} joins {Target} {Meters:F0} m further along (sideways step at the join)",
                            spline.Name, spline.ConnectEnd, Vector3.Distance(Points[better].Position, endPoint.Position));
                        endPoint = ref Points[better];
                    }
                }
                
                startPoint.NextId = endPoint.Id;
                
                var jct = new SplineJunction
                {
                    Id = junctionsIndex++,
                    StartPointId = startPoint.Id,
                    EndPointId = endPoint.Id,
                    Probability = 1.0f,
                    IndicateWhenTaken = IndicatorToStatusFlags(spline.IndicateEnd),
                    IndicateDistancePre = spline.IndicateEndDistancePre,
                    IndicateDistancePost = spline.IndicateEndDistancePost
                };

                startPoint.JunctionStartId = jct.Id;
                endPoint.JunctionEndId = jct.Id;

                Junctions.Add(jct);
            }

            foreach (var junction in spline.Junctions)
            {
                _logger.Debug("Junction {Name} from {StartSpline} {StartId} to {End}", junction.Name, startSpline.Name, junction.Start, junction.End);

                ref var startPoint = ref Points[startSpline.Points[junction.Start].Id];
                ref var endPoint = ref GetByIdentifier(junction.End);
                if (smoothJoinMeters > 0)
                {
                    var better = SmoothJoin(startPoint.Id, endPoint.Id, smoothJoinMeters);
                    if (better != endPoint.Id)
                    {
                        _logger.Information("BetterTraffic: junction {Name} joins {Target} {Meters:F0} m further along (sideways step at the join)",
                            junction.Name, junction.End, Vector3.Distance(Points[better].Position, endPoint.Position));
                        endPoint = ref Points[better];
                    }
                }

                var jct = new SplineJunction
                {
                    Id = junctionsIndex++,
                    StartPointId = startPoint.Id,
                    EndPointId = endPoint.Id,
                    Probability = junction.Probability,
                    IndicateWhenTaken = IndicatorToStatusFlags(junction.IndicateWhenTaken),
                    IndicateWhenNotTaken = IndicatorToStatusFlags(junction.IndicateWhenNotTaken),
                    IndicateDistancePre = junction.IndicateDistancePre,
                    IndicateDistancePost = junction.IndicateDistancePost
                };

                startPoint.JunctionStartId = jct.Id;
                endPoint.JunctionEndId = jct.Id;

                Junctions.Add(jct);
            }
        }
    }

    private static CarStatusFlags IndicatorToStatusFlags(Indicator indicator)
    {
        return indicator switch
        {
            Indicator.Left => CarStatusFlags.IndicateLeft,
            Indicator.Right => CarStatusFlags.IndicateRight,
            _ => 0
        };
    }
}
