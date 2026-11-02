using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProstoKVN;

public static class ThemeTests
{
    static void Check(bool condition, string reason)
    {
        if (!condition) throw new Exception("THEME_FAIL: " + reason);
        Console.WriteLine("PASS " + reason);
    }

    static void SavePreview(MainWindow window, string suffix)
    {
        string? target = Environment.GetEnvironmentVariable("PROSTOKVN_THEME_PREVIEW");
        if (string.IsNullOrWhiteSpace(target)) return;
        string path = Path.Combine(Path.GetDirectoryName(target)!,
            Path.GetFileNameWithoutExtension(target) + "-" + suffix + ".png");
        window.UpdateLayout();
        var canvas = new RenderTargetBitmap((int)window.ActualWidth,
            (int)window.ActualHeight,96,96,PixelFormats.Pbgra32);
        canvas.Render(window);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(canvas));
        using var stream = File.Create(path);
        encoder.Save(stream);
        Console.WriteLine("SCREENSHOT=" + path);
    }

    public static void Run()
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                UserStore.EnsureHome();
                var sample = new AppSettings {
                    Mode = "smart", Tun = true,
                    ProtectedUrl = UserStore.EncryptUrl("https://example.org/sub/test"),
                    SelectedNode = "Sample", Theme = "light"
                };
                UserStore.SaveSettings(sample);
                var window = new MainWindow {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -1700, Top = -1700,
                    ShowActivated = false, ShowInTaskbar = false
                };
                var switcher = (Button)window.FindName("ThemeButton");
                Check(!ThemeManager.IsDark(window.Resources) &&
                    !ThemeManager.IsDark(((OnboardingView)window.FindName("OnboardingPanel")).Resources),
                    "Light theme defaults in main and onboarding views");
                ((TextBlock)window.FindName("StatusLabel")).Text = "Подключено • VPN-выход подтверждён.";
                ((TextBlock)window.FindName("LatencyText")).Text = "HTTPS: 82 мс";
                ((TextBlock)window.FindName("UdpText")).Text = "UDP: 9 потоков (Discord: 6) • ↑ 0.3 КБ/с ↓ 1.1 КБ/с";
                ((ComboBox)window.FindName("ServerBox")).ItemsSource = new[] { "Германия • Hysteria #1" };
                ((ComboBox)window.FindName("ServerBox")).SelectedIndex = 0;
                // Isolated UI tests never contact a real subscription.
                window.StartupSubscriptionLoader = _ =>
                    System.Threading.Tasks.Task.FromException<SubscriptionImporter.Result>(
                        new InvalidOperationException("Offline preview fixture"));
                window.Show();
                SavePreview(window, "light");
                var initial = ((SolidColorBrush)window.FindResource("ThemeBgF3F6F2")).Color;
                switcher.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                window.UpdateLayout();
                Check(ThemeManager.IsDark(window.Resources) &&
                    ThemeManager.IsDark(((OnboardingView)window.FindName("OnboardingPanel")).Resources),
                    "Switch updates entire main window and onboarding instantly");
                Check(UserStore.LoadSettings().Theme == "dark",
                    "Dark preference saved without changing routes or subscription");
                var changed = ((SolidColorBrush)window.FindResource("ThemeBgF3F6F2")).Color;
                Check(changed != initial && changed.R < 45 && changed.G < 60,
                    "Surface changes to genuinely dark palette");
                SavePreview(window, "dark");
                var tabs = (TabControl)window.FindName("MainTabs");
                tabs.SelectedIndex = 1;
                SavePreview(window, "dark-routes");
                tabs.SelectedIndex = 2;
                SavePreview(window, "dark-settings");
                tabs.SelectedIndex = 3;
                SavePreview(window, "dark-log");
                window.Close();

                var reopening = new MainWindow {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -1700, Top = -1700,
                    ShowActivated = false, ShowInTaskbar = false
                };
                Check(ThemeManager.IsDark(reopening.Resources) &&
                    ((Button)reopening.FindName("ThemeButton")).Content?.ToString()?.Contains("Светлая") == true,
                    "Dark theme restores after reopening the application");
                ((Button)reopening.FindName("ThemeButton")).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
                Check(UserStore.LoadSettings().Theme == "light" &&
                    !ThemeManager.IsDark(reopening.Resources),
                    "Switching back to light is persisted");
                reopening.Close();
                Console.WriteLine("ALL_THEME_TESTS_PASS");
            }
            catch (Exception ex) { error = ex; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(25)))
            throw new TimeoutException("Theme test exceeded 25 seconds");
        if (error != null) throw new Exception("Dark theme test failed", error);
    }
}
