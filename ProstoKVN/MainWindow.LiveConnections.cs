using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ProstoKVN;

public partial class MainWindow
{
    private readonly DispatcherTimer liveRoutesTimer = new() { Interval = TimeSpan.FromSeconds(2) };
    private readonly LiveConnectionMonitor liveRoutesMonitor = new();
    private readonly CancellationTokenSource liveRoutesCancellation = new();
    private IReadOnlyList<LiveConnection> liveRoutesRows = Array.Empty<LiveConnection>();
    private bool liveRoutesBusy;
    // Tests can provide telemetry without connecting to the user's controller.
    private Func<CancellationToken, Task<string>>? liveRoutesLoader = null;

    private void InitializeLiveRoutes()
    {
        liveRoutesTimer.Tick += async (_, _) => await RefreshLiveRoutesAsync();
        liveRoutesTimer.Start();
        MainTabs.SelectionChanged += async (sender, args) =>
        { if (args.Source == MainTabs && MainTabs.SelectedIndex == 4) await RefreshLiveRoutesAsync(); };
        Closed += (_, _) => { liveRoutesTimer.Stop(); liveRoutesCancellation.Cancel(); liveRoutesCancellation.Dispose(); };
    }
    private void LiveRoutesLink_Click(object sender, RoutedEventArgs e)
    { MainTabs.SelectedIndex = 4; HelpPageScroll.ScrollToTop(); }
    private async Task RefreshLiveRoutesAsync()
    {
        if (isWindowClosing || !IsVisible || MainTabs.SelectedIndex != 4 || liveRoutesBusy) return;
        if (!core.IsRunning && liveRoutesLoader == null)
        { ClearLiveRoutes("Нет подключения. Подключитесь, чтобы увидеть маршруты."); return; }
        liveRoutesBusy = true;
        int revision = connectionRevision;
        try
        {
            string json = await (liveRoutesLoader?.Invoke(liveRoutesCancellation.Token) ?? core.ReadConnectionsAsync(liveRoutesCancellation.Token));
            if (isWindowClosing || revision != connectionRevision) { ClearLiveRoutes("Подключение изменилось. Ожидаем новые данные."); return; }
            liveRoutesRows = liveRoutesMonitor.Read(json, DateTime.UtcNow, core.CurrentProviderFiles,
                nodes.Select(n => n.Name).Concat(nodes.Select((_, i) => RoutingPolicy.NodeTarget(nodes, i))));
            ApplyLiveRoutesFilter();
            LiveRoutesStatus.Text = $"Обновлено {DateTime.Now:HH:mm:ss} · Каждые 2 секунды · Активные соединения";
        }
        catch (OperationCanceledException) when (isWindowClosing) { }
        catch (Exception ex)
        { if (!isWindowClosing) ClearLiveRoutes("Нет свежих данных: " + ex.GetType().Name + ". Повторим автоматически."); }
        finally { liveRoutesBusy = false; }
    }
    private void ClearLiveRoutes(string status)
    { liveRoutesRows = Array.Empty<LiveConnection>(); liveRoutesMonitor.Reset(); ApplyLiveRoutesFilter(); LiveRoutesStatus.Text = status; }
    private void LiveRoutesFilter_Changed(object sender, RoutedEventArgs e) => ApplyLiveRoutesFilter();
    private void ApplyLiveRoutesFilter()
    {
        if (LiveRoutesList == null || LiveRoutesSearch == null || LiveRoutesFilter == null) return;
        string query = LiveRoutesSearch.Text.Trim();
        string filter = (LiveRoutesFilter.SelectedItem as ComboBoxItem)?.Tag as string ?? "all";
        var rows = liveRoutesRows.Where(r => (filter == "all" || r.Route == filter)
            && (query.Length == 0 || r.Target.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.Ip.Contains(query, StringComparison.OrdinalIgnoreCase) || r.Process.Contains(query, StringComparison.OrdinalIgnoreCase)
                || r.Rule.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        string? selected = (LiveRoutesList.SelectedItem as LiveConnection)?.Id;
        var scroll = FindLiveScroll(LiveRoutesList);
        double offset = scroll?.VerticalOffset ?? 0;
        LiveRoutesList.ItemsSource = rows;
        if (selected != null) LiveRoutesList.SelectedItem = rows.FirstOrDefault(r => r.Id == selected);
        if (offset > 0) Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => scroll?.ScrollToVerticalOffset(offset)));
        LiveRoutesCounts.Text = $"VPN: {liveRoutesRows.Count(r => r.Route == "VPN")}  ·  DIRECT: {liveRoutesRows.Count(r => r.Route == "DIRECT")}  ·  Блокировка: {liveRoutesRows.Count(r => r.Route == "Блокировка")}  ·  Неизвестно: {liveRoutesRows.Count(r => r.Route == "Неизвестно")}  ·  Показано: {rows.Length}";
        LiveBypassStatus.Text = core.CurrentTunExclusions.Count == 0 ? "Обход TUN для отдельных IP не настроен."
            : "В обход TUN для всех приложений: " + string.Join(", ", core.CurrentTunExclusions) + ". Эти соединения ядро не перехватывает.";
        LiveRoutesEmpty.Visibility = rows.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
    private static ScrollViewer? FindLiveScroll(DependencyObject parent)
    {
        for (int i = 0; i < System.Windows.Media.VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(parent, i);
            if (child is ScrollViewer scroll) return scroll;
            if (FindLiveScroll(child) is { } nested) return nested;
        }
        return null;
    }
}
