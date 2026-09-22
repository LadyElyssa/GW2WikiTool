using System;
using System.Collections.Generic;
using System.Linq;

namespace GW2WikiTool;

public sealed record NearbyWaypoint(PointOfInterest Poi, double Distance);

/// <summary>Finds the waypoints closest to a continent-coordinate position among a set of points of interest.</summary>
public static class WaypointFinder
{
    /// <summary>The single closest waypoint, or null if none are present.</summary>
    public static NearbyWaypoint? FindNearestWaypoint(IEnumerable<PointOfInterest> pointsOfInterest, double x, double y) =>
        NearestWaypoints(pointsOfInterest, x, y, 1).FirstOrDefault();

    /// <summary>The closest <paramref name="count"/> waypoints, nearest first.</summary>
    public static IReadOnlyList<NearbyWaypoint> NearestWaypoints(IEnumerable<PointOfInterest> pointsOfInterest, double x, double y, int count = 5)
    {
        return pointsOfInterest
            .Where(p => p.Type == "waypoint" && p.Coord.Length == 2)
            .Select(p => new NearbyWaypoint(p, Distance(p.Coord[0], p.Coord[1], x, y)))
            .OrderBy(n => n.Distance)
            .Take(count)
            .ToList();
    }

    private static double Distance(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2, dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
