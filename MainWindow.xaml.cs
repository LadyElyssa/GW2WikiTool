using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GW2WikiTool;

public partial class MainWindow : Window, IDisposable
{
    private readonly MumbleLinkReader _mumble = new();
    private readonly Gw2ApiClient _api;
    private readonly AchievementLookup _achievements;
    private readonly WaypointLookup _waypoints;
    private readonly Dictionary<int, Gw2Map> _mapCache = new();
    private readonly Dictionary<int, Task<Gw2Map?>> _mapCacheTasks = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    private MumbleLinkSnapshot? _lastSnapshot;
    private bool _isPolling;
    private bool _isIndexing;
    private bool _isDisposed;
    private bool _retryIsRefresh; // what a click on the button (in its current state) should do
    private EventHandler? _pollHandler;

    private readonly Dictionary<Button, CancellationTokenSource> _copyButtonTimers = new();

    private static readonly SolidColorBrush CopiedBrush = new(Color.FromRgb(0x0E, 0x0B, 0x08));

    private const string WaitingMessage = "Waiting for game...";
    private const string DataLoadedMessage = "Data loaded successfully";

    public MainWindow()
    {
        InitializeComponent();

        _api = new Gw2ApiClient(Environment.GetEnvironmentVariable("GW2_API_KEY"));
        _achievements = new AchievementLookup(_api);
        _waypoints = new WaypointLookup(_api);

        UpdateHintVisibility(AchievementBox, AchievementHint);
        UpdateHintVisibility(WaypointBox, WaypointHint);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollHandler = async (_, _) => await PollLocationAsync();
        _pollTimer.Tick += _pollHandler;
        _pollTimer.Start();

        Closed += OnWindowClosed;

        _ = PollLocationAsync();
        _ = StartupIndexingAsync();
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _shutdownCts.Cancel();

        foreach (var timer in _copyButtonTimers.Values)
        {
            timer.Cancel();
            timer.Dispose();
        }
        _copyButtonTimers.Clear();

        if (_pollTimer != null && _pollHandler != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= _pollHandler;
        }

        _mumble.Dispose();
        _api.Dispose();
        _statusLock.Dispose();
        _shutdownCts.Dispose();

        Closed -= OnWindowClosed;
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed)
            DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Dispose();
        Application.Current.Shutdown();
    }

    private async Task PollLocationAsync()
    {
        if (_isPolling || _isDisposed) return;
        _isPolling = true;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            MumbleLinkSnapshot? snap;
            try { snap = _mumble.Read(); }
            catch { snap = null; }

            _lastSnapshot = snap;

            if (snap is not { IsActive: true, Identity: not null })
            {
                LocationValueText.Text = WaitingMessage;
                if (!_isIndexing) await SetStatusSafeAsync(StatusKind.Ready, WaitingMessage);
                return;
            }

            var map = await GetMapCachedAsync(snap.Identity.MapId);
            var coords = Gw2Coordinates.FromContext(snap.Context);
            if (coords == null && map is { MapRect: not null, ContinentRect: not null })
                coords = Gw2Coordinates.FromMumbleAvatarPosition(snap.AvatarPosition, map.MapRect, map.ContinentRect);

            LocationValueText.Text = coords != null
                ? $"{coords.Value.X:F2}, {coords.Value.Y:F2}"
                : $"{map?.Name ?? $"Map {snap.Identity.MapId}"} (coords unavailable)";

            if (!_isIndexing) await SetStatusSafeAsync(StatusKind.Ready, DataLoadedMessage, showLastUpdated: true);
        }
        catch (OperationCanceledException) when (!_shutdownCts.IsCancellationRequested)
        {
            if (!_isIndexing) await SetStatusSafeAsync(StatusKind.Error, "Location update timed out");
        }
        catch (Exception ex)
        {
            if (!_isIndexing) await SetStatusSafeAsync(StatusKind.Error, $"Error: {DescribeApiError(ex)}");
        }
        finally
        {
            _isPolling = false;
        }
    }

    private async Task StartupIndexingAsync(bool forceRefresh = false)
    {
        if (_isDisposed) return;
        _isIndexing = true;
        RetryButton.IsEnabled = false;
        RetryButton.Visibility = Visibility.Collapsed; // shown again once the outcome is known
        try
        {
            await SetIndexingStatusSafeAsync("Checking achievement & waypoint data...");

            // Both indexes build concurrently; the status line shows the latest message from each.
            var achievementStatus = "";
            var waypointStatus = "";
            void ShowProgress() => _ = SetIndexingStatusSafeAsync(
                string.Join("  |  ", new[] { achievementStatus, waypointStatus }.Where(s => s.Length > 0)));
            var achievementProgress = new Progress<string>(msg => { achievementStatus = msg; ShowProgress(); });
            var waypointProgress = new Progress<string>(msg => { waypointStatus = msg; ShowProgress(); });

            // Each build reports its own failure, so one failing doesn't block the other.
            async Task<string?> BuildAsync(Func<Task> build, string name, Action finished)
            {
                try
                {
                    await build();
                    return null;
                }
                catch (Exception ex)
                {
                    return $"{name} ({DescribeApiError(ex)})";
                }
                finally
                {
                    finished();
                }
            }

            var results = await Task.WhenAll(
                BuildAsync(() => _achievements.EnsureIndexLoadedAsync(forceRefresh: forceRefresh, progress: achievementProgress),
                    "achievement data", () => achievementStatus = ""),
                BuildAsync(() => _waypoints.EnsureIndexLoadedAsync(forceRefresh: forceRefresh, progress: waypointProgress),
                    "waypoint data", () => waypointStatus = ""));
            var failures = results.OfType<string>().ToList();

            if (failures.Count > 0)
            {
                await SetIndexingStatusSafeAsync(
                    $"Error preparing {string.Join(" and ", failures)}. Retry continues from where it stopped.",
                    StatusKind.Error);
                ShowRetryButton(isRefresh: false);
                return;
            }

            var cacheError = _achievements.LastCacheError ?? _waypoints.LastCacheError;
            if (cacheError != null)
            {
                await SetIndexingStatusSafeAsync($"Data ready, but {cacheError}", StatusKind.Ready);
                ShowRetryButton(isRefresh: false);
                return;
            }

            await SetIndexingStatusSafeAsync("Achievement & waypoint data ready", StatusKind.Ready);
            await CheckForGameUpdateAsync();
        }
        catch (Exception ex)
        {
            await SetIndexingStatusSafeAsync($"Error preparing achievement/waypoint data: {DescribeApiError(ex)}", StatusKind.Error);
            ShowRetryButton(isRefresh: false);
        }
        finally
        {
            _isIndexing = false;
        }
    }

    /// <summary>
    /// Both indexes loaded cleanly, so the button only needs to appear if the game itself has
    /// updated since they were built. A failure here (e.g. offline) is treated as unchanged 
    /// rather than as an error, since the cached data is still usable.
    /// </summary>
    private async Task CheckForGameUpdateAsync()
    {
        try
        {
            var currentBuild = await _api.GetCurrentBuildIdAsync();
            var upToDate = currentBuild != null
                && currentBuild == _achievements.CachedBuildId
                && currentBuild == _waypoints.CachedBuildId;
            if (!upToDate) ShowRetryButton(isRefresh: true);
        }
        catch
        {
            // If can't confirm either way; leave the button hidden.
        }
    }

    private void ShowRetryButton(bool isRefresh)
    {
        if (_isDisposed) return;
        _retryIsRefresh = isRefresh;
        RetryButton.ToolTip = isRefresh
            ? "A game update was found — refresh achievement/waypoint data"
            : "Retry fetching achievement/waypoint data";
        AutomationProperties.SetName(RetryButton, isRefresh ? "Refresh achievement/waypoint data" : "Retry fetching achievement/waypoint data");
        RetryButton.IsEnabled = true;
        RetryButton.Visibility = Visibility.Visible;
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await StartupIndexingAsync(forceRefresh: _retryIsRefresh);
    }

    private enum StatusKind { Caching, Error, Ready }

    private static readonly SolidColorBrush CachingBrush = new(Color.FromRgb(0xFF, 0x6A, 0x00));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xFF, 0x00, 0x00));
    private static readonly SolidColorBrush ReadyBrush = new(Color.FromRgb(0x00, 0xFF, 0x00));

    private static Brush BrushFor(StatusKind kind) => kind switch
    {
        StatusKind.Caching => CachingBrush,
        StatusKind.Error => ErrorBrush,
        StatusKind.Ready => ReadyBrush,
        _ => ReadyBrush,
    };

    private async Task SetStatusSafeAsync(StatusKind kind, string message, bool showLastUpdated = false)
    {
        await _statusLock.WaitAsync();
        try
        {
            if (_isDisposed) return;
            SetStatus(kind, message, showLastUpdated);
        }
        finally
        {
            _statusLock.Release();
        }
    }

    private async Task SetIndexingStatusSafeAsync(string message, StatusKind kind = StatusKind.Caching)
    {
        await _statusLock.WaitAsync();
        try
        {
            if (_isDisposed) return;
            SetIndexingStatus(message, kind);
        }
        finally
        {
            _statusLock.Release();
        }
    }

    private void SetIndexingStatus(string message, StatusKind kind = StatusKind.Caching)
    {
        StatusText.Text = message;
        StatusText.ToolTip = message; // the line is trimmed when the window is narrow
        StatusIcon.Foreground = BrushFor(kind);
        LastUpdatedSeparator.Visibility = Visibility.Collapsed;
        LastUpdatedText.Visibility = Visibility.Collapsed;
    }

    private async void CopyLocation_Click(object sender, RoutedEventArgs e)
    {
        await CopyToClipboardAsync(LocationValueText.Text, CopyLocationButton, CopyLocationButtonText);
    }

    private void SetStatus(StatusKind kind, string message, bool showLastUpdated = false)
    {
        StatusText.Text = message;
        StatusText.ToolTip = message;
        StatusIcon.Foreground = BrushFor(kind);

        var visibility = showLastUpdated ? Visibility.Visible : Visibility.Collapsed;
        LastUpdatedSeparator.Visibility = visibility;
        LastUpdatedText.Visibility = visibility;
        if (showLastUpdated) LastUpdatedText.Text = $"Last updated: {DateTime.Now:d MMM yyyy HH:mm}";
    }

    private void AchievementBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHintVisibility(AchievementBox, AchievementHint);
        AchievementBox.ToolTip = null;
    }

    private async void AchievementBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAchievementAsync();
    }

    private async void SearchAchievement_Click(object sender, RoutedEventArgs e) => await SearchAchievementAsync();

    private async void CopyAchievement_Click(object sender, RoutedEventArgs e)
    {
        await CopyToClipboardAsync(AchievementBox.Text, CopyAchievementButton, CopyAchievementButtonText);
    }

    private async Task SearchAchievementAsync()
    {
        if (_isDisposed) return;
        var query = AchievementBox.Text.Trim();
        if (query.Length == 0) return;

        SearchAchievementButton.IsEnabled = false;
        try
        {
            if (int.TryParse(query, out int id))
            {
                var achievement = await _api.GetAchievementAsync(id);
                AchievementBox.Text = achievement != null ? achievement.Id.ToString() : "No match found";
                AchievementBox.ToolTip = achievement?.Name;
                return;
            }

            await _achievements.EnsureIndexLoadedAsync();

            // Text must be set before ToolTip: AchievementBox_TextChanged clears the tooltip.
            var results = _achievements.Search(query);
            if (results.Count == 0)
            {
                AchievementBox.Text = "No match found";
                AchievementBox.ToolTip = null;
            }
            else
            {
                AchievementBox.Text = string.Join(", ", results.Select(r => r.Id));
                // One result shows its name; several show "ID - Category (Group)" per line, with the
                // name added when the results' names differ.
                var sameNames = results.Select(r => r.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() == 1;
                AchievementBox.ToolTip = results.Count == 1
                    ? results[0].Name
                    : string.Join(Environment.NewLine, results.Select(r => sameNames
                        ? $"{r.Id} - {r.DescribePlacements()}"
                        : $"{r.Id} - {r.Name} - {r.DescribePlacements()}"));
            }
        }
        catch (Exception ex)
        {
            AchievementBox.Text = $"Error: {DescribeApiError(ex)}";
            AchievementBox.ToolTip = null;
        }
        finally
        {
            SearchAchievementButton.IsEnabled = true;
            UpdateHintVisibility(AchievementBox, AchievementHint);
        }
    }

    private void WaypointBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHintVisibility(WaypointBox, WaypointHint);
        WaypointBox.ToolTip = null;
    }

    private async void WaypointBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchWaypointAsync();
    }

    private async void SearchWaypoint_Click(object sender, RoutedEventArgs e) => await SearchWaypointAsync();

    private async void CopyWaypoint_Click(object sender, RoutedEventArgs e)
    {
        await CopyToClipboardAsync(WaypointBox.Text, CopyWaypointButton, CopyWaypointButtonText);
    }

    private async Task SearchWaypointAsync()
    {
        if (_isDisposed) return;
        var query = WaypointBox.Text.Trim();
        SearchWaypointButton.IsEnabled = false;
        try
        {
            if (query.Length == 0)
            {
                var snap = _lastSnapshot;
                if (snap is not { IsActive: true, Identity: not null })
                {
                    WaypointBox.Text = "No active game session";
                    WaypointBox.ToolTip = null;
                    return;
                }

                var map = await GetMapCachedAsync(snap.Identity.MapId);
                var coords = Gw2Coordinates.FromContext(snap.Context);
                if (coords == null && map is { MapRect: not null, ContinentRect: not null })
                    coords = Gw2Coordinates.FromMumbleAvatarPosition(snap.AvatarPosition, map.MapRect, map.ContinentRect);

                if (map == null || coords == null)
                {
                    WaypointBox.Text = "Location unavailable";
                    WaypointBox.ToolTip = null;
                    return;
                }

                // Uses the local index; falls back to the API while it is still building.
                var nearest = _waypoints.IsIndexLoaded
                    ? _waypoints.FindNearest(map.Id, coords.Value.X, coords.Value.Y)
                    : WaypointFinder.FindNearestWaypoint(await _api.GetMapPointsOfInterestAsync(map), coords.Value.X, coords.Value.Y);
                WaypointBox.Text = nearest != null
                    ? nearest.Poi.ChatLink ?? ChatLink.ForWaypoint(nearest.Poi.Id)
                    : "No waypoints on this map";
                WaypointBox.ToolTip = nearest?.Poi.Name;
                return;
            }

            await _waypoints.EnsureIndexLoadedAsync();

            var results = _waypoints.Search(query, 1);
            WaypointBox.Text = results.Count > 0 ? results[0].ChatLink : "No match found";
            WaypointBox.ToolTip = results.Count > 0 ? results[0].Name : null;
        }
        catch (Exception ex)
        {
            WaypointBox.Text = $"Error: {DescribeApiError(ex)}";
            WaypointBox.ToolTip = null;
        }
        finally
        {
            SearchWaypointButton.IsEnabled = true;
            UpdateHintVisibility(WaypointBox, WaypointHint);
        }
    }

    private async Task<Gw2Map?> GetMapCachedAsync(int mapId)
    {
        if (_mapCache.TryGetValue(mapId, out var cached)) return cached;

        if (_mapCacheTasks.TryGetValue(mapId, out var existingTask))
        {
            return await existingTask;
        }

        var task = FetchMapAsync(mapId);
        _mapCacheTasks[mapId] = task;

        try
        {
            var map = await task;
            if (map != null)
            {
                _mapCache[mapId] = map;
            }
            return map;
        }
        finally
        {
            _mapCacheTasks.Remove(mapId);
        }
    }

    private async Task<Gw2Map?> FetchMapAsync(int mapId)
    {
        try
        {
            return await _api.GetMapAsync(mapId);
        }
        catch
        {
            return null;
        }
    }

    private static void UpdateHintVisibility(TextBox box, UIElement hint) =>
        hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;

    private static string DescribeApiError(Exception ex) => ex switch
    {
        TaskCanceledException { InnerException: TimeoutException } =>
            "GW2 API request timed out — it may be slow or temporarily down.",
        HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout } =>
            "GW2 API appears to be down for maintenance or an outage.",
        HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests } =>
            "GW2 API is rate-limiting requests — try again in a moment.",
        HttpRequestException =>
            "Can't reach the GW2 API right now — check your internet connection.",
        JsonException =>
            "GW2 API returned an unexpected response — it may be having issues.",
        _ => ex.Message,
    };

    private async Task CopyToClipboardAsync(string? text, Button button, TextBlock buttonText)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "No match found")
            return;

        try
        {
            Clipboard.SetText(text);

            if (_copyButtonTimers.TryGetValue(button, out var existingCts))
            {
                existingCts.Cancel();
                existingCts.Dispose();
                _copyButtonTimers.Remove(button);
            }

            buttonText.Text = "COPIED";
            button.Background = CopiedBrush;

            var cts = new CancellationTokenSource();
            _copyButtonTimers[button] = cts;

            try
            {
                await Task.Delay(1000, cts.Token);
                if (!_isDisposed)
                {
                    buttonText.Text = "COPY";
                    button.Background = (Brush)Application.Current.Resources["ButtonFill"];
                }
            }
            catch (OperationCanceledException)
            {
            }
            finally
            {
                _copyButtonTimers.Remove(button);
                cts.Dispose();
            }
        }
        catch (Exception)
        {
            buttonText.Text = "COPY";
            button.Background = (Brush)Application.Current.Resources["ButtonFill"];
        }
    }
}