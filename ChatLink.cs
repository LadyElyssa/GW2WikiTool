using System;

namespace GW2WikiTool;

/// <summary>
/// Builds and parses GW2 chat links — the "[&amp;xxxx=]" codes you paste in-game to link a
/// waypoint, landmark, vista, item, etc.
/// Reference: https://wiki.guildwars2.com/wiki/Chat_link_format
/// </summary>
public static class ChatLink
{
    // Waypoints, landmarks (points of interest), and vistas all share this one header byte —
    // the game tells them apart by the id itself, not the link type.
    private const byte TypePointOfInterest = 0x04;

    /// <summary>
    /// Builds a chat link from a bare point-of-interest id (waypoint, landmark, or vista).
    /// Layout: [type=0x04][id as 3-byte little-endian][0x00 padding], base64-encoded.
    /// If you already have a PointOfInterest from Gw2ApiClient.GetMapAsync(), prefer its own
    /// ChatLink field — the API returns it precomputed and this is only needed when you have
    /// just a bare id (e.g. from the wiki's chat-code tables).
    /// </summary>
    public static string ForPointOfInterest(int poiId)
    {
        if (poiId < 0 || poiId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(poiId), "POI ids must fit in 3 bytes.");

        Span<byte> bytes = stackalloc byte[5];
        bytes[0] = TypePointOfInterest;
        bytes[1] = (byte)(poiId & 0xFF);
        bytes[2] = (byte)((poiId >> 8) & 0xFF);
        bytes[3] = (byte)((poiId >> 16) & 0xFF);
        bytes[4] = 0x00;
        return $"[&{Convert.ToBase64String(bytes)}]";
    }

    /// <summary>Alias for ForPointOfInterest — waypoints use the same 0x04 encoding as
    /// landmarks/vistas, this name just matches how most people ask for "waypoint codes".</summary>
    public static string ForWaypoint(int waypointPoiId) => ForPointOfInterest(waypointPoiId);

    /// <summary>
    /// Decodes a "[&amp;xxxx=]" chat link back into its type byte and (for 0x04-style 3-byte-id
    /// links) numeric id. Returns null if the string isn't in the recognized shape.
    /// </summary>
    public static (byte Type, int Id)? Decode(string chatLink)
    {
        var trimmed = chatLink.Trim();
        if (trimmed.StartsWith("[&") && trimmed.EndsWith("]"))
            trimmed = trimmed[2..^1];

        byte[] bytes;
        try { bytes = Convert.FromBase64String(trimmed); }
        catch (FormatException) { return null; }

        if (bytes.Length < 4) return null;

        byte type = bytes[0];
        int id = bytes[1] | (bytes[2] << 8) | (bytes[3] << 16);
        return (type, id);
    }
}
