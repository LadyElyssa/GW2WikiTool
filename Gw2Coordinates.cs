using System;

namespace GW2WikiTool;

/// <summary>
/// Converts in-game positions into "continent coordinates" — the same coordinate system the
/// GW2 wiki's interactive maps use, and the same one used by map_rect/continent_rect and
/// waypoint/POI "coord" fields from the /v2/maps API.
/// Reference: https://wiki.guildwars2.com/wiki/API:MumbleLink and
///            https://wiki.guildwars2.com/wiki/API:1/event_details#Coordinate_recalculation
/// </summary>
public static class Gw2Coordinates
{
    private const double MetersToInches = 39.3700787;

    /// <summary>
    /// The simplest and most reliable path: GW2 already writes the player's continent position
    /// directly into MumbleLink's Context block (playerX/playerY) for its own compass — no map
    /// lookup needed. Returns null if the context wasn't parsed or hasn't been populated yet
    /// (both values still zero, e.g. right after zoning).
    /// </summary>
    public static (double X, double Y)? FromContext(Gw2Context? ctx)
    {
        if (ctx == null) return null;
        if (ctx.PlayerX == 0f && ctx.PlayerY == 0f) return null;
        return (ctx.PlayerX, ctx.PlayerY);
    }

    /// <summary>
    /// General-purpose conversion for a raw world position already in inches (GW2's native unit)
    /// e.g. positions from the /v2/events API. 
    /// Requires the target map's MapRect and ContinentRect (from Gw2ApiClient.GetMapAsync).
    /// </summary>
    public static (double X, double Y) WorldToContinent(double worldX, double worldZ, double[][] mapRect, double[][] continentRect)
    {
        double mapMinX = mapRect[0][0], mapMinY = mapRect[0][1];
        double mapMaxX = mapRect[1][0], mapMaxY = mapRect[1][1];
        double contMinX = continentRect[0][0], contMinY = continentRect[0][1];
        double contMaxX = continentRect[1][0], contMaxY = continentRect[1][1];

        double pctX = (worldX - mapMinX) / (mapMaxX - mapMinX);
        // Map-space Y increases north; continent-space Y increases south — hence the inversion.
        double pctY = 1 - (worldZ - mapMinY) / (mapMaxY - mapMinY);

        double contX = contMinX + (contMaxX - contMinX) * pctX;
        double contY = contMinY + (contMaxY - contMinY) * pctY;
        return (contX, contY);
    }

    /// <summary>
    /// Same conversion, but takes MumbleLink's AvatarPosition directly (in meters, GW2/Mumble's
    /// left-handed system — X and Z are the ground plane, Y is up, so Z is used as "world Y"
    /// here). Prefer FromContext() when possible; this exists as a fallback for older clients or
    /// for converting the camera position instead of the avatar.
    /// </summary>
    public static (double X, double Y) FromMumbleAvatarPosition(Vector3 avatarPosition, double[][] mapRect, double[][] continentRect)
    {
        double worldX = avatarPosition.X * MetersToInches;
        double worldZ = avatarPosition.Z * MetersToInches;
        return WorldToContinent(worldX, worldZ, mapRect, continentRect);
    }
}
