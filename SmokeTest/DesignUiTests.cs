using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProstoKVN;

public static class DesignUiTests
{
    static void Require(bool value, string reason)
    {
        if (!value) throw new Exception("DESIGN_UI_FAIL: " + reason);
        Console.WriteLine("PASS " + reason);
    }

    private static void SavePreview(Window window, string path)
    {
        var bmp = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight,
            96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var stream = File.Create(path);
        png.Save(stream);
        Console.WriteLine("PREVIEW=" + path);
    }

    public static void Run()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                // Isolated data; never import or interact with the user's live session.
                UserStore.EnsureHome();
                var settings = new AppSettings {
                    Mode = "smart", Tun = true, SelectedNode = "Test Germany",
                    ProtectedUrl = UserStore.EncryptUrl("https://example.org/sub/test")
                };
                UserStore.SaveSettings(settings);
                var window = new MainWindow {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -1800, Top = -1800,
                    ShowInTaskbar = false,
                    ShowActivated = false
                };
                var tabs = (TabControl)window.FindName("MainTabs");
                Require(window.Width >= 820 && tabs.Items.Count == 4,
                    "New four-tab navigation and desktop canvas");
                foreach (string id in new[] {
                    "StatusLabel","ServerBox","EngineInfoLabel","ConnectButton",
                    "DisconnectButton","RefreshButton","RecoveryText","LatencyText",
                    "UdpText","SubscriptionBox","ImportButton","ImportSummary",
                    "ModeBox","AutoLearnBox","TunBox","RulesList","RuleNodeBox",
                    "DomainBox","ProbeBox","ProbeResult","LogBox"
                })
                    Require(window.FindName(id) != null, "Preserved action " + id);
                Require(((TabItem)tabs.Items[0]).Header?.ToString() == "Обзор"
                    && ((TabItem)tabs.Items[2]).Header?.ToString() == "Настройки",
                    "Overview and settings separated");

                ((TextBlock)window.FindName("StatusLabel")).Text = "Подключено • VPN-выход подтверждён.";
                ((TextBlock)window.FindName("RecoveryText")).Text = "VPN работает, контроль каждые 12 секунд";
                ((TextBlock)window.FindName("LatencyText")).Text = "HTTPS: 91 мс";
                ((TextBlock)window.FindName("UdpText")).Text = "UDP: 9 потоков (Discord: 6) • ↑ 0.0 КБ/с ↓ 1.1 КБ/с";
                ((ComboBox)window.FindName("ServerBox")).ItemsSource = new[] { "Германия • Hysteria #1", "Нидерланды • Xray" };
                ((ComboBox)window.FindName("ServerBox")).SelectedIndex = 0;
                // Isolated UI tests never contact a real subscription.
                window.StartupSubscriptionLoader = _ =>
                    System.Threading.Tasks.Task.FromException<SubscriptionImporter.Result>(
                        new InvalidOperationException("Offline preview fixture"));
                window.Show();
                window.UpdateLayout();
                var size = new Size(window.ActualWidth, window.ActualHeight);
                Require(size.Width > 700 && size.Height > 450, "Layout has non-zero visual size");

                string? preview = Environment.GetEnvironmentVariable("PROSTOKVN_DESIGN_PREVIEW");
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, preview);

                tabs.SelectedIndex = 2;
                window.UpdateLayout();
                Require(((ComboBox)window.FindName("ModeBox")).IsVisible,
                    "Network and subscription controls available on Settings tab");
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, Path.ChangeExtension(preview, ".settings.png"));
                tabs.SelectedIndex = 1;
                window.UpdateLayout();
                Require(((ListBox)window.FindName("RulesList")).IsVisible,
                    "Existing rule editor accessible on Routes tab");
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, Path.ChangeExtension(preview, ".routes.png"));
                window.Close();
                Console.WriteLine("ALL_DESIGN_UI_TESTS_PASS");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(22)))
            throw new TimeoutException("Design UI smoke test timed out");
        if (error != null) throw new Exception("Design UI test failed: " + error);
    }
}
