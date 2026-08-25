using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace GW2WikiTool;

public sealed record AchEntry(int Id, string Name);

public sealed class AchievementLookup
{
    private const int ChunkSz = 200;
    private const int SchemaVer = 4;

    private readonly Gw2ApiClient _api;
    private readonly string _cachePath;
    private readonly object _loadLock = new();
    private List<AchEntry>? _index;
    private Task? _loadTask;

    public string? LastCacheErr { get; private set; }

    public AchievementLookup(Gw2ApiClient api, string? cachePath = null)
    {
        _api = api;
        _cachePath = cachePath ?? Path.Combine(AppContext.BaseDirectory, "achievements_index.json");
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

        progress?.Report("Fetching achievement id list...");
        var ids = await _api.GetAllAchIds(ct);

        var index = new List<AchEntry>(ids.Count);
        for (int i = 0; i < ids.Count; i += ChunkSz)
        {
            var chunk = ids.Skip(i).Take(ChunkSz).ToList();
            progress?.Report($"Fetching achievements {i + 1}-{Math.Min(i + ChunkSz, ids.Count)} of {ids.Count}...");
            var achList = await _api.GetAchs(chunk, ct);

            var foundIds = achList.Select(a => a.Id).ToHashSet();
            var missing = chunk.Where(id => !foundIds.Contains(id)).ToList();
            foreach (var id in missing)
            {
                var single = await _api.GetAch(id, ct);
                if (single != null) achList.Add(single);
            }

            foreach (var a in achList)
            {
                if (string.IsNullOrWhiteSpace(a.Name)) continue;
                if (a.Flags.Contains("Daily")) continue; // skip daily wrapper achievements
                index.Add(new AchEntry(a.Id, a.Name));
            }
        }

        _index = index;
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

    public IReadOnlyList<AchEntry> Search(string query, int maxResults = 20)
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

    private sealed record CacheFile(int SchemaVer, List<AchEntry> Entries);
}