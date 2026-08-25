using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace GW2WikiTool;

public partial class MainWindow : Window, IDisposable
{
    private readonly MumbleLinkReader _mumble = new();
    private readonly Gw2ApiClient _api;
    private readonly AchievementLookup _ach;
    private readonly WaypointLookup _wp;
    private readonly Dictionary<int, Gw2Map> _mapCache = new();
    private readonly Dictionary<int, Task<Gw2Map?>> _mapTasks = new();
    private readonly DispatcherTimer _pollTimer;
    private readonly SemaphoreSlim _statusLock = new(1, 1);
    private readonly CancellationTokenSource _shutdownCts = new();

    private MumbleLinkSnapshot? _lastSnap;
    private bool _isPolling;
    private bool _isIndexing;
    private bool _isDisposed;
    private EventHandler? _pollHandler;

    private readonly Dictionary<Button, CancellationTokenSource> _copyTimers = new();

    private static readonly SolidColorBrush CopiedBrush = new(Color.FromRgb(0x0E, 0x0B, 0x08));

    private const string WaitingMsg = "Waiting for game...";
    private const string DataLoadedMsg = "Data loaded successfully";

    public MainWindow()
    {
        InitializeComponent();

        _api = new Gw2ApiClient(Environment.GetEnvironmentVariable("GW2_API_KEY"));
        _ach = new AchievementLookup(_api);
        _wp = new WaypointLookup(_api);

        UpdateHint(AchievementBox, AchievementHint);
        UpdateHint(WaypointBox, WaypointHint);

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _pollHandler = async (_, _) => await PollLocAsync();
        _pollTimer.Tick += _pollHandler;
        _pollTimer.Start();

        Closed += OnClosed;

        // start polling right away instead of waiting for the first tick
        _ = PollLocAsync();
        _ = StartupIdxAsync();
    }

    private void OnClosed(object? sender, EventArgs e)
    {
        Dispose();
        Application.Current.Shutdown();
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        _isDisposed = true;

        _shutdownCts.Cancel();

        foreach (var t in _copyTimers.Values)
        {
            t.Cancel();
            t.Dispose();
        }
        _copyTimers.Clear();

        if (_pollTimer != null && _pollHandler != null)
        {
            _pollTimer.Stop();
            _pollTimer.Tick -= _pollHandler;
        }

        _mumble.Dispose();
        _api.Dispose();
        _statusLock.Dispose();
        _shutdownCts.Dispose();

        Closed -= OnClosed;
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

    private async Task PollLocAsync()
    {
        if (_isPolling || _isDisposed) return;
        _isPolling = true;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(_shutdownCts.Token);
            cts.CancelAfter(TimeSpan.FromSeconds(10));

            MumbleLinkSnapshot? s;
            try { s = _mumble.Read(); }
            catch { s = null; }

            _lastSnap = s;

            if (s is not { IsActive: true, Identity: not null })
            {
                LocationValueText.Text = WaitingMsg;
                if (!_isIndexing) await SetStatusSafeAsync(StatusKind.Ready, WaitingMsg);
                return;
            }

            var m = await GetMapCachedAsync(s.Identity.MapId);
            var c = Gw2Coords.FromCtx(s.Context);
            if (c == null && m is { MapRect: not null, ContRect: not null })
                c = Gw2Coords.FromMumble(s.AvPos, m.MapRect, m.ContRect);

            LocationValueText.Text = c != null
                ? $"{c.Value.X:F2}, {c.Value.Y:F2}"
                : $"{m?.Name ?? $"Map {s.Identity.MapId}"} (coords unavailable)";

            if (!_isIndexing) await SetStatusSafeAsync(StatusKind.Ready, DataLoadedMsg, showLastUpdated: true);
        }
        catch (OperationCanceledException) when (!_shutdownCts.IsCancellationRequested)
        {
            if (!_isIndexing) await SetStatusSafeAsync(StatusKind.Error, "Location update timed out");
        }
        catch (Exception ex)
        {
            if (!_isIndexing) await SetStatusSafeAsync(StatusKind.Error, $"Error: {DescribeApiErr(ex)}");
        }
        finally
        {
            _isPolling = false;
        }
    }

    private async Task StartupIdxAsync(bool forceRefresh = false)
    {
        if (_isDisposed) return;
        _isIndexing = true;
        RetryButton.IsEnabled = false;
        try
        {
            var p = new Progress<string>(msg => _ = SetIdxStatusSafeAsync(msg));

            await SetIdxStatusSafeAsync("Checking achievement & waypoint data...");
            await _ach.EnsureLoaded(forceRefresh: forceRefresh, progress: p);
            await _wp.EnsureLoaded(forceRefresh: forceRefresh, progress: p);

            var err = _ach.LastCacheErr ?? _wp.LastCacheErr;
            await SetIdxStatusSafeAsync(
                err == null
                    ? "Achievement & waypoint data ready"
                    : $"Data ready, but not saved to disk: {err}",
                StatusKind.Ready);
        }
        catch (Exception ex)
        {
            await SetIdxStatusSafeAsync($"Error preparing achievement/waypoint data: {DescribeApiErr(ex)}", StatusKind.Error);
        }
        finally
        {
            _isIndexing = false;
            if (!_isDisposed)
            {
                RetryButton.IsEnabled = true;
            }
        }
    }

    private async void Retry_Click(object sender, RoutedEventArgs e)
    {
        await StartupIdxAsync(forceRefresh: true);
    }

    private enum StatusKind { Caching, Error, Ready }

    private static readonly SolidColorBrush CachingBrush = new(Color.FromRgb(0xFF, 0x6A, 0x00));
    private static readonly SolidColorBrush ErrorBrush = new(Color.FromRgb(0xFF, 0x00, 0x00));
    private static readonly SolidColorBrush ReadyBrush = new(Color.FromRgb(0x00, 0xFF, 0x00));

    private static Brush BrushFor(StatusKind k) => k switch
    {
        StatusKind.Caching => CachingBrush,
        StatusKind.Error => ErrorBrush,
        StatusKind.Ready => ReadyBrush,
        _ => ReadyBrush,
    };

    private async Task SetStatusSafeAsync(StatusKind k, string msg, bool showLastUpdated = false)
    {
        await _statusLock.WaitAsync();
        try
        {
            if (_isDisposed) return;
            SetStatus(k, msg, showLastUpdated);
        }
        finally
        {
            _statusLock.Release();
        }
    }

    private async Task SetIdxStatusSafeAsync(string msg, StatusKind k = StatusKind.Caching)
    {
        await _statusLock.WaitAsync();
        try
        {
            if (_isDisposed) return;
            SetIdxStatus(msg, k);
        }
        finally
        {
            _statusLock.Release();
        }
    }

    private void SetIdxStatus(string msg, StatusKind k = StatusKind.Caching)
    {
        StatusText.Text = msg;
        StatusIcon.Foreground = BrushFor(k);
        LastUpdatedSeparator.Visibility = Visibility.Collapsed;
        LastUpdatedText.Visibility = Visibility.Collapsed;
    }

    private async void CopyLocation_Click(object sender, RoutedEventArgs e)
    {
        await CopyToClipboardAsync(LocationValueText.Text, CopyLocationButton, CopyLocationButtonText);
    }

    private void SetStatus(StatusKind k, string msg, bool showLastUpdated = false)
    {
        StatusText.Text = msg;
        StatusIcon.Foreground = BrushFor(k);

        var v = showLastUpdated ? Visibility.Visible : Visibility.Collapsed;
        LastUpdatedSeparator.Visibility = v;
        LastUpdatedText.Visibility = v;
        if (showLastUpdated) LastUpdatedText.Text = $"Last updated: {DateTime.Now:d MMM yyyy HH:mm}";
    }

    private void AchievementBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHint(AchievementBox, AchievementHint);
        AchievementBox.ToolTip = null;
    }

    private async void AchievementBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAchAsync();
    }

    private async void SearchAchievement_Click(object sender, RoutedEventArgs e) => await SearchAchAsync();

    private async void CopyAchievement_Click(object sender, RoutedEventArgs e)
    {
        await CopyToClipboardAsync(AchievementBox.Text, CopyAchievementButton, CopyAchievementButtonText);
    }

    private async Task SearchAchAsync()
    {
        if (_isDisposed) return;
        var q = AchievementBox.Text.Trim();
        if (q.Length == 0) return;

        SearchAchievementButton.IsEnabled = false;
        try
        {
            if (int.TryParse(q, out int id))
            {
                var a = await _api.GetAch(id);
                AchievementBox.Text = a != null ? a.Id.ToString() : "No match found";
                AchievementBox.ToolTip = a?.Name;
                return;
            }

            await _ach.EnsureLoaded();

            var r = _ach.Search(q, 1);
            AchievementBox.Text = r.Count > 0 ? r[0].Id.ToString() : "No match found";
            AchievementBox.ToolTip = r.Count > 0 ? r[0].Name : null;
        }
        catch (Exception ex)
        {
            AchievementBox.Text = $"Error: {DescribeApiErr(ex)}";
            AchievementBox.ToolTip = null;
        }
        finally
        {
            SearchAchievementButton.IsEnabled = true;
            UpdateHint(AchievementBox, AchievementHint);
        }
    }

    private void WaypointBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateHint(WaypointBox, WaypointHint);
        WaypointBox.ToolTip = null;
    }

    private async void WaypointBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchWpAsync();
    }

    private async void SearchWaypoint_Click(object sender, RoutedEventArgs e) => await SearchWpAsync();

    private async void CopyWaypoint_Click(object sender, RoutedEventArgs e)
    {
        await CopyToClipboardAsync(WaypointBox.Text, CopyWaypointButton, CopyWaypointButtonText);
    }

    private async Task SearchWpAsync()
    {
        if (_isDisposed) return;
        var q = WaypointBox.Text.Trim();
        SearchWaypointButton.IsEnabled = false;
        try
        {
            if (q.Length == 0)
            {
                var s = _lastSnap;
                if (s is not { IsActive: true, Identity: not null })
                {
                    WaypointBox.Text = "No active game session";
                    WaypointBox.ToolTip = null;
                    return;
                }

                var m = await GetMapCachedAsync(s.Identity.MapId);
                var c = Gw2Coords.FromCtx(s.Context);
                if (c == null && m is { MapRect: not null, ContRect: not null })
                    c = Gw2Coords.FromMumble(s.AvPos, m.MapRect, m.ContRect);

                if (m == null || c == null)
                {
                    WaypointBox.Text = "Location unavailable";
                    WaypointBox.ToolTip = null;
                    return;
                }

                var pois = await _api.GetPois(m);
                var n = WpFinder.FindClosest(pois, c.Value.X, c.Value.Y);
                WaypointBox.Text = n != null
                    ? n.Poi.ChatLink ?? ChatLink.MakeWpLink(n.Poi.Id)
                    : "No waypoints on this map";
                WaypointBox.ToolTip = n?.Poi.Name;
                return;
            }

            await _wp.EnsureLoaded();

            var r = _wp.Search(q, 1);
            WaypointBox.Text = r.Count > 0 ? r[0].ChatLink : "No match found";
            WaypointBox.ToolTip = r.Count > 0 ? r[0].Name : null;
        }
        catch (Exception ex)
        {
            WaypointBox.Text = $"Error: {DescribeApiErr(ex)}";
            WaypointBox.ToolTip = null;
        }
        finally
        {
            SearchWaypointButton.IsEnabled = true;
            UpdateHint(WaypointBox, WaypointHint);
        }
    }

    private async Task<Gw2Map?> GetMapCachedAsync(int mapId)
    {
        if (_mapCache.TryGetValue(mapId, out var c)) return c;

        if (_mapTasks.TryGetValue(mapId, out var t))
            return await t;

        var task = FetchMapAsync(mapId);
        _mapTasks[mapId] = task;

        try
        {
            var m = await task;
            if (m != null)
                _mapCache[mapId] = m;
            return m;
        }
        finally
        {
            _mapTasks.Remove(mapId);
        }
    }

    private async Task<Gw2Map?> FetchMapAsync(int mapId)
    {
        try
        {
            return await _api.GetMap(mapId);
        }
        catch
        {
            // idk why this fails sometimes but it does
            return null;
        }
    }

    private static void UpdateHint(TextBox box, UIElement hint) =>
        hint.Visibility = string.IsNullOrEmpty(box.Text) ? Visibility.Visible : Visibility.Collapsed;

    private static string DescribeApiErr(Exception ex)
    {
        if (ex is TaskCanceledException { InnerException: TimeoutException })
            return "GW2 API request timed out — it may be slow or temporarily down.";
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout })
            return "GW2 API appears to be down for maintenance or an outage.";
        if (ex is HttpRequestException { StatusCode: HttpStatusCode.TooManyRequests })
            return "GW2 API is rate-limiting requests — try again in a moment.";
        if (ex is HttpRequestException)
            return "Can't reach the GW2 API right now — check your internet connection.";
        if (ex is JsonException)
            return "GW2 API returned an unexpected response — it may be having issues.";
        return ex.Message;
    }

    private async Task CopyToClipboardAsync(string? text, Button btn, TextBlock txt)
    {
        if (string.IsNullOrWhiteSpace(text) || text == "No match found")
            return;

        try
        {
            Clipboard.SetText(text);

            if (_copyTimers.TryGetValue(btn, out var oldCts))
            {
                oldCts.Cancel();
                oldCts.Dispose();
                _copyTimers.Remove(btn);
            }

            txt.Text = "COPIED";
            btn.Background = CopiedBrush;

            var cts = new CancellationTokenSource();
            _copyTimers[btn] = cts;

            try
            {
                await Task.Delay(1000, cts.Token);
                if (!_isDisposed)
                {
                    txt.Text = "COPY";
                    // TODO: fix this hardcoded resource lookup
                    btn.Background = (Brush)Application.Current.Resources["ButtonFill"];
                }
            }
            catch (OperationCanceledException)
            {
                // whatever
            }
            finally
            {
                _copyTimers.Remove(btn);
                cts.Dispose();
            }
        }
        catch (Exception)
        {
            txt.Text = "COPY";
            btn.Background = (Brush)Application.Current.Resources["ButtonFill"];
        }
    }
}