using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace GW2WikiTool;

// Every model property below has an explicit [JsonPropertyName] mapping it to the API's
// snake_case JSON key -- deliberate, not incidental. If you add a new property, it MUST get
// its own [JsonPropertyName] too, or System.Text.Json will silently leave it at its default
// value rather than throwing (unmatched JSON properties are ignored by default).
// Note that JsonSerializerOptions.PropertyNameCaseInsensitive would NOT fix a forgotten attribute
// here the way it might for a camelCase/PascalCase-only API: GW2's keys are snake_case
// ("min_level", "chat_link"), which differs from a C# property name by more than casing, so
// case-insensitive matching wouldn't bridge the gap. The explicit-attribute-on-every-property
// discipline already in use is the actual safeguard -- keep using it.

public sealed class Gw2Map
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("min_level")] public int MinLevel { get; set; }
    [JsonPropertyName("max_level")] public int MaxLevel { get; set; }
    [JsonPropertyName("region_name")] public string? RegionName { get; set; }
    [JsonPropertyName("continent_name")] public string? ContinentName { get; set; }

    /// <summary>Which continent this map belongs to (1 = Tyria, 2 = The Mists) — needed to look
    /// up its points of interest via <see cref="Gw2ApiClient.GetContinentFloorAsync"/>, since
    /// /v2/maps itself does NOT return points_of_interest despite how similar its schema looks.</summary>
    [JsonPropertyName("continent_id")] public int ContinentId { get; set; }

    /// <summary>The floor to request from the continents/floors endpoint for this map's data.
    /// Occasionally wrong for a handful of maps (a documented API quirk, e.g. some Crystal Desert
    /// maps only have real data on floor 49 regardless of what this says) — a known, accepted gap.</summary>
    [JsonPropertyName("default_floor")] public int DefaultFloor { get; set; }

    /// <summary>Map-local coordinate bounds: [[SW.x, SW.y], [NE.x, NE.y]]. Used with
    /// <see cref="ContinentRect"/> to convert a world/avatar position into continent
    /// ("wiki map") coordinates — see <see cref="Gw2Coordinates"/>.</summary>
    [JsonPropertyName("map_rect")] public double[][]? MapRect { get; set; }

    /// <summary>This map's bounds within the shared continent coordinate system:
    /// [[NW.x, NW.y], [SE.x, SE.y]].</summary>
    [JsonPropertyName("continent_rect")] public double[][]? ContinentRect { get; set; }
}

/// <summary>
/// A waypoint, landmark, or vista — normalized from the continents/floors endpoint's response
/// (see <see cref="FloorPoi"/>) into a flat, easy-to-use shape.
/// </summary>
public sealed class PointOfInterest
{
    public int Id { get; init; }
    public string? Name { get; init; }

    /// <summary>"waypoint", "landmark" (a.k.a. point of interest), "vista", or "unlock".</summary>
    public string Type { get; init; } = "";
    public int Floor { get; init; }

    /// <summary>[x, y] in continent coordinates — directly comparable to
    /// Gw2Context.PlayerX/PlayerY and to Gw2Coordinates conversion output.</summary>
    public double[] Coord { get; init; } = Array.Empty<double>();

    /// <summary>Ready-to-use "[&...]" chat code, as returned by the API.</summary>
    public string? ChatLink { get; init; }
}

/// <summary>
/// Response shape of /v2/continents/{continentId}/floors/{floor} — the ONLY endpoint that
/// actually returns points_of_interest (waypoints, landmarks, vistas), despite /v2/maps having
/// a deceptively similar-looking schema without it. Reference:
/// https://wiki.guildwars2.com/wiki/API:2/continents
/// </summary>
public sealed class ContinentFloor
{
    [JsonPropertyName("regions")] public Dictionary<string, FloorRegion> Regions { get; set; } = new();
}

public sealed class FloorRegion
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Keyed by map id (as a string) — matches Gw2Map.Id.ToString().</summary>
    [JsonPropertyName("maps")] public Dictionary<string, FloorMap> Maps { get; set; } = new();
}

public sealed class FloorMap
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";

    /// <summary>Keyed by POI id (as a string).</summary>
    [JsonPropertyName("points_of_interest")] public Dictionary<string, FloorPoi> PointsOfInterest { get; set; } = new();
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
    [JsonPropertyName("description")] public string? Description { get; set; }
    [JsonPropertyName("requirement")] public string? Requirement { get; set; }
    [JsonPropertyName("locked_text")] public string? LockedText { get; set; }
    [JsonPropertyName("type")] public string? Type { get; set; }
    [JsonPropertyName("flags")] public List<string> Flags { get; set; } = new();
    [JsonPropertyName("tiers")] public List<AchievementTier> Tiers { get; set; } = new();
    [JsonPropertyName("point_cap")] public int? PointCap { get; set; }
}

/// <summary>An achievement group (e.g. "Historical"), made up of categories.</summary>
public sealed class AchievementGroup
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("order")] public int Order { get; set; }
    [JsonPropertyName("categories")] public List<int> Categories { get; set; } = new();
}

/// <summary>An achievement category and the achievements it contains.</summary>
public sealed class AchievementCategory
{
    [JsonPropertyName("id")] public int Id { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("order")] public int Order { get; set; }

    /// <summary>Plain IDs, or objects with an "id" in newer API schemas. Read via <see cref="GetAchievementIds"/>.</summary>
    [JsonPropertyName("achievements")] public List<JsonElement> Achievements { get; set; } = new();

    public IEnumerable<int> GetAchievementIds()
    {
        foreach (var el in Achievements)
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var plain))
                yield return plain;
            else if (el.ValueKind == JsonValueKind.Object
                     && el.TryGetProperty("id", out var idProp)
                     && idProp.ValueKind == JsonValueKind.Number
                     && idProp.TryGetInt32(out var nested))
                yield return nested;
        }
    }
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
    [JsonPropertyName("population")] public string Population { get; set; } = "";
}

public sealed class Gw2Account
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("world")] public long World { get; set; }
    [JsonPropertyName("guilds")] public List<string> Guilds { get; set; } = new();
    [JsonPropertyName("age")] public long AgeSeconds { get; set; }
    [JsonPropertyName("created")] public string Created { get; set; } = "";
}

public sealed class Gw2Character
{
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("race")] public string Race { get; set; } = "";
    [JsonPropertyName("profession")] public string Profession { get; set; } = "";
    [JsonPropertyName("level")] public int Level { get; set; }
    [JsonPropertyName("age")] public long AgeSeconds { get; set; }
    [JsonPropertyName("last_modified")] public string? LastModified { get; set; }
}

public sealed class Gw2Build
{
    [JsonPropertyName("id")] public long Id { get; set; }
}

/// <summary>
/// Thin wrapper over the Guild Wars 2 public API (https://api.guildwars2.com/v2).
/// An API key is only required for account-scoped endpoints (account, characters, wallet, etc).
/// Generate one at https://account.arena.net/applications
/// </summary>
public sealed class Gw2ApiClient : IDisposable
{
    private const string BaseUrl = "https://api.guildwars2.com/v2/";
    private readonly HttpClient _http;

    /// <param name="handler">Optional HTTP message handler, e.g. a fake API for tests.</param>
    public Gw2ApiClient(string? apiKey = null, HttpMessageHandler? handler = null)
    {
        handler ??= new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        _http = new HttpClient(handler) { BaseAddress = new Uri(BaseUrl) };
        if (!string.IsNullOrWhiteSpace(apiKey))
        {
            _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        }
    }

    /// <summary>GETs and deserializes JSON. Resumes on a thread-pool thread so parsing doesn't run on the caller's (UI) thread.</summary>
    private async Task<T?> GetAsync<T>(string path, CancellationToken ct = default)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonSerializer.DeserializeAsync<T>(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>Current build id — compare against MumbleLink's Context.BuildId to check for a game update.</summary>
    /// <summary>The current live game build id, or null if the API didn't return one.</summary>
    public async Task<long?> GetCurrentBuildIdAsync(CancellationToken ct = default)
    {
        var build = await GetAsync<Gw2Build>("build", ct).ConfigureAwait(false);
        return build?.Id;
    }

    public Task<Gw2Map?> GetMapAsync(int mapId, CancellationToken ct = default) =>
        GetAsync<Gw2Map>($"maps/{mapId}", ct);

    public Task<Gw2World?> GetWorldAsync(long worldId, CancellationToken ct = default) =>
        GetAsync<Gw2World>($"worlds/{worldId}", ct);

    /// <summary>Requires an API key with the "account" scope.</summary>
    public Task<Gw2Account?> GetAccountAsync(CancellationToken ct = default) =>
        GetAsync<Gw2Account>("account", ct);

    /// <summary>Requires an API key with the "characters" scope.</summary>
    public Task<List<string>?> GetCharacterNamesAsync(CancellationToken ct = default) =>
        GetAsync<List<string>>("characters", ct);

    /// <summary>Requires an API key with the "characters" scope.</summary>
    public Task<Gw2Character?> GetCharacterAsync(string name, CancellationToken ct = default) =>
        GetAsync<Gw2Character>($"characters/{Uri.EscapeDataString(name)}", ct);

    /// <summary>Every achievement id in the game (several thousand). No API key required.</summary>
    public async Task<List<int>> GetAllAchievementIdsAsync(CancellationToken ct = default) =>
        await GetAsync<List<int>>("achievements", ct).ConfigureAwait(false) ?? new List<int>();

    /// <summary>Full details for a single achievement by id.</summary>
    public Task<Achievement?> GetAchievementAsync(int id, CancellationToken ct = default) =>
        GetAsync<Achievement>($"achievements/{id}", ct);

    /// <summary>
    /// Bulk achievement lookup. The GW2 API caps bulk "ids" requests at 200 — pass 200 or fewer
    /// ids per call (see AchievementLookup for a helper that chunks + caches the full list).
    /// </summary>
    public async Task<List<Achievement>> GetAchievementsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids as IReadOnlyCollection<int> ?? ids.ToList();
        if (idList.Count == 0) return new List<Achievement>();
        var idsParam = string.Join(',', idList);
        return await GetAsync<List<Achievement>>($"achievements?ids={idsParam}", ct).ConfigureAwait(false) ?? new List<Achievement>();
    }

    /// <summary>Every achievement group. No API key required.</summary>
    public async Task<List<AchievementGroup>> GetAchievementGroupsAsync(CancellationToken ct = default) =>
        await GetAsync<List<AchievementGroup>>("achievements/groups?ids=all", ct).ConfigureAwait(false) ?? new List<AchievementGroup>();

    /// <summary>Every achievement category. No API key required.</summary>
    public async Task<List<AchievementCategory>> GetAchievementCategoriesAsync(CancellationToken ct = default) =>
        await GetAsync<List<AchievementCategory>>("achievements/categories?ids=all", ct).ConfigureAwait(false) ?? new List<AchievementCategory>();

    /// <summary>Every map id in the game. No API key required.</summary>
    public async Task<List<int>> GetAllMapIdsAsync(CancellationToken ct = default) =>
        await GetAsync<List<int>>("maps", ct).ConfigureAwait(false) ?? new List<int>();

    /// <summary>Bulk map lookup, with map_rect/continent_rect for coordinate conversion
    /// (max 200 ids per call). Does NOT include points_of_interest.
    /// Use <see cref="GetContinentFloorAsync"/> or <see cref="GetMapPointsOfInterestAsync"/> for that.</summary>
    public async Task<List<Gw2Map>> GetMapsAsync(IEnumerable<int> ids, CancellationToken ct = default)
    {
        var idList = ids as IReadOnlyCollection<int> ?? ids.ToList();
        if (idList.Count == 0) return new List<Gw2Map>();
        var idsParam = string.Join(',', idList);
        return await GetAsync<List<Gw2Map>>($"maps?ids={idsParam}", ct).ConfigureAwait(false) ?? new List<Gw2Map>();
    }

    /// <summary>
    /// /v2/maps does NOT return points_of_interest despite its similar-looking schema. See
    /// https://wiki.guildwars2.com/wiki/API:2/continents
    /// </summary>
    public Task<ContinentFloor?> GetContinentFloorAsync(int continentId, int floor, CancellationToken ct = default) =>
        GetAsync<ContinentFloor>($"continents/{continentId}/floors/{floor}", ct);

    /// <summary>
    /// Convenience wrapper: fetches the given map's floor data and returns just its own
    /// points of interest, normalized to the flat <see cref="PointOfInterest"/> shape.
    /// Returns an empty list if the map has no continent placement or isn't found on its floor.
    /// </summary>
    public async Task<List<PointOfInterest>> GetMapPointsOfInterestAsync(Gw2Map map, CancellationToken ct = default)
    {
        if (map.ContinentId <= 0) return new List<PointOfInterest>();

        var floor = await GetContinentFloorAsync(map.ContinentId, map.DefaultFloor, ct).ConfigureAwait(false);
        if (floor == null) return new List<PointOfInterest>();

        var mapIdKey = map.Id.ToString();
        foreach (var region in floor.Regions.Values)
        {
            if (!region.Maps.TryGetValue(mapIdKey, out var floorMap)) continue;

            return floorMap.PointsOfInterest.Values
                .Select(p => new PointOfInterest
                {
                    Id = p.Id,
                    Name = p.Name,
                    Type = p.Type,
                    Floor = p.Floor,
                    Coord = p.Coord,
                    ChatLink = p.ChatLink,
                })
                .ToList();
        }

        return new List<PointOfInterest>();
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>
/// Support for the resumable index builds: retry with backoff, failure classification and
/// checkpoint file helpers.
/// </summary>
internal static class IndexBuildSupport
{
    private const int MaxAttempts = 3;

    /// <summary>Delay before the first retry; doubles on each further retry.</summary>
    internal static TimeSpan RetryBaseDelay = TimeSpan.FromSeconds(2);

    /// <summary>Age after which a checkpoint is discarded instead of resumed.</summary>
    internal static readonly TimeSpan PartialMaxAge = TimeSpan.FromHours(24);

    /// <summary>Runs an API call, retrying transient failures with exponential backoff.</summary>
    public static async Task<T> WithRetryAsync<T>(Func<Task<T>> action, string what, IProgress<string>? progress, CancellationToken ct)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await action().ConfigureAwait(false);
            }
            catch (Exception ex) when (attempt < MaxAttempts && IsTransient(ex) && !ct.IsCancellationRequested)
            {
                var delay = TimeSpan.FromTicks(RetryBaseDelay.Ticks * (1L << (attempt - 1)));
                progress?.Report($"{what} failed; retrying in {delay.TotalSeconds:0.#}s (attempt {attempt + 1} of {MaxAttempts})...");
                await Task.Delay(delay, ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>True for failures worth retrying: no response, timeouts, 408, 429 and 5xx.</summary>
    public static bool IsTransient(Exception ex)
    {
        if (ex is HttpRequestException http)
        {
            if (http.StatusCode is null) return true;
            var code = (int)http.StatusCode.Value;
            return code == 408 || code == 429 || code >= 500;
        }
        return ex is TaskCanceledException or TimeoutException or IOException;
    }

    /// <summary>True for failures a retry cannot fix: 4xx responses (except 408/429) and malformed JSON.</summary>
    public static bool IsPermanentFailure(Exception ex)
    {
        if (ex is HttpRequestException http && http.StatusCode is { } status)
        {
            var code = (int)status;
            return code >= 400 && code < 500 && code != 408 && code != 429;
        }
        return ex is JsonException;
    }

    /// <summary>Reads a JSON file, returning null if it is missing or invalid.</summary>
    public static T? TryReadJson<T>(string path) where T : class
    {
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<T>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Writes a JSON file via a temp file so an interruption cannot leave a partial file. Errors are ignored.</summary>
    public static void TryWriteJson<T>(string path, T value)
    {
        try
        {
            var dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            var tmp = path + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(value));
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            // Checkpoints are best effort.
        }
    }

    public static void TryDelete(string path)
    {
        try { File.Delete(path); } catch { /* ignored */ }
    }
}
