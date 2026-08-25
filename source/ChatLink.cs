using System;

namespace GW2WikiTool;

public static class ChatLink
{
    private const byte TypePoi = 0x04;

    public static string MakeLink(int poiId)
    {
        if (poiId < 0 || poiId > 0xFFFFFF)
            throw new ArgumentOutOfRangeException(nameof(poiId), "POI ids must fit in 3 bytes.");

        Span<byte> raw = stackalloc byte[5];
        raw[0] = TypePoi;
        raw[1] = (byte)(poiId & 0xFF);
        raw[2] = (byte)((poiId >> 8) & 0xFF);
        raw[3] = (byte)((poiId >> 16) & 0xFF);
        raw[4] = 0x00;
        return $"[&{Convert.ToBase64String(raw)}]";
    }

    public static string MakeWpLink(int id) => MakeLink(id);

    public static (byte Type, int Id)? Crack(string link)
    {
        var s = link.Trim();
        if (s.StartsWith("[&") && s.EndsWith("]"))
            s = s[2..^1];

        byte[] raw;
        try { raw = Convert.FromBase64String(s); }
        catch (FormatException) { return null; }

        if (raw.Length < 4) return null;

        byte type = raw[0];
        int id = raw[1] | (raw[2] << 8) | (raw[3] << 16);
        return (type, id);
    }
}