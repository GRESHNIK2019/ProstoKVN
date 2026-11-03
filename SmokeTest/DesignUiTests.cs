using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
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

    private static IEnumerable<T> VisualChildren<T>(DependencyObject element)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(element); i++)
        {
            var child = VisualTreeHelper.GetChild(element, i);
            if (child is T typed) yield return typed;
            foreach (var nested in VisualChildren<T>(child))
                yield return nested;
        }
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
                    Theme = Environment.GetEnvironmentVariable("PROSTOKVN_PREVIEW_THEME") == "dark"
                        ? "dark" : "light",
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
                var subscription = (Expander)window.FindName("SubscriptionSection");
                var routing = (Expander)window.FindName("RoutingSection");
                var updates = (Expander)window.FindName("UpdateSection");
                Require(!subscription.IsExpanded && routing.IsExpanded && !updates.IsExpanded,
                    "Settings use compact sections by default");
                Require(((ComboBox)window.FindName("ModeBox")).IsVisible,
                    "Network routing settings remain available");
                double collapsedSettingsHeight = subscription.ActualHeight;
                subscription.IsExpanded = true;
                window.UpdateLayout();
                Require(subscription.ActualHeight > collapsedSettingsHeight + 50
                    && ((TextBox)window.FindName("SubscriptionBox")).IsVisible,
                    "Subscription section expands and preserves input fields");
                subscription.IsExpanded = false;
                window.UpdateLayout();
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, Path.ChangeExtension(preview, ".settings.png"));
                tabs.SelectedIndex = 1;
                window.UpdateLayout();
                var addRule = (Expander)window.FindName("AddRuleSection");
                var ruleGroups = (Expander)window.FindName("RuleGroupsSection");
                var probe = (Expander)window.FindName("ProbeSection");
                Require(!addRule.IsExpanded && ruleGroups.IsExpanded && !probe.IsExpanded,
                    "Routes page opens with compact forms and visible service list");
                var rulesList = (ListBox)window.FindName("RulesList");
                Require(rulesList.IsVisible,
                    "Existing rule editor accessible on Routes tab");
                var groups = VisualChildren<Expander>(ruleGroups)
                    .Where(x => x.DataContext is System.Windows.Data.CollectionViewGroup).ToList();
                Require(groups.Count > 0, "Grouped rules have expandable service panels");
                groups[0].IsExpanded = true;
                window.UpdateLayout();
                var ruleRow = VisualChildren<ListBoxItem>(ruleGroups)
                    .FirstOrDefault(x => x.DataContext is RouteRule);
                Require(ruleRow != null, "Rule list renders selectable rows");
                ruleRow!.ApplyTemplate();
                var marker = (Border)ruleRow.Template.FindName("RuleSelectedMarker", ruleRow);
                var frame = (Border)ruleRow.Template.FindName("RuleRowFrame", ruleRow);
                Require(marker != null && frame != null,
                    "Rule highlight includes accent marker and frame");
                rulesList.SelectedItem = null;
                ruleRow.IsSelected = false;
                window.UpdateLayout();
                var before = (frame!.Background as SolidColorBrush)?.Color;
                var otherRow = VisualChildren<ListBoxItem>(ruleGroups)
                    .FirstOrDefault(x => x.DataContext is RouteRule && !ReferenceEquals(x, ruleRow));
                Require(otherRow != null, "Multiple rule rows available to test right-click targeting");
                var otherRule = (RouteRule)otherRow!.DataContext;
                rulesList.SelectedItem = otherRule;
                window.UpdateLayout();
                var rightClick = window.GetType().GetMethod("RuleItem_RightButtonDown",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Require(rightClick != null, "Right-click handler is connected");
                rightClick!.Invoke(window, new object?[] { ruleRow, null });
                window.UpdateLayout();
                Require(ReferenceEquals(rulesList.SelectedItem, ruleRow.DataContext),
                    "Right-click selects the clicked rule");
                Require(marker!.Visibility == Visibility.Visible,
                    "Selected rule has a visible accent stripe");
                Require(frame.Background is SolidColorBrush selectedBackground
                    && selectedBackground.Color != before,
                    "Selected rule background contrasts with ordinary rows");
                Require(((TextBlock)window.FindName("SelectedRuleHint")).Text.Contains(
                    ((RouteRule)ruleRow.DataContext).Domain),
                    "Selection summary identifies the clicked rule");
                Require(((Button)window.FindName("RemoveRuleButton")).IsEnabled,
                    "Delete action is available only when a rule is selected");
                Require(ruleRow.ContextMenu != null
                    && ruleRow.ContextMenu.Items.OfType<MenuItem>().Any(m =>
                        m.Header?.ToString() == "Удалить правило"),
                    "Right-click opens a context menu with delete action");
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, Path.ChangeExtension(preview, ".selected.routes.png"));
                var selectedRule = (RouteRule)ruleRow.DataContext;
                var deleteItem = ruleRow.ContextMenu!.Items.OfType<MenuItem>()
                    .First(m => m.Header?.ToString() == "Удалить правило");
                var confirmField = window.GetType().GetField("confirmDeleteOverride",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Require(confirmField != null, "Delete confirmation is testable without live settings");
                confirmField!.SetValue(window, new Func<RouteRule, bool>(_ => false));
                deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                Require(rulesList.Items.Contains(selectedRule),
                    "Cancelling context-menu delete keeps the selected rule");
                confirmField.SetValue(window, new Func<RouteRule, bool>(_ => true));
                deleteItem.RaiseEvent(new RoutedEventArgs(MenuItem.ClickEvent));
                window.UpdateLayout();
                Require(!rulesList.Items.Contains(selectedRule)
                    && !UserStore.LoadRules().Any(r => r.Domain == selectedRule.Domain),
                    "Confirmed context-menu delete persists removal in isolated profile");
                Require(UserStore.LoadRules().Any(r => r.Domain == otherRule.Domain),
                    "Deleting the right-clicked rule keeps the previously selected rule");
                Require(!((Button)window.FindName("RemoveRuleButton")).IsEnabled,
                    "Delete action disables after the selected rule is removed");
                groups[0].IsExpanded = false;
                window.UpdateLayout();
                double collapsedRuleHeight = addRule.ActualHeight;
                addRule.IsExpanded = true;
                window.UpdateLayout();
                Require(addRule.ActualHeight > collapsedRuleHeight + 50
                    && ((TextBox)window.FindName("DomainBox")).IsVisible,
                    "Add rule section expands without losing the editor");
                addRule.IsExpanded = false;
                window.UpdateLayout();
                Require(addRule.ActualHeight <= collapsedRuleHeight + 4,
                    "Collapsed add-rule panel returns to compact height");
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, Path.ChangeExtension(preview, ".routes.png"));
                tabs.SelectedIndex = 3;
                window.UpdateLayout();
                var log = (Expander)window.FindName("LogSection");
                var logHelp = (Expander)window.FindName("LogHelpSection");
                Require(log.IsExpanded && !logHelp.IsExpanded && ((TextBox)window.FindName("LogBox")).IsVisible,
                    "Journal has a compact collapsible help section and visible log");
                log.IsExpanded = false;
                window.UpdateLayout();
                Require(!((TextBox)window.FindName("LogBox")).IsVisible,
                    "Journal content hides when section is collapsed");
                log.IsExpanded = true;
                window.UpdateLayout();
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, Path.ChangeExtension(preview, ".journal.png"));
                window.Width = 750;
                window.Height = 485;
                tabs.SelectedIndex = 2;
                window.UpdateLayout();
                Require(window.ActualWidth >= 749 && subscription.ActualHeight < 60
                    && updates.ActualHeight < 60,
                    "Compact settings remain usable at 750x485 minimum window");
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, Path.ChangeExtension(preview, ".small.settings.png"));
                tabs.SelectedIndex = 1;
                window.UpdateLayout();
                Require(addRule.ActualHeight < 60 && probe.ActualHeight < 60,
                    "Collapsed route forms stay compact at minimum window size");
                if (!string.IsNullOrWhiteSpace(preview))
                    SavePreview(window, Path.ChangeExtension(preview, ".small.routes.png"));
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
