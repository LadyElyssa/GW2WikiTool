using System;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GW2WikiTool;

/// <summary>
/// Parsed "identity" JSON block that GW2 writes into MumbleLink.
/// </summary>
public sealed class Gw2Identity
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("profession")]
    public int Profession { get; set; }

    [JsonPropertyName("spec")]
    public int Spec { get; set; }

    [JsonPropertyName("race")]
    public int Race { get; set; }

    [JsonPropertyName("map_id")]
    public int MapId { get; set; }

    [JsonPropertyName("world_id")]
    public long WorldId { get; set; }

    [JsonPropertyName("team_color_id")]
    public int TeamColorId { get; set; }

    [JsonPropertyName("commander")]
    public bool Commander { get; set; }

    [JsonPropertyName("map_open")]
    public bool MapOpen { get; set; }

    [JsonPropertyName("fov")]
    public double Fov { get; set; }

    [JsonPropertyName("uisz")]
    public int UiSize { get; set; }
}

/// <summary>
/// GW2's extra "Context" block embedded in MumbleLink's context byte array.
/// Layout reference: https://wiki.guildwars2.com/wiki/API:MumbleLink
/// </summary>
public sealed class Gw2Context
{
    public uint MapId { get; init; }
    public uint MapType { get; init; }
    public uint ShardId { get; init; }
    public uint Instance { get; init; }
    public uint BuildId { get; init; }
    public uint UiState { get; init; }
    public ushort CompassWidth { get; init; }
    public ushort CompassHeight { get; init; }
    public float CompassRotation { get; init; }
    public float PlayerX { get; init; }
    public float PlayerY { get; init; }
    public float MapCenterX { get; init; }
    public float MapCenterY { get; init; }
    public float MapScale { get; init; }
    public uint ProcessId { get; init; }
    public byte MountIndex { get; init; }
}

public sealed record Vector3(float X, float Y, float Z);

/// <summary>
/// Full snapshot of one MumbleLink read.
/// </summary>
public sealed class MumbleLinkSnapshot
{
    public uint UiVersion { get; init; }
    public uint UiTick { get; init; }
    public Vector3 AvatarPosition { get; init; } = new(0, 0, 0);
    public Vector3 AvatarFront { get; init; } = new(0, 0, 0);
    public Vector3 AvatarTop { get; init; } = new(0, 0, 0);
    public string Name { get; init; } = "";
    public Vector3 CameraPosition { get; init; } = new(0, 0, 0);
    public Vector3 CameraFront { get; init; } = new(0, 0, 0);
    public Vector3 CameraTop { get; init; } = new(0, 0, 0);
    public Gw2Identity? Identity { get; init; }
    public Gw2Context? Context { get; init; }
    public string Description { get; init; } = "";

    /// <summary>True once the game has actually written data (uiTick advances each frame).</summary>
    public bool IsActive => UiTick != 0;
}

/// <summary>
/// Reads and parses the "MumbleLink" shared memory segment written by GW2 (and Mumble-compatible games).
/// Windows-only: the segment is a named file mapping created via CreateFileMapping/MapViewOfFile.
/// </summary>
public sealed class MumbleLinkReader : IDisposable
{
    // Fixed byte layout of the LinkedMem struct GW2/Mumble write. Total size: 5460 bytes.
    private const int TotalSize = 5460;

    private const int OffsetUiVersion = 0;
    private const int OffsetUiTick = 4;
    private const int OffsetAvatarPosition = 8;    // 3 floats
    private const int OffsetAvatarFront = 20;       // 3 floats
    private const int OffsetAvatarTop = 32;         // 3 floats
    private const int OffsetName = 44;               // wchar_t[256] -> 512 bytes
    private const int OffsetCameraPosition = 556;    // 3 floats
    private const int OffsetCameraFront = 568;       // 3 floats
    private const int OffsetCameraTop = 580;         // 3 floats
    private const int OffsetIdentity = 592;          // wchar_t[256] -> 512 bytes
    private const int OffsetContextLen = 1104;
    private const int OffsetContext = 1108;          // byte[256]
    private const int OffsetDescription = 1364;      // wchar_t[2048] -> 4096 bytes

    // Sub-offsets inside the 256-byte GW2 Context block.
    private const int CtxMapId = 28;
    private const int CtxMapType = 32;
    private const int CtxShardId = 36;
    private const int CtxInstance = 40;
    private const int CtxBuildId = 44;
    private const int CtxUiState = 48;
    private const int CtxCompassWidth = 52;
    private const int CtxCompassHeight = 54;
    private const int CtxCompassRotation = 56;
    private const int CtxPlayerX = 60;
    private const int CtxPlayerY = 64;
    private const int CtxMapCenterX = 68;
    private const int CtxMapCenterY = 72;
    private const int CtxMapScale = 76;
    private const int CtxProcessId = 80;
    private const int CtxMountIndex = 84;

    private MemoryMappedFile? _mmf;
    private MemoryMappedViewAccessor? _accessor;
    private readonly byte[] _buffer = new byte[TotalSize];

    /// <summary>
    /// Attempts to open the MumbleLink shared memory segment. Returns false if GW2 (or another
    /// Mumble-linked game) isn't running.
    /// </summary>
    public bool TryConnect(string mapName = "MumbleLink")
    {
        if (_mmf != null) return true;

        try
        {
            _mmf = MemoryMappedFile.OpenExisting(mapName, MemoryMappedFileRights.Read);
            _accessor = _mmf.CreateViewAccessor(0, TotalSize, MemoryMappedFileAccess.Read);
            return true;
        }
        catch (Exception)
        {
            // Not found yet, or not on Windows. Caller can retry on the next poll.
            _mmf?.Dispose();
            _mmf = null;
            _accessor = null;
            return false;
        }
    }

    public bool IsConnected => _accessor != null;

    /// <summary>
    /// Reads the current contents of the shared memory segment and parses them.
    /// Call TryConnect() first (or let this call it lazily).
    /// </summary>
    public MumbleLinkSnapshot? Read()
    {
        if (_accessor == null && !TryConnect())
            return null;

        _accessor!.ReadArray(0, _buffer, 0, TotalSize);

        uint uiTick = BitConverter.ToUInt32(_buffer, OffsetUiTick);
        if (uiTick == 0)
        {
            // Segment exists but the game hasn't written a frame yet (e.g. GW2 not fully loaded).
            return new MumbleLinkSnapshot { UiVersion = BitConverter.ToUInt32(_buffer, OffsetUiVersion), UiTick = 0 };
        }

        var identityRaw = ReadWideString(OffsetIdentity, 256);
        Gw2Identity? identity = null;
        if (!string.IsNullOrWhiteSpace(identityRaw))
        {
            try { identity = JsonSerializer.Deserialize<Gw2Identity>(identityRaw); }
            catch (Exception)
            {
                // Game may write a partial/malformed string mid-frame, or (if GW2 ever changes
                // its MumbleLink JSON format) something other than JsonException could surface
                // here too. Deliberately caught broadly and scoped to ONLY this block: skipping
                // identity for this one tick still lets position/context below parse normally,
                // rather than the whole Read() call aborting and discarding data that WAS read
                // successfully this tick.
            }
        }

        uint contextLen = BitConverter.ToUInt32(_buffer, OffsetContextLen);
        Gw2Context? context = null;
        if (contextLen >= CtxMountIndex + 1)
        {
            context = new Gw2Context
            {
                MapId = BitConverter.ToUInt32(_buffer, OffsetContext + CtxMapId),
                MapType = BitConverter.ToUInt32(_buffer, OffsetContext + CtxMapType),
                ShardId = BitConverter.ToUInt32(_buffer, OffsetContext + CtxShardId),
                Instance = BitConverter.ToUInt32(_buffer, OffsetContext + CtxInstance),
                BuildId = BitConverter.ToUInt32(_buffer, OffsetContext + CtxBuildId),
                UiState = BitConverter.ToUInt32(_buffer, OffsetContext + CtxUiState),
                CompassWidth = BitConverter.ToUInt16(_buffer, OffsetContext + CtxCompassWidth),
                CompassHeight = BitConverter.ToUInt16(_buffer, OffsetContext + CtxCompassHeight),
                CompassRotation = BitConverter.ToSingle(_buffer, OffsetContext + CtxCompassRotation),
                PlayerX = BitConverter.ToSingle(_buffer, OffsetContext + CtxPlayerX),
                PlayerY = BitConverter.ToSingle(_buffer, OffsetContext + CtxPlayerY),
                MapCenterX = BitConverter.ToSingle(_buffer, OffsetContext + CtxMapCenterX),
                MapCenterY = BitConverter.ToSingle(_buffer, OffsetContext + CtxMapCenterY),
                MapScale = BitConverter.ToSingle(_buffer, OffsetContext + CtxMapScale),
                ProcessId = BitConverter.ToUInt32(_buffer, OffsetContext + CtxProcessId),
                MountIndex = _buffer[OffsetContext + CtxMountIndex],
            };
        }

        return new MumbleLinkSnapshot
        {
            UiVersion = BitConverter.ToUInt32(_buffer, OffsetUiVersion),
            UiTick = uiTick,
            AvatarPosition = ReadVector3(OffsetAvatarPosition),
            AvatarFront = ReadVector3(OffsetAvatarFront),
            AvatarTop = ReadVector3(OffsetAvatarTop),
            Name = ReadWideString(OffsetName, 256),
            CameraPosition = ReadVector3(OffsetCameraPosition),
            CameraFront = ReadVector3(OffsetCameraFront),
            CameraTop = ReadVector3(OffsetCameraTop),
            Identity = identity,
            Context = context,
            Description = ReadWideString(OffsetDescription, 2048),
        };
    }

    private Vector3 ReadVector3(int offset) => new(
        BitConverter.ToSingle(_buffer, offset),
        BitConverter.ToSingle(_buffer, offset + 4),
        BitConverter.ToSingle(_buffer, offset + 8));

    private string ReadWideString(int offset, int maxChars)
    {
        // UTF-16, null-terminated within a fixed-size buffer.
        int byteLen = maxChars * 2;
        int nullIndex = -1;
        for (int i = 0; i < byteLen - 1; i += 2)
        {
            if (_buffer[offset + i] == 0 && _buffer[offset + i + 1] == 0)
            {
                nullIndex = i;
                break;
            }
        }
        int len = nullIndex >= 0 ? nullIndex : byteLen;
        return Encoding.Unicode.GetString(_buffer, offset, len);
    }

    public void Dispose()
    {
        _accessor?.Dispose();
        _mmf?.Dispose();
    }
}
