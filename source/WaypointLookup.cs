using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GW2WikiTool;

public sealed record WpEntry(int Id, string Name, string ChatLink, int MapId, string MapName);

public sealed class WaypointLookup
{
    private const int ChunkSz = 200;
    private const int SchemaVer = 4;

    private readonly Gw2ApiClient _api;
    private readonly string _cachePath;
    private readonly object _loadLock = new();
    private List<WpEntry>? _index;
    private Task? _loadTask;

    public string? LastCacheErr { get; private set; }

    public WaypointLookup(Gw2ApiClient api, string? cachePath = null)
    {
        _api = api;
        _cachePath = cachePath ?? Path.Combine(AppContext.BaseDirectory, "waypoints_index.json");
    }

    public bool IsLoaded => _index != null;

    public Task EnsureLoaded(bool forceRefresh = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        if (_index != null && !forceRefresh) return Task.CompletedTask;

        lock (_loadLock)
        {
            if (_index != null && !forceRefresh) return Task.CompletedTask;
            if (!forceRefresh && _loadTask is { IsCompleted: false }) return _loadTask;

            var task = LoadAsync(forceRefresh, progress, ct);
            _loadTask = task;
            return task;
        }
    }

    private async Task LoadAsync(bool forceRefresh, IProgress<string>? progress, CancellationToken ct)
    {
        if (_index != null && !forceRefresh) return;

        if (!forceRefresh && File.Exists(_cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<CacheFile>(await File.ReadAllTextAsync(_cachePath, ct));
                if (cached != null && cached.SchemaVer == SchemaVer && cached.Entries.Count > 0)
                {
                    _index = cached.Entries;
                    LastCacheErr = null;
                    return;
                }
            }
            catch
            {
                // rebuild
            }
        }

        progress?.Report("Fetching map list...");
        var mapIds = await _api.GetAllMapIds(ct);

        var maps = new List<Gw2Map>(mapIds.Count);
        for (int i = 0; i < mapIds.Count; i += ChunkSz)
        {
            var chunk = mapIds.Skip(i).Take(ChunkSz).ToList();
            progress?.Report($"Fetching maps {i + 1}-{Math.Min(i + ChunkSz, mapIds.Count)} of {mapIds.Count}...");
            var batch = await _api.GetMaps(chunk, ct);

            var foundIds = batch.Select(m => m.Id).ToHashSet();
            var missing = chunk.Where(id => !foundIds.Contains(id)).ToList();
            foreach (var id in missing)
            {
                var single = await _api.GetMap(id, ct);
                if (single != null) batch.Add(single);
            }

            maps.AddRange(batch);
        }

        var floorKeys = maps
            .Where(m => m.ContId > 0)
            .Select(m => (m.ContId, m.DefFloor))
            .Distinct()
            .ToList();

        var index = new List<WpEntry>();
        var skipped = new List<(int ContId, int Floor)>();
        int done = 0;
        foreach (var (contId, floorId) in floorKeys)
        {
            done++;
            progress?.Report($"Fetching floor data {done} of {floorKeys.Count}...");

            ContinentFloor? floor;
            try
            {
                floor = await _api.GetFloor(contId, floorId, ct);
            }
            catch (Exception) when (!ct.IsCancellationRequested)
            {
                skipped.Add((contId, floorId));
                continue;
            }
            if (floor == null) continue;

            foreach (var region in floor.Regions.Values)
            {
                foreach (var fm in region.Maps.Values)
                {
                    foreach (var poi in fm.Pois.Values)
                    {
                        if (poi.Type == "waypoint" && !string.IsNullOrWhiteSpace(poi.Name) && poi.ChatLink != null)
                            index.Add(new WpEntry(poi.Id, poi.Name!, poi.ChatLink, fm.Id, fm.Name));
                    }
                }
            }
        }

        _index = index;
        if (skipped.Count > 0)
            progress?.Report($"Note: {skipped.Count} floor(s) had no data and were skipped.");

        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(new CacheFile(SchemaVer, _index)), ct);
            LastCacheErr = null;
        }
        catch (Exception ex)
        {
            LastCacheErr = ex.Message;
        }
    }

    public IReadOnlyList<WpEntry> Search(string query, int maxResults = 20)
    {
        if (_index == null)
            throw new InvalidOperationException("Call EnsureLoaded() before Search().");

        var nq = FuzzySearch.FixQuotes(query);
        var exact = _index
            .Where(e => FuzzySearch.FixQuotes(e.Name).Contains(nq, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
        if (exact.Count > 0) return exact;

        return _index
            .Where(e => FuzzySearch.LooseMatch(e.Name, query))
            .Take(maxResults)
            .ToList();
    }

    private sealed record CacheFile(int SchemaVer, List<WpEntry> Entries);
}