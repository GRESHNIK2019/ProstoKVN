using System;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ProstoKVN;

public static class HeroRadarLayoutTests
{
    static void Check(bool ok, string reason)
    {
        if (!ok) throw new InvalidOperationException("RADAR_LAYOUT_FAILED: " + reason);
        Console.WriteLine("PASS " + reason);
    }
    static Rect Bounds(FrameworkElement element, Window window)
        => element.TransformToAncestor(window)
            .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
    static void SavePreview(Window window, string mode, int width, string scenario)
    {
        string? dir = Environment.GetEnvironmentVariable("PROSTOKVN_RADAR_PREVIEW");
        if (string.IsNullOrWhiteSpace(dir)) return;
        Directory.CreateDirectory(dir);
        var bmp = new RenderTargetBitmap((int)window.ActualWidth,
            (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bmp.Render(window);
        var png = new PngBitmapEncoder();
        png.Frames.Add(BitmapFrame.Create(bmp));
        string filename = Path.Combine(dir, $"radar-status-{mode}-{width}-{scenario}.png");
        using var stream = File.Create(filename);
        Console.WriteLine("SCREENSHOT " + filename);
        png.Save(stream);
    }
    public static void Run()
    {
        Exception? failed = null;
        var thread = new Thread(() =>
        {
            try
            {
                UserStore.EnsureHome();
                UserStore.SaveSettings(new AppSettings { Theme = "dark", Mode = "smart",
                    ProtectedUrl = UserStore.EncryptUrl("https://example.test/sub/demo") });
                var window = new MainWindow {
                    WindowStartupLocation = WindowStartupLocation.Manual,
                    Left = -1700, Top = -1700,
                    ShowInTaskbar = false, ShowActivated = false
                };
                window.StartupSubscriptionLoader = _ =>
                    System.Threading.Tasks.Task.FromException<SubscriptionImporter.Result>(
                        new InvalidOperationException("Network disabled in visual test"));
                var label = (TextBlock)window.FindName("StatusLabel");
                var radar = (Viewbox)window.FindName("RadarViewport");
                var textColumn = (StackPanel)window.FindName("HeroTextColumn");
                var body = (Grid)window.FindName("HeroContentGrid");
                window.Show();
                string[] statuses = {
                    "Подключено • VPN-выход подтверждён.",
                    "Соединение запущено • VPN-выход не подтверждён.",
                    "Не удалось проверить соединение"
                };
                foreach (string mode in new[] { "dark", "light" })
                {
                    if (mode == "light")
                        ((Button)window.FindName("ThemeButton"))
                            .RaiseEvent(new RoutedEventArgs(System.Windows.Controls.Primitives.ButtonBase.ClickEvent));
                    foreach (int width in new[] { 890, 750 })
                    {
                        window.Width = width;
                        foreach (int i in new[] { 0, 1, 2 })
                        {
                            label.Text = statuses[i];
                            window.UpdateLayout();
                            Rect text = Bounds(label, window);
                            Rect picture = Bounds(radar, window);
                            Rect stack = Bounds(textColumn, window);
                            Rect card = Bounds(body, window);
                            Check(text.Right + 4 <= picture.Left &&
                                  stack.Right + 4 <= picture.Left,
                                  $"{mode} {width} status {i} never intersects radar");
                            Check(text.Top >= card.Top - 1 && text.Bottom <= card.Bottom + 1 &&
                                text.Height > 10 && picture.Width > 100,
                                  $"{mode} {width} status {i} remains readable inside panel");
                            if (i == 0)
                                SavePreview(window, mode, width, "connected");
                        }
                    }
                }
                window.Close();
                Console.WriteLine("ALL_RADAR_LAYOUT_TESTS_PASS");
            }
            catch (Exception e) { failed = e; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!thread.Join(TimeSpan.FromSeconds(25)))
            throw new TimeoutException("Radar layout test timed out");
        if (failed != null) throw new Exception("Radar layout test failed", failed);
    }
}
