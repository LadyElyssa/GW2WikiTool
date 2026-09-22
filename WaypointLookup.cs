using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GW2WikiTool;

public sealed record WaypointIndexEntry(int Id, string Name, string ChatLink, int MapId, string MapName, double[] Coord);

/// <summary>
/// Game-wide waypoint search. Waypoints only exist on the continent floor endpoints, so the
/// index, including each waypoint's position, is built from every map's floor data and cached.
/// to disk.
/// </summary>
public sealed class WaypointLookup
{
    private const int ChunkSize = 200;       // API limit for bulk id lookups
    private const int CheckpointEvery = 10;  // floors between checkpoint writes
    private const int FloorConcurrency = 4;  // simultaneous floor downloads

    // Bump when the indexing rules change so older caches are rebuilt.
    private const int SchemaVersion = 5;

    private readonly Gw2ApiClient _api;
    private readonly string _cachePath;
    private readonly string _checkpointPath;
    private readonly object _loadLock = new();
    private List<WaypointIndexEntry>? _index;
    private Task? _loadTask;

    /// <summary>A warning about the last load (e.g. it could not be saved), or null. The index is
    /// still usable in memory when this is set.</summary>
    public string? LastCacheError { get; private set; }

    /// <summary>The game build the loaded index was built or last confirmed against, or null if unknown.</summary>
    public long? CachedBuildId { get; private set; }

    public WaypointLookup(Gw2ApiClient api, string? cachePath = null)
    {
        _api = api;
        _cachePath = cachePath ?? Path.Combine(AppContext.BaseDirectory, "waypoints_index.json");
        _checkpointPath = _cachePath + ".partial";
    }

    public bool IsIndexLoaded => _index != null;

    /// <summary>
    /// Loads the index from the disk cache, or builds it from the API. Concurrent callers share
    /// one build. A build that fails part-way resumes from its checkpoint on the next call,
    /// including a forced refresh.
    /// </summary>
    public Task EnsureIndexLoadedAsync(bool forceRefresh = false, IProgress<string>? progress = null, CancellationToken ct = default)
    {
        // A set LastCacheError means the previous load didn't fully finish (e.g. the save
        // failed), so even with an in-memory index and no forced refresh, it still needs to run.
        bool alreadyDone() => _index != null && LastCacheError == null;

        if (alreadyDone() && !forceRefresh) return Task.CompletedTask;

        lock (_loadLock)
        {
            if (alreadyDone() && !forceRefresh) return Task.CompletedTask;

            if (!forceRefresh && _loadTask is { IsCompleted: false })
                return _loadTask;

            var task = LoadAsync(forceRefresh, progress, ct);
            _loadTask = task;
            return task;
        }
    }

    private async Task LoadAsync(bool forceRefresh, IProgress<string>? progress, CancellationToken ct)
    {
        if (forceRefresh)
        {
            // A confirmed build change: don't resume a download that may be against the old
            // build's data.
            _index = null;
            LastCacheError = null;
            IndexBuildSupport.TryDelete(_checkpointPath);
        }

        if (_index != null)
        {
            if (LastCacheError == null) return; // already loaded and fully persisted

            // Built but not saved; retry only the save, not the waypoint download.
            await PersistAsync(_index, ct).ConfigureAwait(false);
            return;
        }

        if (!forceRefresh && File.Exists(_cachePath))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<CacheFile>(await File.ReadAllTextAsync(_cachePath, ct).ConfigureAwait(false));
                if (cached != null && cached.SchemaVersion == SchemaVersion && cached.Entries.Count > 0)
                {
                    _index = cached.Entries;
                    CachedBuildId = cached.BuildId;
                    if (CachedBuildId == null) await StampBuildIdAsync(ct).ConfigureAwait(false);
                    return;
                }
            }
            catch
            {
                // Unreadable or outdated cache: rebuild from the API.
            }
        }

        LastCacheError = null;
        _index = await BuildIndexAsync(progress, ct).ConfigureAwait(false);
        await PersistAsync(_index, ct).ConfigureAwait(false);
    }

    /// <summary>Writes the cache, recording the current build id. Safe to call again on an
    /// already-cached index that failed to save.</summary>
    private async Task PersistAsync(List<WaypointIndexEntry> entries, CancellationToken ct)
    {
        long? buildId = null;
        try
        {
            buildId = await _api.GetCurrentBuildIdAsync(ct).ConfigureAwait(false);
        }
        catch
        {
            // Build id is supplementary; save the index without it rather than losing the download.
        }
        CachedBuildId = buildId;

        try
        {
            var dir = Path.GetDirectoryName(_cachePath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(new CacheFile(SchemaVersion, buildId, entries)), ct).ConfigureAwait(false);
            IndexBuildSupport.TryDelete(_checkpointPath);
            LastCacheError = null;
        }
        catch (Exception ex)
        {
            LastCacheError = $"couldn't save to disk ({ex.Message})";
        }
    }

    /// <summary>Fills in a build id on a cache written before this field existed, without
    /// re-downloading. Best-effort: a failure here (e.g. offline) just leaves it unknown, to be
    /// retried on a later load.</summary>
    private async Task StampBuildIdAsync(CancellationToken ct)
    {
        try
        {
            CachedBuildId = await _api.GetCurrentBuildIdAsync(ct).ConfigureAwait(false);
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(new CacheFile(SchemaVersion, CachedBuildId, _index!)), ct).ConfigureAwait(false);
        }
        catch
        {
            // Left unknown; tried again on the next load.
        }
    }

    /// <summary>
    /// Builds the index in two phases: the map list (to learn each map's continent and default
    /// floor), then one request per distinct (continent, floor) pair. Progress is checkpointed
    /// so an interrupted build resumes where it stopped.
    /// </summary>
    private async Task<List<WaypointIndexEntry>> BuildIndexAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var state = LoadCheckpoint();
        if (state != null)
        {
            progress?.Report("Waypoints: resuming...");
        }
        else
        {
            progress?.Report("Waypoints: fetching map list...");
            state = new BuildState
            {
                SchemaVersion = SchemaVersion,
                StartedUtc = DateTime.UtcNow,
                MapIds = await IndexBuildSupport.WithRetryAsync(() => _api.GetAllMapIdsAsync(ct), "Waypoints: map list", progress, ct).ConfigureAwait(false),
            };
        }

        await DownloadMapsAsync(state, progress, ct).ConfigureAwait(false);
        await DownloadFloorsAsync(state, progress, ct).ConfigureAwait(false);

        if (state.Skipped.Count > 0)
            progress?.Report($"Waypoints: {state.Skipped.Count} floor(s) had no data and were skipped.");

        return state.Entries;
    }

    /// <summary>Collects the distinct (continent, floor) pairs the maps sit on.</summary>
    private async Task DownloadMapsAsync(BuildState state, IProgress<string>? progress, CancellationToken ct)
    {
        var knownKeys = state.FloorKeys.ToHashSet();
        for (int i = state.MapsDone; i < state.MapIds.Count; i += ChunkSize)
        {
            var chunk = state.MapIds.Skip(i).Take(ChunkSize).ToList();
            var range = $"{i + 1}-{Math.Min(i + ChunkSize, state.MapIds.Count)} of {state.MapIds.Count}";
            progress?.Report($"Waypoints: maps {range}...");
            var maps = await IndexBuildSupport.WithRetryAsync(() => _api.GetMapsAsync(chunk, ct), $"Waypoints: maps {range}", progress, ct).ConfigureAwait(false);

            // Bulk responses can silently omit ids; fetch those individually.
            var foundIds = maps.Select(m => m.Id).ToHashSet();
            foreach (var missingId in chunk.Where(id => !foundIds.Contains(id)))
            {
                try
                {
                    var single = await IndexBuildSupport.WithRetryAsync(() => _api.GetMapAsync(missingId, ct), $"Waypoints: map {missingId}", progress, ct).ConfigureAwait(false);
                    if (single != null) maps.Add(single);
                }
                catch (Exception ex) when (IndexBuildSupport.IsPermanentFailure(ex))
                {
                    // The API lists the id but will not serve it; skip it.
                }
            }

            foreach (var map in maps.Where(m => m.ContinentId > 0))
            {
                var key = new FloorKey(map.ContinentId, map.DefaultFloor);
                if (knownKeys.Add(key)) state.FloorKeys.Add(key);
            }

            state.MapsDone = i + chunk.Count;
            SaveCheckpoint(state);
        }
    }

    /// <summary>
    /// Downloads floors several at a time. Results are merged in floor order, so a checkpoint
    /// always covers a contiguous run of floors. If any floor fails, the others are stopped and
    /// the error is rethrown.
    /// </summary>
    private async Task DownloadFloorsAsync(BuildState state, IProgress<string>? progress, CancellationToken ct)
    {
        var total = state.FloorKeys.Count;
        var finished = new FloorResult?[total];
        var gate = new object();
        var nextFloor = state.FloorsDone;
        var completed = state.FloorsDone;
        var lastSaved = state.FloorsDone;
        Exception? failure = null;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);

        async Task WorkerAsync()
        {
            while (true)
            {
                int f;
                lock (gate)
                {
                    if (nextFloor >= total || stop.IsCancellationRequested) return;
                    f = nextFloor++;
                }

                var key = state.FloorKeys[f];
                FloorResult result;
                try
                {
                    var floor = await IndexBuildSupport.WithRetryAsync(
                        () => _api.GetContinentFloorAsync(key.ContinentId, key.Floor, stop.Token),
                        $"Waypoints: floor {f + 1} of {total}", progress, stop.Token).ConfigureAwait(false);
                    result = new FloorResult(ExtractWaypoints(floor), false);
                }
                catch (Exception ex) when (IndexBuildSupport.IsPermanentFailure(ex))
                {
                    // Some maps' default floor has no floor resource; skip it rather than lose every other waypoint.
                    result = new FloorResult(new List<WaypointIndexEntry>(), true);
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref failure, ex, null);
                    stop.Cancel();
                    return;
                }

                lock (gate)
                {
                    finished[f] = result;
                    progress?.Report($"Waypoints: floor {++completed} of {total}...");

                    while (state.FloorsDone < total && finished[state.FloorsDone] is { } next)
                    {
                        state.Entries.AddRange(next.Entries);
                        if (next.Skipped) state.Skipped.Add(state.FloorKeys[state.FloorsDone]);
                        finished[state.FloorsDone] = null;
                        state.FloorsDone++;
                    }

                    if (state.FloorsDone - lastSaved >= CheckpointEvery)
                    {
                        SaveCheckpoint(state);
                        lastSaved = state.FloorsDone;
                    }
                }
            }
        }

        await Task.WhenAll(Enumerable.Range(0, FloorConcurrency).Select(_ => WorkerAsync())).ConfigureAwait(false);

        if (failure != null || ct.IsCancellationRequested)
        {
            lock (gate) SaveCheckpoint(state);
            if (failure != null) ExceptionDispatchInfo.Capture(failure).Throw();
            ct.ThrowIfCancellationRequested();
        }
    }

    private static List<WaypointIndexEntry> ExtractWaypoints(ContinentFloor? floor)
    {
        var waypoints = new List<WaypointIndexEntry>();
        if (floor == null) return waypoints;

        foreach (var region in floor.Regions.Values)
            foreach (var floorMap in region.Maps.Values)
                foreach (var poi in floorMap.PointsOfInterest.Values)
                {
                    if (poi.Type == "waypoint" && !string.IsNullOrWhiteSpace(poi.Name) && poi.ChatLink != null)
                        waypoints.Add(new WaypointIndexEntry(poi.Id, poi.Name!, poi.ChatLink, floorMap.Id, floorMap.Name, poi.Coord));
                }
        return waypoints;
    }

    private void SaveCheckpoint(BuildState state) => IndexBuildSupport.TryWriteJson(_checkpointPath, state);

    /// <summary>Returns the saved in-progress download, or null (deleting it) if it is missing, outdated or invalid.</summary>
    private BuildState? LoadCheckpoint()
    {
        var state = IndexBuildSupport.TryReadJson<BuildState>(_checkpointPath);
        var valid = state != null
            && state.SchemaVersion == SchemaVersion
            && state.MapIds.Count > 0
            && state.MapsDone >= 0 && state.MapsDone <= state.MapIds.Count
            && state.FloorsDone >= 0 && state.FloorsDone <= state.FloorKeys.Count
            && DateTime.UtcNow - state.StartedUtc < IndexBuildSupport.PartialMaxAge;
        if (!valid) IndexBuildSupport.TryDelete(_checkpointPath);
        return valid ? state : null;
    }

    /// <summary>
    /// Finds waypoints by name (case-insensitive, apostrophe-tolerant): substring match first,
    /// then fuzzy matching that tolerates plurals and word order.
    /// </summary>
    public IReadOnlyList<WaypointIndexEntry> Search(string query, int maxResults = 20)
    {
        if (_index == null)
            throw new InvalidOperationException("Call EnsureIndexLoadedAsync() before Search().");

        var normalizedQuery = FuzzySearch.NormalizeApostrophes(query);
        var exact = _index
            .Where(e => FuzzySearch.NormalizeApostrophes(e.Name).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .Take(maxResults)
            .ToList();
        if (exact.Count > 0) return exact;

        return _index
            .Where(e => FuzzySearch.FuzzyMatches(e.Name, query))
            .Take(maxResults)
            .ToList();
    }

    /// <summary>The waypoint closest to a continent position among those on the given map, or null if none.</summary>
    public NearbyWaypoint? FindNearest(int mapId, double x, double y)
    {
        if (_index == null)
            throw new InvalidOperationException("Call EnsureIndexLoadedAsync() before FindNearest().");

        var onMap = _index
            .Where(e => e.MapId == mapId)
            .Select(e => new PointOfInterest { Id = e.Id, Name = e.Name, Type = "waypoint", Coord = e.Coord, ChatLink = e.ChatLink });
        return WaypointFinder.FindNearestWaypoint(onMap, x, y);
    }

    private sealed record CacheFile(int SchemaVersion, long? BuildId, List<WaypointIndexEntry> Entries);

    private sealed record FloorKey(int ContinentId, int Floor);

    private sealed record FloorResult(List<WaypointIndexEntry> Entries, bool Skipped);

    /// <summary>Download progress; saved to disk as the checkpoint.</summary>
    private sealed class BuildState
    {
        public int SchemaVersion { get; set; }
        public DateTime StartedUtc { get; set; }
        public List<int> MapIds { get; set; } = new();
        public int MapsDone { get; set; }
        public List<FloorKey> FloorKeys { get; set; } = new();
        public int FloorsDone { get; set; }
        public List<WaypointIndexEntry> Entries { get; set; } = new();
        public List<FloorKey> Skipped { get; set; } = new();
    }
}
