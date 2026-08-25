using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GW2WikiTool;

public sealed class Gw2Map
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("min_level")] public int MinLvl { get; set; }
    [JsonPropertyName("max_level")] public int MaxLvl { get; set; }
    [JsonPropertyName("region_name")] public string? Region { get; set; }
    [JsonPropertyName("continent_name")] public string? Cont { get; set; }
    [JsonPropertyName("continent_id")] public int ContId { get; set; }
    [JsonPropertyName("default_floor")] public int DefFloor { get; set; }
    [JsonPropertyName("map_rect")] public double[][]? MapRect { get; set; }
    [JsonPropertyName("continent_rect")] public double[][]? ContRect { get; set; }
}

public sealed class PointOfInterest
{
    public int Id { get; init; }
    public string? Name { get; init; }
    public string Type { get; init; } = "";
    public int Floor { get; init; }
    public double[] Coord { get; init; } = Array.Empty<double>();
    public string? ChatLink { get; init; }
}

public sealed class ContinentFloor
{
    [JsonPropertyName("regions")] public Dictionary<string, FloorRegion> Regions { get; set; } = new();
}

public sealed class FloorRegion
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("maps")] public Dictionary<string, FloorMap> Maps { get; set; } = new();
}

public sealed class FloorMap
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("points_of_interest")] public Dictionary<string, FloorPoi> Pois { get; set; } = new();
}

public sealed class FloorPoi
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string? Name { get; set; }
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("floor")] public int Floor { get; set; }
    [JsonPropertyName("coord")] public double[] Coord { get; set; } = Array.Empty<double>();
    [JsonPropertyName("chat_link")] public string? ChatLink { get; set; }
}

public sealed class Achievement
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("description")] public string? Desc { get; set; }
    [JsonPropertyName("requirement")] public string? Req { get; set; }
    [JsonPropertyName("locked_text")] public string? LockedText { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("flags")] public List<string> Flags { get; set; } = new();
    [JsonPropertyName("tiers")] public List<AchievementTier> Tiers { get; set; } = new();
    [JsonPropertyName("point_cap")] public int? PointCap { get; set; }
}

public sealed class AchievementTier
{
    [JsonPropertyName("count")] public int Count { get; set; }
    [JsonPropertyName("points")] public int Points { get; set; }
}

public sealed class Gw2World
{
    [JsonPropertyName("id")] public long Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("population")] public string Pop { get; set; } = "";
}

public sealed class Gw2Account
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("world")] public long World { get; set; }
    [JsonPropertyName("guilds")] public List<string> Guilds { get; set; } = new();
    [JsonPropertyName("age")] public long AgeSecs { get; set; }
    [JsonPropertyName("created")] public string Created { get; set; } = "";
}

public sealed class Gw2Character
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("race")] public string Race { get; set; } = "";
    [JsonPropertyName("profession")] public string Prof { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("age")] public long AgeSecs { get; set; }
    [JsonPropertyName("last_modified")] public string? LastMod { get; set; }
}

public sealed class Gw2Build
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

public sealed class Gw2ApiClient : IDisposable
{
    private const string BaseUrl = "https://api.guildwars2.com/v2/";
    private readonly HttpClient _http;

    public Gw2ApiClient(string? apiKey = null)
    {
        _http = new HttpClient { BaseAddress = new Uri(BaseUrl) };
        if (!string.IsNullOrWhiteSpace(apiKey))
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    private async Task<T?> Fetch<T>(string path, CancellationToken ct = default)
    {
        using var resp = await _http.GetAsync(path, ct);
        resp.EnsureSuccessStatusCode();
        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct);
    }

    public async Task<long> GetBuild(CancellationToken ct = default)
    {
        var b = await Fetch<Gw2Build>("build", ct);
        return b?.Id ?? 0;
    }

    public Task<Gw2Map?> GetMap(int mapId, CancellationToken ct = default) =>
        Fetch<Gw2Map>($"maps/{mapId}", ct);

    public Task<Gw2World?> GetWorld(long worldId, CancellationToken ct = default) =>
        Fetch<Gw2World>($"worlds/{worldId}", ct);

    public Task<Gw2Account?> GetAcct(CancellationToken ct = default) =>
        Fetch<Gw2Account>("account", ct);

    public Task<List<string>?> GetCharNames(CancellationToken ct = default) =>
        Fetch<List<string>>("characters", ct);

    public Task<Gw2Character?> GetChar(string name, CancellationToken ct = default) =>
        Fetch<Gw2Character>($"characters/{Uri.EscapeDataString(name)}", ct);

    public async Task<List<int>> GetAllAchIds(CancellationToken ct = default) =>
        await Fetch<List<int>>("achievements", ct) ?? new List<int>();

    public Task<Achievement?> GetAch(int id, CancellationToken ct = default) =>
        Fetch<Achievement>($"achievements/{id}", ct);

    public async Task<List<Achievement>> GetAchs(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids as IReadOnlyCollection<int> ?? ids.ToList();
        if (idList.Count == 0) return new List<Achievement>();
        var idsParam = string.Join(',', idList);
        return await Fetch<List<Achievement>>($"achievements?ids={idsParam}", ct) ?? new List<Achievement>();
    }

    public async Task<List<int>> GetAllMapIds(CancellationToken ct = default) =>
        await Fetch<List<int>>("maps", ct) ?? new List<int>();

    public async Task<List<Gw2Map>> GetMaps(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids as IReadOnlyCollection<int> ?? ids.ToList();
        if (idList.Count == 0) return new List<Gw2Map>();
        var idsParam = string.Join(',', idList);
        return await Fetch<List<Gw2Map>>($"maps?ids={idsParam}", ct) ?? new List<Gw2Map>();
    }

    public Task<ContinentFloor?> GetFloor(int contId, int floor, CancellationToken ct = default) =>
        Fetch<ContinentFloor>($"continents/{contId}/floors/{floor}", ct);

    public async Task<List<PointOfInterest>> GetPois(Gw2Map map, CancellationToken ct = default)
    {
        if (map.ContId <= 0) return new List<PointOfInterest>();

        var floor = await GetFloor(map.ContId, map.DefFloor, ct);
        if (floor == null) return new List<PointOfInterest>();

        var mapIdKey = map.Id.ToString();
        foreach (var region in floor.Regions.Values)
        {
            if (!region.Maps.TryGetValue(mapIdKey, out var fm)) continue;

            var result = new List<PointOfInterest>();
            foreach (var p in fm.Pois.Values)
            {
                result.Add(new PointOfInterest
                {
                    Id = p.Id,
                    Name = p.Name,
                    Type = p.Type,
                    Floor = p.Floor,
                    Coord = p.Coord,
                    ChatLink = p.ChatLink,
                });
            }
            return result;
        }

        return new List<PointOfInterest>();
    }

    public void Dispose() => _http.Dispose();
}