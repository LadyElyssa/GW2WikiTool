using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GW2WikiTool;

/// <summary>An achievement's category and that category's group (e.g. "Historical").</summary>
public sealed record AchievementPlacement(string Category, string? Group);

/// <summary>A cached achievement. Placements is null when it belongs to no category or category
/// data was unavailable.</summary>
public sealed record AchievementIndexEntry(int Id, string Name, List<AchievementPlacement>? Placements = null)
{
    /// <summary>True if the achievement is listed only under Historical groups.</summary>
    public bool IsHistorical() =>
        Placements is { Count: > 0 }
        && Placements.All(p => string.Equals(p.Group, "Historical", StringComparison.OrdinalIgnoreCase));

    /// <summary>Formats placements as "Category (Group); ...", or "no category".</summary>
    public string DescribePlacements() =>
        Placements is { Count: > 0 }
            ? string.Join("; ", Placements.Select(p => string.IsNullOrEmpty(p.Group) ? p.Category : $"{p.Category} ({p.Group})"))
            : "no category";
}

/// <summary>
/// Name search over GW2 achievements. The API cannot search by name, so the full list is
/// downloaded once, indexed and cached to disk. Daily wrapper achievements and "(Annual)"
/// festival re-runs are left out of the index.
/// </summary>
public sealed class AchievementLookup
{
    private const int ChunkSize = 200; // API limit for bulk id lookups

    // Bump when the indexing rules change so older caches are rebuilt.
    private const int SchemaVersion = 6;

    // Tie-breaks for names shared by several achievements, used when category data leaves more
    // than one candidate. Extended by achievement_preferred_ids.json next to the exe.
    private static readonly Dictionary<string, int> BuiltInPreferredIds = new()
    {
        ["Dragon's Gaze"] = 2076,
        ["Choya Champion"] = 9000,
    };

    private readonly Gw2ApiClient _api;
    private readonly string _cachePath;
    private readonly string _checkpointPath;
    private readonly Dictionary<string, int> _preferredIds;
    private readonly object _loadLock = new();
    private List<AchievementIndexEntry>? _index;
    private Task? _loadTask;

    /// <summary>A warning about the last load (e.g. it could not be saved), or null. The index is
    /// still usable in memory when this is set.</summary>
    public string? LastCacheError { get; private set; }

    /// <summary>The game build the loaded index was built or last confirmed against, or null if unknown.</summary>
    public long? CachedBuildId { get; private set; }

    public AchievementLookup(Gw2ApiClient api, string? cachePath = null, string? preferredIdsPath = null)
    {
        _api = api;
        _cachePath = cachePath ?? Path.Combine(AppContext.BaseDirectory, "achievements_index.json");
        _checkpointPath = _cachePath + ".partial";
        _preferredIds = LoadPreferredIds(preferredIdsPath ?? Path.Combine(AppContext.BaseDirectory, "achievement_preferred_ids.json"));
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

            // A previous attempt built the achievements but didn't finish (category data or the
            // disk write failed); retry only that tail, not the achievement download.
            await FinishAndPersistAsync(_index, progress, ct).ConfigureAwait(false);
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
        var entries = await DownloadAchievementsAsync(progress, ct).ConfigureAwait(false);
        await FinishAndPersistAsync(entries, progress, ct).ConfigureAwait(false);
    }

    /// <summary>Adds categories/groups to the achievements and writes the cache. Safe to call
    /// again on just-downloaded entries or on an already-cached index that never finished.</summary>
    private async Task FinishAndPersistAsync(List<AchievementIndexEntry> entries, IProgress<string>? progress, CancellationToken ct)
    {
        // Categories are fetched last so a failure here keeps the downloaded achievements.
        Dictionary<int, List<AchievementPlacement>>? placements = null;
        string? placementError = null;
        try
        {
            progress?.Report("Achievements: fetching categories...");
            placements = await IndexBuildSupport.WithRetryAsync(() => LoadPlacementsAsync(ct), "Achievements: categories", progress, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            placementError = ex.Message;
        }

        _index = placements == null
            ? entries
            : entries.Select(e => placements.TryGetValue(e.Id, out var list) ? e with { Placements = list } : e).ToList();

        if (placementError != null)
        {
            // Not cached: LastCacheError set means the next call retries just this tail.
            LastCacheError = $"achievement category data couldn't be loaded ({placementError}); duplicate achievements can't be narrowed down until it is";
            return;
        }

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
            await File.WriteAllTextAsync(_cachePath, JsonSerializer.Serialize(new CacheFile(SchemaVersion, buildId, _index)), ct).ConfigureAwait(false);
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
    /// Downloads every achievement's ID and name, checkpointing after each chunk so an
    /// interrupted download resumes where it stopped.
    /// </summary>
    private async Task<List<AchievementIndexEntry>> DownloadAchievementsAsync(IProgress<string>? progress, CancellationToken ct)
    {
        var checkpoint = LoadCheckpoint();
        List<int> ids;
        List<AchievementIndexEntry> entries;
        int next;
        DateTime started;

        if (checkpoint != null)
        {
            (ids, entries, next, started) = (checkpoint.Ids, checkpoint.Entries, checkpoint.NextIndex, checkpoint.StartedUtc);
            progress?.Report($"Achievements: resuming at {next} of {ids.Count}...");
        }
        else
        {
            progress?.Report("Achievements: fetching id list...");
            ids = await IndexBuildSupport.WithRetryAsync(() => _api.GetAllAchievementIdsAsync(ct), "Achievements: id list", progress, ct).ConfigureAwait(false);
            (entries, next, started) = (new List<AchievementIndexEntry>(ids.Count), 0, DateTime.UtcNow);
        }

        for (int i = next; i < ids.Count; i += ChunkSize)
        {
            var chunk = ids.Skip(i).Take(ChunkSize).ToList();
            var range = $"{i + 1}-{Math.Min(i + ChunkSize, ids.Count)} of {ids.Count}";
            progress?.Report($"Achievements: {range}...");
            var achievements = await IndexBuildSupport.WithRetryAsync(() => _api.GetAchievementsAsync(chunk, ct), $"Achievements: {range}", progress, ct).ConfigureAwait(false);

            // Bulk responses can silently omit ids; fetch those individually.
            var foundIds = achievements.Select(a => a.Id).ToHashSet();
            foreach (var missingId in chunk.Where(id => !foundIds.Contains(id)))
            {
                try
                {
                    var single = await IndexBuildSupport.WithRetryAsync(() => _api.GetAchievementAsync(missingId, ct), $"Achievements: id {missingId}", progress, ct).ConfigureAwait(false);
                    if (single != null) achievements.Add(single);
                }
                catch (Exception ex) when (IndexBuildSupport.IsPermanentFailure(ex))
                {
                    // The API lists the id but will not serve it; skip it.
                }
            }

            foreach (var a in achievements)
            {
                if (string.IsNullOrWhiteSpace(a.Name)) continue;
                if (a.Flags.Contains("Daily")) continue;
                if (IsAnnualFestival(a.Name)) continue;
                entries.Add(new AchievementIndexEntry(a.Id, a.Name));
            }

            IndexBuildSupport.TryWriteJson(_checkpointPath, new BuildCheckpoint(SchemaVersion, started, ids, i + chunk.Count, entries));
        }

        return entries;
    }

    /// <summary>Returns the saved in-progress download, or null (deleting it) if it is missing, outdated or invalid.</summary>
    private BuildCheckpoint? LoadCheckpoint()
    {
        var checkpoint = IndexBuildSupport.TryReadJson<BuildCheckpoint>(_checkpointPath);
        var valid = checkpoint != null
            && checkpoint.SchemaVersion == SchemaVersion
            && checkpoint.Ids.Count > 0
            && checkpoint.NextIndex >= 0 && checkpoint.NextIndex <= checkpoint.Ids.Count
            && DateTime.UtcNow - checkpoint.StartedUtc < IndexBuildSupport.PartialMaxAge;
        if (!valid) IndexBuildSupport.TryDelete(_checkpointPath);
        return valid ? checkpoint : null;
    }

    /// <summary>Maps each achievement ID to its category/group placements. Throws if the API returns no categories.</summary>
    private async Task<Dictionary<int, List<AchievementPlacement>>> LoadPlacementsAsync(CancellationToken ct)
    {
        var groups = await _api.GetAchievementGroupsAsync(ct).ConfigureAwait(false);
        var categories = await _api.GetAchievementCategoriesAsync(ct).ConfigureAwait(false);
        if (categories.Count == 0)
            throw new InvalidOperationException("the API returned no achievement categories");

        var groupByCategory = new Dictionary<int, string>();
        foreach (var g in groups)
            foreach (var categoryId in g.Categories)
                groupByCategory[categoryId] = g.Name;

        var result = new Dictionary<int, List<AchievementPlacement>>();
        foreach (var c in categories)
        {
            groupByCategory.TryGetValue(c.Id, out var groupName);
            foreach (var achievementId in c.GetAchievementIds())
            {
                if (!result.TryGetValue(achievementId, out var list))
                    result[achievementId] = list = new List<AchievementPlacement>();
                list.Add(new AchievementPlacement(c.Name, groupName));
            }
        }
        return result;
    }

    /// <summary>True for "(Annual)"-prefixed names, the yearly festival re-runs. Parent
    /// achievements such as "Annual Customs" are not matched.</summary>
    private static bool IsAnnualFestival(string name) =>
        name.StartsWith("(Annual)", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Finds achievements by name (case-insensitive, apostrophe-tolerant), newest ID first.
    /// An exact name shared by several achievements is narrowed by dropping Historical ones,
    /// then by the preferred ID if it is still a candidate. Otherwise falls back to substring,
    /// then fuzzy, matching.
    /// </summary>
    public IReadOnlyList<AchievementIndexEntry> Search(string query, int maxResults = 20)
    {
        if (_index == null)
            throw new InvalidOperationException("Call EnsureIndexLoadedAsync() before Search().");

        var normalizedQuery = FuzzySearch.NormalizeApostrophes(query).Trim();

        var sameName = _index
            .Where(e => string.Equals(FuzzySearch.NormalizeApostrophes(e.Name).Trim(), normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .ToList();
        if (sameName.Count == 1) return sameName;
        if (sameName.Count > 1)
        {
            var current = sameName.Where(e => !e.IsHistorical()).ToList();
            var pool = current.Count > 0 ? current : sameName;
            if (pool.Count == 1) return pool;

            if (_preferredIds.TryGetValue(normalizedQuery, out var preferredId))
            {
                var preferred = pool.FirstOrDefault(e => e.Id == preferredId);
                if (preferred != null) return new List<AchievementIndexEntry> { preferred };
            }

            return pool.OrderByDescending(e => e.Id).Take(maxResults).ToList();
        }

        var contains = _index
            .Where(e => FuzzySearch.NormalizeApostrophes(e.Name).Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(e => e.Id)
            .Take(maxResults)
            .ToList();
        if (contains.Count > 0) return contains;

        return _index
            .Where(e => FuzzySearch.FuzzyMatches(e.Name, query))
            .OrderByDescending(e => e.Id)
            .Take(maxResults)
            .ToList();
    }

    /// <summary>Built-in preferred IDs overlaid with achievement_preferred_ids.json
    /// ({"Name": id}) if present. A missing or invalid file is ignored.</summary>
    private static Dictionary<string, int> LoadPreferredIds(string path)
    {
        var result = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, id) in BuiltInPreferredIds)
            result[FuzzySearch.NormalizeApostrophes(name).Trim()] = id;

        try
        {
            if (File.Exists(path))
            {
                var fromFile = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(path));
                if (fromFile != null)
                    foreach (var (name, id) in fromFile)
                        result[FuzzySearch.NormalizeApostrophes(name).Trim()] = id;
            }
        }
        catch
        {
            // Ignore an invalid override file.
        }

        return result;
    }

    private sealed record CacheFile(int SchemaVersion, long? BuildId, List<AchievementIndexEntry> Entries);

    /// <summary>In-progress download: the ID list, how far through it we are, and the entries so far.</summary>
    private sealed record BuildCheckpoint(int SchemaVersion, DateTime StartedUtc, List<int> Ids, int NextIndex, List<AchievementIndexEntry> Entries);
}
