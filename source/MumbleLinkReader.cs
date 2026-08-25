using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GW2WikiTool;

public sealed class Gw2Identity
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("profession")] public int Prof { get; set; }
    [JsonPropertyName("spec")] public int Spec { get; set; }
    [JsonPropertyName("race")] public int Race { get; set; }
    [JsonPropertyName("map_id")] public int MapId { get; set; }
    [JsonPropertyName("world_id")] public long WorldId { get; set; }
    [JsonPropertyName("team_color_id")] public int TeamColorId { get; set; }
    [JsonPropertyName("commander")] public bool Cmdr { get; set; }
    [JsonPropertyName("map_open")] public bool MapOpen { get; set; }
    [JsonPropertyName("fov")] public double Fov { get; set; }
    [JsonPropertyName("uisz")] public int UiSz { get; set; }
}

public sealed class Gw2Context
{
    public uint MapId { get; init; }
    public uint MapType { get; init; }
    public uint ShardId { get; init; }
    public uint Instance { get; init; }
    public uint BuildId { get; init; }
    public uint UiState { get; init; }
    public ushort CompW { get; init; }
    public ushort CompH { get; init; }
    public float CompRot { get; init; }
    public float PlayerX { get; init; }
    public float PlayerY { get; init; }
    public float MapCtrX { get; init; }
    public float MapCtrY { get; init; }
    public float MapScale { get; init; }
    public uint ProcId { get; init; }
    public byte MountIdx { get; init; }
}

public sealed record Vector3(float X, float Y, float Z);

public sealed class MumbleLinkSnapshot
{
    public uint UiVer { get; init; }
    public uint UiTick { get; init; }
    public Vector3 AvPos { get; init; } = new(0, 0, 0);
    public Vector3 AvFront { get; init; } = new(0, 0, 0);
    public Vector3 AvTop { get; init; } = new(0, 0, 0);
    public string Name { get; init; } = "";
    public Vector3 CamPos { get; init; } = new(0, 0, 0);
    public Vector3 CamFront { get; init; } = new(0, 0, 0);
    public Vector3 CamTop { get; init; } = new(0, 0, 0);
    public Gw2Identity? Identity { get; init; }
    public Gw2Context? Context { get; init; }
    public string Desc { get; init; } = "";

    public bool IsActive => UiTick != 0;
}

public sealed class MumbleLinkReader : IDisposable
{
    private const int TotalSz = 5460;

    private const int OffUiVer = 0;
    private const int OffUiTick = 4;
    private const int OffAvPos = 8;
    private const int OffAvFront = 20;
    private const int OffAvTop = 32;
    private const int OffName = 44;
    private const int OffCamPos = 556;
    private const int OffCamFront = 568;
    private const int OffCamTop = 580;
    private const int OffIdentity = 592;
    private const int OffCtxLen = 1104;
    private const int OffCtx = 1108;
    private const int OffDesc = 1364;

    private const int CtxMapId = 28;
    private const int CtxMapType = 32;
    private const int CtxShardId = 36;
    private const int CtxInstance = 40;
    private const int CtxBuildId = 44;
    private const int CtxUiState = 48;
    private const int CtxCompW = 52;
    private const int CtxCompH = 54;
    private const int CtxCompRot = 56;
    private const int CtxPlayerX = 60;
    private const int CtxPlayerY = 64;
    private const int CtxMapCtrX = 68;
    private const int CtxMapCtrY = 72;
    private const int CtxMapScale = 76;
    private const int CtxProcId = 80;
    private const int CtxMountIdx = 84;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _view;
    private readonly byte[] _raw = new byte[TotalSz];

    public bool TryConnect(string mapName = "MumbleLink")
    {
        if (_mmf != null) return true;

        try
        {
            _mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            _view = _mmf.CreateViewAccessor(0, TotalSz, MemoryMappedFileAccess.Read);
            return true;
        }
        catch (Exception)
        {
            _mmf?.Dispose();
            _mmf = null;
            _view = null;
            return false;
        }
    }

    public bool IsConnected => _view != null;

    public MumbleLinkSnapshot? Read()
    {
        if (_view == null && !TryConnect())
            return null;

        _view!.ReadArray(0, _raw, 0, TotalSz);

        uint tick = BitConverter.ToUInt32(_raw, OffUiTick);
        if (tick == 0)
        {
            return new MumbleLinkSnapshot { UiVer = BitConverter.ToUInt32(_raw, OffUiVer), UiTick = 0 };
        }

        var identityRaw = ReadWStr(OffIdentity, 256);
        Gw2Identity? identity = null;
        if (!string.IsNullOrWhiteSpace(identityRaw))
        {
            // sometimes the game writes garbage mid-frame, just skip it
            try { identity = JsonSerializer.Deserialize<Gw2Identity>(identityRaw); }
            catch (Exception) { }
        }

        uint ctxLen = BitConverter.ToUInt32(_raw, OffCtxLen);
        Gw2Context? ctx = null;
        if (ctxLen >= CtxMountIdx + 1)
        {
            ctx = new Gw2Context
            {
                MapId = BitConverter.ToUInt32(_raw, OffCtx + CtxMapId),
                MapType = BitConverter.ToUInt32(_raw, OffCtx + CtxMapType),
                ShardId = BitConverter.ToUInt32(_raw, OffCtx + CtxShardId),
                Instance = BitConverter.ToUInt32(_raw, OffCtx + CtxInstance),
                BuildId = BitConverter.ToUInt32(_raw, OffCtx + CtxBuildId),
                UiState = BitConverter.ToUInt32(_raw, OffCtx + CtxUiState),
                CompW = BitConverter.ToUInt16(_raw, OffCtx + CtxCompW),
                CompH = BitConverter.ToUInt16(_raw, OffCtx + CtxCompH),
                CompRot = BitConverter.ToSingle(_raw, OffCtx + CtxCompRot),
                PlayerX = BitConverter.ToSingle(_raw, OffCtx + CtxPlayerX),
                PlayerY = BitConverter.ToSingle(_raw, OffCtx + CtxPlayerY),
                MapCtrX = BitConverter.ToSingle(_raw, OffCtx + CtxMapCtrX),
                MapCtrY = BitConverter.ToSingle(_raw, OffCtx + CtxMapCtrY),
                MapScale = BitConverter.ToSingle(_raw, OffCtx + CtxMapScale),
                ProcId = BitConverter.ToUInt32(_raw, OffCtx + CtxProcId),
                MountIdx = _raw[OffCtx + CtxMountIdx],
            };
        }

        return new MumbleLinkSnapshot
        {
            UiVer = BitConverter.ToUInt32(_raw, OffUiVer),
            UiTick = tick,
            AvPos = ReadVec3(OffAvPos),
            AvFront = ReadVec3(OffAvFront),
            AvTop = ReadVec3(OffAvTop),
            Name = ReadWStr(OffName, 256),
            CamPos = ReadVec3(OffCamPos),
            CamFront = ReadVec3(OffCamFront),
            CamTop = ReadVec3(OffCamTop),
            Identity = identity,
            Context = ctx,
            Desc = ReadWStr(OffDesc, 2048),
        };
    }

    private Vector3 ReadVec3(int off) => new(
        BitConverter.ToSingle(_raw, off),
        BitConverter.ToSingle(_raw, off + 4),
        BitConverter.ToSingle(_raw, off + 8));

    private string ReadWStr(int off, int maxChars)
    {
        int byteLen = maxChars * 2;
        int nullIdx = -1;
        for (int i = 0; i < byteLen - 1; i += 2)
        {
            if (_raw[off + i] == 0 && _raw[off + i + 1] == 0)
            {
                nullIdx = i;
                break;
            }
        }
        int len = nullIdx >= 0 ? nullIdx : byteLen;
        return Encoding.Unicode.GetString(_raw, off, len);
    }

    public void Dispose()
    {
        _view?.Dispose();
        _mmf?.Dispose();
    }
}