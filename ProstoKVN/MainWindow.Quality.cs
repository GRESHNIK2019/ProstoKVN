using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace ProstoKVN;

public partial class MainWindow
{
    private readonly ConnectionQualityHistory qualityHistory = new();
    private readonly DispatcherTimer qualityTimer = new() { Interval = TimeSpan.FromSeconds(5) };
    private CancellationTokenSource qualityCancellation = new();
    private bool qualityActive;
    private bool qualityBusy;
    private int qualityRevision;
    private MonitorSnapshot? qualityLastUdp;
    // For isolated UI tests. Never contacts a user VPN when injected.
    public Func<CancellationToken, Task<int?>>? QualityProbeOverride { get; set; }

    private void InitializeQualityDiagnostics()
    {
        qualityTimer.Tick += async (_, _) => await SampleQualityAsync();
        UpdateQualityUi();
    }

    private void QualityToggle_Click(object sender, RoutedEventArgs e)
    {
        if (qualityActive)
        {
            StopQualityDiagnostics();
            return;
        }
        if (!core.IsTransportHealthy || appliedMode == "direct")
        {
            QualityStatusText.Text = "Для проверки подключитесь к VPN.";
            return;
        }
        qualityHistory.Clear();
        qualityRevision++;
        qualityCancellation.Cancel();
        qualityCancellation.Dispose();
        qualityCancellation = new CancellationTokenSource();
        qualityActive = true;
        qualityTimer.Start();
        UpdateQualityUi();
        _ = SampleQualityAsync();
    }

    private void StopQualityDiagnostics(bool completed = false)
    {
        qualityActive = false;
        qualityTimer.Stop();
        qualityRevision++;
        qualityCancellation.Cancel();
        QualityStatusText.Text = completed
            ? "Сеанс диагностики завершён (10 минут). История сохранена до закрытия приложения."
            : "Диагностика остановлена. История сохранена до следующего запуска.";
        UpdateQualityUi(updateStatus: false);
    }

    private void QualityClear_Click(object sender, RoutedEventArgs e)
    {
        qualityHistory.Clear();
        UpdateQualityUi();
    }

    private async Task SampleQualityAsync()
    {
        if (!qualityActive || qualityBusy || !core.IsTransportHealthy) return;
        if (qualityHistory.Attempts >= ConnectionQualityHistory.MaximumSamples)
        {
            StopQualityDiagnostics(completed: true);
            return;
        }
        qualityBusy = true;
        int revision = qualityRevision;
        int connection = connectionRevision;
        var token = qualityCancellation.Token;
        try
        {
            int? ms = QualityProbeOverride != null
                ? await QualityProbeOverride(token)
                : await core.ProbeVpnHttpsDelayAsync(token);
            if (token.IsCancellationRequested || revision != qualityRevision
                || connection != connectionRevision || !qualityActive || isWindowClosing)
                return;
            qualityHistory.Add(DateTime.UtcNow, ms);
            UpdateQualityUi();
            if (qualityHistory.Attempts >= ConnectionQualityHistory.MaximumSamples)
                StopQualityDiagnostics(completed: true);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        catch (Exception ex)
        {
            if (!token.IsCancellationRequested && revision == qualityRevision
                && connection == connectionRevision && qualityActive && !isWindowClosing)
            {
                qualityHistory.Add(DateTime.UtcNow, null);
                UpdateQualityUi();
                AppendLog("Диагностическая HTTPS-проба: " + ex.GetType().Name);
            }
        }
        finally { qualityBusy = false; }
    }

    private void ResetQualityForNewConnection()
    {
        qualityRevision++;
        qualityCancellation.Cancel();
        qualityCancellation.Dispose();
        qualityCancellation = new CancellationTokenSource();
        qualityHistory.Clear();
        qualityLastUdp = null;
        UpdateQualityUi();
    }

    private void QualityUpdateUdp(MonitorSnapshot snapshot)
    {
        qualityLastUdp = snapshot;
        QualityUdpRoutesText.Text = "UDP Discord по данным Mihomo: через VPN — "
            + snapshot.DiscordVpnUdpConnections + ", напрямую — "
            + snapshot.DiscordDirectUdpConnections + ", маршрут неизвестен — "
            + snapshot.DiscordUnknownUdpConnections + ".";
    }

    private void QualityCopy_Click(object sender, RoutedEventArgs e)
    {
        var average = qualityHistory.AverageMs?.ToString() ?? "нет данных";
        var peak = qualityHistory.MaximumMs?.ToString() ?? "нет данных";
        var variation = qualityHistory.VariationMs?.ToString() ?? "нет данных";
        string report = "ProstoKVN: диагностический отчёт\n"
            + "HTTPS-пробы через выбранный VPN, не задержка голоса Discord.\n"
            + $"Успешных: {qualityHistory.Successful}/{qualityHistory.Attempts}\n"
            + $"Средняя HTTPS-задержка: {average} мс\n"
            + $"Пик HTTPS-задержки: {peak} мс\n"
            + $"Среднее изменение соседних проб: {variation} мс\n"
            + $"Неудачные HTTPS-пробы: {qualityHistory.Failures} ({qualityHistory.FailurePercent}%)\n"
            + $"Discord UDP (VPN/DIRECT/неизвестно): "
            + $"{qualityLastUdp?.DiscordVpnUdpConnections ?? 0}/"
            + $"{qualityLastUdp?.DiscordDirectUdpConnections ?? 0}/"
            + $"{qualityLastUdp?.DiscordUnknownUdpConnections ?? 0}\n"
            + "Фактические потери UDP-пакетов и голосовой джиттер не измерены.";
        try
        {
            Clipboard.SetText(report);
            QualityStatusText.Text = "Отчёт без адресов и данных подписки скопирован.";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            QualityStatusText.Text = "Не удалось скопировать отчёт в буфер обмена.";
        }
    }

    private void QualityCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
        => DrawQualityGraph();

    private void UpdateQualityUi(bool updateStatus = true)
    {
        if (QualityStartButton == null) return;
        QualityStartButton.Content = qualityActive ? "Остановить диагностику" : "Начать диагностику";
        QualityStartButton.IsEnabled = qualityActive
            || (core.IsTransportHealthy && appliedMode != "direct");
        QualityCopyButton.IsEnabled = qualityHistory.Attempts > 0;
        QualityResetButton.IsEnabled = qualityHistory.Attempts > 0;
        QualityCurrentText.Text = qualityHistory.LatestMs is int latest
            ? latest + " мс" : "—";
        QualityVariationText.Text = qualityHistory.VariationMs is int variation
            ? variation + " мс" : "—";
        QualityPeakText.Text = qualityHistory.MaximumMs is int peak
            ? peak + " мс" : "—";
        QualityFailuresText.Text = qualityHistory.Attempts == 0 ? "—"
            : $"{qualityHistory.FailurePercent}% ({qualityHistory.Failures}/{qualityHistory.Attempts})";
        QualityGraphHint.Text = $"HTTPS-проверки: {qualityHistory.Successful}/{qualityHistory.Attempts}"
            + (qualityHistory.AverageMs is int average ? $" · среднее {average} мс" : "")
            + " · история до 10 минут";
        if (updateStatus)
            QualityStatusText.Text = qualityActive
                ? "Проверка каждые 5 секунд через выбранный VPN. Голосовой UDP не измеряется."
                : qualityHistory.Attempts == 0
                    ? "Подключитесь к VPN и нажмите «Начать диагностику»."
                    : "Диагностика остановлена. Последние измерения сохранены в памяти.";
        DrawQualityGraph();
    }

    private void DrawQualityGraph()
    {
        if (QualityCanvas == null || QualityCanvas.ActualWidth <= 1) return;
        var canvas = QualityCanvas;
        canvas.Children.Clear();
        var samples = qualityHistory.Samples;
        if (samples.Count == 0) return;
        double width = canvas.ActualWidth;
        double height = Math.Max(80, canvas.ActualHeight);
        double top = 10, bottom = height - 12;
        double maximum = Math.Max(100, samples.Where(s => s.HttpsLatencyMs.HasValue)
            .Select(s => s.HttpsLatencyMs!.Value).DefaultIfEmpty(0).Max() * 1.15);
        Brush ink = TryFindResource("AccentSolid") as Brush ?? Brushes.MediumPurple;
        Brush muted = TryFindResource("TextMuted") as Brush ?? Brushes.Gray;
        for (int i = 1; i <= 3; i++)
        {
            double y = top + (bottom - top) * i / 4;
            canvas.Children.Add(new Line
            {
                X1 = 0, X2 = width, Y1 = y, Y2 = y,
                Stroke = muted, StrokeThickness = 0.6, Opacity = 0.24
            });
        }
        double step = width / (ConnectionQualityHistory.MaximumSamples - 1);
        double start = width - step * Math.Max(0, samples.Count - 1);
        Polyline? line = null;
        for (int i = 0; i < samples.Count; i++)
        {
            double x = start + i * step;
            if (samples[i].HttpsLatencyMs is not int latency)
            {
                line = null; // Failed HTTPS probe produces a visible gap, never a fake RTT.
                canvas.Children.Add(new Line
                {
                    X1 = x, X2 = x, Y1 = bottom - 5, Y2 = bottom,
                    Stroke = Brushes.IndianRed, StrokeThickness = 2
                });
                continue;
            }
            double y = bottom - (bottom - top) * Math.Min(1, latency / maximum);
            if (line == null)
            {
                line = new Polyline
                {
                    Stroke = ink, StrokeThickness = 2.4,
                    StrokeLineJoin = PenLineJoin.Round
                };
                canvas.Children.Add(line);
            }
            line.Points.Add(new Point(x, y));
            if (line.Points.Count == 1)
            {
                canvas.Children.Add(new System.Windows.Shapes.Ellipse
                {
                    Width = 3, Height = 3, Fill = ink,
                    Margin = new Thickness(x - 1.5, y - 1.5, 0, 0)
                });
            }
        }
    }
}
