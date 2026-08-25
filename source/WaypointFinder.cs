using System;
using System.Collections.Generic;
using System.Linq;

namespace GW2WikiTool;

public sealed record NearbyWp(PointOfInterest Poi, double Dist);

public static class WpFinder
{
    public static NearbyWp? FindClosest(IEnumerable<PointOfInterest> pois, double x, double y) =>
        GetNearest(pois, x, y, 1).FirstOrDefault();

    public static IReadOnlyList<NearbyWp> GetNearest(IEnumerable<PointOfInterest> pois, double x, double y, int count = 5)
    {
        return pois
            .Where(p => p.Type == "waypoint" && p.Coord.Length == 2)
            .Select(p => new NearbyWp(p, Dist(p.Coord[0], p.Coord[1], x, y)))
            .OrderBy(n => n.Dist)
            .Take(count)
            .ToList();
    }

    private static double Dist(double x1, double y1, double x2, double y2)
    {
        double dx = x1 - x2, dy = y1 - y2;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}