using System;

namespace GW2WikiTool;

public static class Gw2Coords
{
    private const double MToIn = 39.3700787;

    public static (double X, double Y)? FromCtx(Gw2Context? ctx)
    {
        if (ctx == null) return null;
        if (ctx.PlayerX == 0f && ctx.PlayerY == 0f) return null;
        return (ctx.PlayerX, ctx.PlayerY);
    }

    public static (double X, double Y) Convert(double wx, double wz, double[][] mapRect, double[][] contRect)
    {
        double mapMinX = mapRect[0][0], mapMinY = mapRect[0][1];
        double mapMaxX = mapRect[1][0], mapMaxY = mapRect[1][1];
        double contMinX = contRect[0][0], contMinY = contRect[0][1];
        double contMaxX = contRect[1][0], contMaxY = contRect[1][1];

        double pctX = (wx - mapMinX) / (mapMaxX - mapMinX);
        double pctY = 1 - (wz - mapMinY) / (mapMaxY - mapMinY);

        double contX = contMinX + (contMaxX - contMinX) * pctX;
        double contY = contMinY + (contMaxY - contMinY) * pctY;
        return (contX, contY);
    }

    public static (double X, double Y) FromMumble(Vector3 pos, double[][] mapRect, double[][] contRect)
    {
        double wx = pos.X * MToIn;
        double wz = pos.Z * MToIn;
        return Convert(wx, wz, mapRect, contRect);
    }
}