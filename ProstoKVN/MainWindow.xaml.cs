using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ProstoKVN;

public partial class MainWindow : Window
{
    private readonly VpnCore core = new();
    private readonly ConnectionMetrics meter = new();
    private readonly RecoveryPolicy recovery = new();
    private readonly DispatcherTimer monitorTimer = new() { Interval = TimeSpan.FromSeconds(12) };
    private bool monitorBusy;
    private int monitorTicks;
    private bool vpnExitVerified;
    private bool vpnVerificationBusy;
    private DateTime lastVpnVerificationUtc = DateTime.MinValue;
    private int connectionRevision;
    private readonly SmartRouteLearner learner = new();
    private readonly DispatcherTimer learnTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly DispatcherTimer learnedRulesBatchTimer = new()
    {
        Interval = TimeSpan.FromSeconds(8)
    };
    private readonly System.Threading.SemaphoreSlim reloadGate = new(1, 1);
    private readonly System.Threading.SemaphoreSlim connectionGate = new(1, 1);
    private bool shutdownFinalized;
    private int consecutiveFailedVpnChecks;
    private bool pendingLearnedRules;
    private bool learnedReloadBusy;
    private AppSettings settings = new();
    private List<RouteRule> rules = new();
    private RouteRule? editingRule;
    private bool applicationRulePage;
    private string siteRuleQuery = "", applicationRuleQuery = "";
    private List<ProxyNode> nodes = new();
    private List<SubscriptionProfile> profiles = new();
    private readonly Dictionary<string, List<ProxyNode>> profileNodes = new();
    private bool suppressProfileSelection;
    // Isolated UI tests can accept deletion without a modal dialog.
    private Func<SubscriptionProfile, bool>? confirmRemoveSubscriptionOverride = null;
    private bool importing, testing;
    private bool firstRunScreenHandled;
    private bool startupSubscriptionLoadStarted;
    private bool isWindowClosing;
    private bool checkingUpdates;
    private bool connectionBusy;
    private string? appliedMode;
    private AppSettings? activeSettings;
    private bool rulesNeedReconnect;
    private string? rulesApplyError;
    private bool appliedTun;
    private string appliedTunStack = "mixed";
    private bool appliedTunDnsHijack = true;
    private bool appliedTunStrictRoute;
    private GitHubUpdate? readyUpdate;
    // Injectable to test startup without contacting a real subscription server.
    public Func<string, Task<SubscriptionImporter.Result>> StartupSubscriptionLoader { get; set; }
        = url => SubscriptionImporter.LoadAsync(url);
    private readonly Queue<string> observedTraffic = new();
    private readonly HashSet<string> observedQueued = new(StringComparer.OrdinalIgnoreCase);
    public MainWindow()
    {
        InitializeComponent();
        InitializeQualityDiagnostics();
        InitializeLiveRoutes();
        // Keep the first window inside the usable desktop at high display scaling.
        var workArea = SystemParameters.WorkArea;
        MinWidth = Math.Min(MinWidth, Math.Max(400, workArea.Width - 32));
        MinHeight = Math.Min(MinHeight, Math.Max(360, workArea.Height - 32));
        Width = Math.Max(MinWidth, Math.Min(Width, workArea.Width - 32));
        Height = Math.Max(MinHeight, Math.Min(Height, workArea.Height - 32));
        var appVersion = typeof(MainWindow).Assembly.GetName().Version;
        string release = appVersion == null ? "dev" : $"{appVersion.Major}.{appVersion.Minor}"
            + (appVersion.Build > 0 ? $".{appVersion.Build}" : "");
        Title = "ProstoKVN " + release;
        VersionText.Text = release;
        OnboardingPanel.SetupCompleted += (_, imported) =>
        {
            ApplyImportedSubscription(imported);
            HideOnboarding();
        };
        OnboardingPanel.Skipped += (_, _) => HideOnboarding();
        ContentRendered += (_, _) =>
        {
            if (!firstRunScreenHandled && !SubscriptionImporter.IsValidUrl(SubscriptionBox.Text))
            {
                ShowOnboarding();
                if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")))
                    _ = CheckUpdatesAsync(false);
                return;
            }
            // Load only the saved encrypted subscription, asynchronously.
            // Displaying the server list never connects or restarts the VPN.
            _ = LoadSavedSubscriptionAsync();
            // UI tests use a separate PROSTOKVN_TEST_HOME; never contact GitHub from tests.
            if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("PROSTOKVN_TEST_HOME")))
                _ = CheckUpdatesAsync(false);
        };
        settings = UserStore.LoadSettings();
        settings.Mode = RoutingPolicy.NormalizeMode(settings.Mode);
        ApplyTheme(settings.Theme, save: false);
        rules = UserStore.LoadRules();
        var modeUpgrade = UserStore.UpgradeRoutingModes(settings, rules);
        settings = modeUpgrade.Settings;
        rules = modeUpgrade.Rules;
        ModeMigrationNotice.Text = modeUpgrade.Notice;
        // Earlier versions created trial VPN rules without a working VPN response.
        // Retire only those unconfirmed automatic routes, retaining a local backup.
        int retiredTrials = rules.Count(r => !r.Manual && r.Provisional);
        if (retiredTrials > 0)
        {
            string source = System.IO.Path.Combine(UserStore.Home, "routes.json");
            if (System.IO.File.Exists(source))
                System.IO.File.Copy(source, System.IO.Path.Combine(UserStore.Home,
                    "routes.before-verified-learning-" + DateTime.UtcNow.ToString("yyyyMMdd-HHmmssfff") + ".json"));
            rules.RemoveAll(r => !r.Manual && r.Provisional);
            UserStore.SaveRules(rules);
        }

        // Old versions misclassified .ru CDN HTTP 403 challenges as VPN blocks.
        // Restore DIRECT for learned .ru domains only, never touch manual choices.
        int removedRu = RuDomainPolicy.RemoveAutomaticRoutes(rules);
        string ruBackup = "";
        if (removedRu > 0)
        {
            ruBackup = UserStore.BackupRoutesBeforeRuCleanup();
            UserStore.SaveRules(rules);
        }
        // Seed AI service routes before the first browser/desktop login request.
        // Delayed connection sampling and HTTP 403 challenges cannot reliably
        // teach OAuth and stream endpoints in time.
        int addedAi = AiServiceRouting.EnsureDefaults(rules);
        int consolidated = ServiceRuleGrouper.CompactStableRules(rules);
        if (consolidated > 0 || addedAi > 0)
        {
            if (addedAi > 0) UserStore.BackupRoutesBeforeServicePresets();
            UserStore.SaveRules(rules);
        }
        profiles = SubscriptionProfileStore.Load(settings);
        RefreshSubscriptionList(profiles.FirstOrDefault());
        AutoLearnBox.IsChecked = settings.AutoLearn;
        if (settings.Mode == "mine") settings.Tun = true;
        TunBox.IsChecked = settings.Tun;
        TunBox.IsEnabled = settings.Mode != "mine";
        TunStackBox.SelectedIndex = VpnCore.NormalizeTunStack(settings.TunStack) switch
        {
            "system" => 1, "gvisor" => 2, _ => 0
        };
        TunDnsHijackBox.IsChecked = settings.TunDnsHijack;
        TunStrictRouteBox.IsChecked = settings.TunStrictRoute;
        TunAdvancedSection.IsEnabled = settings.Tun;
        AutoLearnBox.IsEnabled = settings.Mode == "smart";
        ModeBox.SelectedValue = settings.Mode;
        UserStore.PruneExpiredRules(rules);
        RefreshRules();
        UpdateRuleCategoryButtons();
        RefreshNodeChoices();
        UpdateConnectionUi();
        core.OnLog += msg => Dispatcher.BeginInvoke(() => { AppendLog(msg); ObserveDomainFromLog(msg); });
        learner.Log += msg => Dispatcher.BeginInvoke(() => AppendLog(msg));
        learnedRulesBatchTimer.Tick += async (_, _) =>
        {
            learnedRulesBatchTimer.Stop();
            await FlushLearnedRulesAsync();
        };
        learner.Learned += _ => Dispatcher.BeginInvoke(() =>
        {
            RefreshRules();
            ScheduleLearnedRulesApply();
        });
        learnTimer.Tick += async (_, _) => await LearnTickAsync();
        learnTimer.Start();
        monitorTimer.Tick += async (_, _) => await MonitorTickAsync();
        monitorTimer.Start();
        Closing += async (_, args) =>
        {
            if (shutdownFinalized) return;
            if (closeToTrayEnabled && !exitRequested)
            {
                args.Cancel = true;
                HideToTray();
                return;
            }
            // Delay closing until an in-flight StartAsync releases the gate,
            // otherwise the core can turn the system proxy back on after exit.
            args.Cancel = true;
            if (isWindowClosing) return;
            isWindowClosing = true;
            liveRoutesTimer.Stop();
            liveRoutesCancellation.Cancel();
            ResetServerLatencySession();
            StopQualityDiagnostics();
            recovery.Disable();
            ++connectionRevision;
            monitorTimer.Stop();
            learnTimer.Stop();
            learnedRulesBatchTimer.Stop();
            await connectionGate.WaitAsync();
            try
            {
                learner.Dispose();
                core.Dispose();
            }
            finally
            {
                shutdownFinalized = true;
                connectionGate.Release();
                // Never call Close recursively from the current Closing event.
                _ = Dispatcher.BeginInvoke(DispatcherPriority.Background, new Action(Close));
            }
        };
        Closed += (_, _) => DisposeTray();
        if (closeToTrayEnabled) InitializeTray();
        if (retiredTrials > 0) AppendLog($"Удалено {retiredTrials} неподтверждённых пробных AUTO-правил; прежние правила сохранены в резервной копии.");
        AppendLog("ProstoKVN открыт. Для TUN нужны права администратора.");
        if (removedRu > 0)
            AppendLog($"Исправлена маршрутизация .ru: убрано {removedRu} ошибочных AUTO-правил. Резервная копия: {System.IO.Path.GetFileName(ruBackup)}.");
        if (addedAi > 0)
            AppendLog($"Настроены {addedAi} маршрутов сервисов: OpenAI — VPN до первого запроса; Claude — DIRECT с проверкой доступности и переключением при необходимости. Ручные исключения выше.");
        if (consolidated > 0)
            AppendLog($"Сгруппированы совпадающие правила по сервисам: удалено {consolidated} дублирующих записей.");
    }
    private void SettingsLink_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 3;
    }

    private void MinimizeWindow_Click(object sender, RoutedEventArgs e)
        => SystemCommands.MinimizeWindow(this);

    private void MaximizeWindow_Click(object sender, RoutedEventArgs e)
    {
        if (WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(this);
        else SystemCommands.MaximizeWindow(this);
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
        => HideToTray();

    private void SubscriptionsLink_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 1;
        if (profiles.Count == 0) NewSubscription_Click(sender, e);
    }

    private void UpdateConnectionUi()
    {
        if (ConnectionTitle == null) return;
        bool active = core.IsRunning;
        bool desired = recovery.Desired;
        bool pending = HasPendingConnectionSettings;
        ConnectionTitle.Text = connectionBusy ? "Подключаемся…"
            : !active && desired ? "Восстанавливаем соединение"
            : !active ? "VPN выключен"
            : appliedMode == "direct" ? "Работает без VPN"
            : vpnExitVerified ? "VPN подключён" : "Проверяем соединение";
        ConnectionHint.Text = connectionBusy ? "Проверяем доступ через сервер."
            : !active && desired ? "Повторяем попытку подключения."
            : !active && nodes.Count == 0 ? (profiles.Count == 0
                ? "Добавьте ссылку от провайдера."
                : "Загрузите серверы из подписки.")
            : !active ? "Выберите сервер и подключитесь."
            : pending ? "Новые настройки ещё не применены."
            : appliedMode == "direct" ? "Трафик идёт напрямую."
            : appliedMode == "mine" ? "VPN только для выбранных правил. Остальной трафик — DIRECT."
            : vpnExitVerified ? "Доступ через VPN подтверждён."
            : "VPN-выход ещё не подтверждён.";
        ConnectButton.Content = connectionBusy ? "Подключаемся…" : active || desired ? "Отключиться"
            : nodes.Count == 0 && profiles.Count == 0 ? "Добавить подписку" : "Подключиться";
        DisconnectButton.Visibility = connectionBusy ? Visibility.Visible : Visibility.Collapsed;
        ConnectButton.Visibility = connectionBusy ? Visibility.Collapsed : Visibility.Visible;
        ConnectionIcon.Text = connectionBusy ? "\uE895" : active && vpnExitVerified ? "\uE73E" : "\uE7E8";
        bool confirmed = active && vpnExitVerified && appliedMode != "direct" && !connectionBusy;
        bool uncertain = !connectionBusy && (desired || active) && !confirmed && appliedMode != "direct";
        ConnectionStateBadge.SetResourceReference(Border.BackgroundProperty,
            confirmed ? "SuccessSoft" : uncertain ? "WarningSoft" : "SurfaceRaised");
        ConnectionIcon.SetResourceReference(TextBlock.ForegroundProperty,
            confirmed ? "StatusPositive" : uncertain ? "StatusWarning" : "TextMuted");
        ConnectButton.SetResourceReference(Control.BackgroundProperty,
            active || desired ? "SurfaceRaised" : "AccentSolid");
        ConnectButton.SetResourceReference(Control.ForegroundProperty,
            active || desired ? "TextPrimary" : "OnAccent");
        ConnectButton.SetResourceReference(Control.BorderBrushProperty,
            active || desired ? "BorderStrong" : "AccentSolid");
        UpdateRoutingUi();
        ServerHint.Text = nodes.Count == 0 ? "Нет серверов. Добавьте или обновите подписку."
            : active ? "При выборе другого сервера соединение переключится автоматически."
            : "Этот сервер будет использован при подключении.";
        PendingSettingsBanner.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        ApplySettingsButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        SettingsApplyHint.Text = pending
            ? "Есть изменения. Переподключитесь, чтобы применить их."
            : active ? "Настройки применены к текущему подключению."
                : "Изменения применятся при следующем подключении.";
        SubscriptionsEmptyText.Visibility = profiles.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateQualityUi(updateStatus: false);
        UpdateTrayStatus();
    }

    private async void ApplyConnectionSettings_Click(object sender, RoutedEventArgs e)
    {
        if (connectionBusy || !core.IsRunning) return;
        await ReconnectForSelectionAsync();
    }

    private void CloseSubscriptionEditor_Click(object sender, RoutedEventArgs e)
    {
        SubscriptionEditor.IsExpanded = false;
        SubscriptionsPageScroll.ScrollToTop();
        NewSubscriptionButton.Focus();
    }

    private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
    {
        if (readyUpdate != null)
            await DownloadAndInstallUpdateAsync(readyUpdate);
        else
            await CheckUpdatesAsync(true);
    }

    private async Task CheckUpdatesAsync(bool manual)
    {
        if (checkingUpdates || isWindowClosing) return;
        checkingUpdates = true;
        CheckUpdateButton.IsEnabled = false;
        if (manual) UpdateStatusText.Text = "Проверяю GitHub Releases...";
        try
        {
            var assembly = typeof(MainWindow).Assembly.GetName().Version ?? new Version(0, 0);
            var version = new Version(assembly.Major, assembly.Minor, Math.Max(0, assembly.Build));
            var found = await GitHubUpdateService.CheckAsync(version);
            if (isWindowClosing) return;
            readyUpdate = found;
            if (found == null)
            {
                UpdateStatusText.Text = "Установлена актуальная версия. Новых стабильных релизов нет.";
                CheckUpdateButton.Content = "Проверить обновления";
            }
            else
            {
                UpdateStatusText.Text = "Доступно обновление " + found.Tag;
                CheckUpdateButton.Content = "Скачать и установить " + found.Tag;
                AppendLog("Доступен официальный релиз: " + found.Tag);
            }
        }
        catch (Exception ex)
        {
            if (isWindowClosing) return;
            UpdateStatusText.Text = "Не удалось проверить обновления. " +
                "Проверь подключение к GitHub и повтори.";
            AppendLog("Проверка обновления: " + ex.GetType().Name);
        }
        finally
        {
            checkingUpdates = false;
            if (!isWindowClosing) CheckUpdateButton.IsEnabled = true;
        }
    }

    private async Task DownloadAndInstallUpdateAsync(GitHubUpdate release)
    {
        if (checkingUpdates || isWindowClosing) return;
        string oldExe = Environment.ProcessPath ?? "";
        string? installPath = System.IO.Path.GetDirectoryName(oldExe);
        if (string.IsNullOrEmpty(installPath) ||
            !System.IO.File.Exists(System.IO.Path.Combine(installPath, "ProstoKVN.exe")))
        {
            UpdateStatusText.Text = "Обновления доступны только в установленной сборке ProstoKVN.";
            return;
        }
        var result = ClientDialog.Show(this,
            $"Скачать {release.Tag} с GitHub?\n\n" +
            "После загрузки и проверки SHA-256 приложение будет закрыто, " +
            "VPN временно отключится, файлы обновятся и ProstoKVN запустится снова.\n\n" +
            "При ошибке установки обновлятор попытается восстановить предыдущую версию.\n\nПродолжить?",
            "Обновление ProstoKVN", MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result != MessageBoxResult.Yes) return;
        checkingUpdates = true;
        CheckUpdateButton.IsEnabled = false;
        try
        {
            UpdateStatusText.Text = "Скачиваю релиз с GitHub...";
            var progress = new Progress<int>(percent =>
                UpdateStatusText.Text = $"Загрузка обновления: {percent}%");
            string stage = await GitHubUpdateService.StageAsync(release, progress);
            if (isWindowClosing) return;
            string helper = System.IO.Path.Combine(installPath, "updater", "ProstoKVN.Updater.exe");
            if (!System.IO.File.Exists(helper))
                throw new InvalidOperationException("Компонент обновления не найден. Переустанови ProstoKVN.");
            string runner = System.IO.Path.Combine(stage, "runner");
            System.IO.Directory.CreateDirectory(runner);
            string helperDir = System.IO.Path.GetDirectoryName(helper)!;
            foreach (string file in System.IO.Directory.GetFiles(helperDir))
                System.IO.File.Copy(file, System.IO.Path.Combine(runner,
                    System.IO.Path.GetFileName(file)), overwrite: true);
            string copy = System.IO.Path.Combine(runner, "ProstoKVN.Updater.exe");
            var process = new System.Diagnostics.ProcessStartInfo(copy)
            {
                WorkingDirectory = runner,
                UseShellExecute = true
            };
            process.ArgumentList.Add("--replace");
            process.ArgumentList.Add(stage);
            process.ArgumentList.Add(installPath);
            process.ArgumentList.Add(Environment.ProcessId.ToString());
            UpdateStatusText.Text = "Проверено. Закрываю приложение для обновления...";
            if (System.Diagnostics.Process.Start(process) == null)
                throw new InvalidOperationException("Не удалось запустить обновлятор.");
            // No manual core.Stop() here: normal Closing cleans up TUN,
            // system proxy and sidecar only after the helper was launched.
            RequestExit();
        }
        catch (Exception ex)
        {
            UpdateStatusText.Text = "Установка не началась: " + ex.Message;
            AppendLog("Обновление: " + ex.GetType().Name);
        }
        finally
        {
            checkingUpdates = false;
            if (!isWindowClosing) CheckUpdateButton.IsEnabled = true;
        }
    }



    private void ApplyTheme(string theme, bool save)
    {
        var normalized = ThemeManager.Normalize(theme);
        ThemeManager.Apply(Resources, normalized);
        ThemeManager.Apply(OnboardingPanel.Resources, normalized);
        ThemeButton.Content = normalized == ThemeManager.Dark ? "Светлая" : "Тёмная";
        ThemeButton.ToolTip = normalized == ThemeManager.Dark
            ? "Включить светлую тему" : "Включить тёмную тему";
        settings.Theme = normalized;
        if (save) UserStore.SaveSettings(settings);
    }

    private void ThemeButton_Click(object sender, RoutedEventArgs e)
    {
        ApplyTheme(ThemeManager.Normalize(settings.Theme) == ThemeManager.Dark
            ? ThemeManager.Light : ThemeManager.Dark, save: true);
    }

    private void AppendLog(string line)
    {
        if (line.Contains("password", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("uuid", StringComparison.OrdinalIgnoreCase) ||
            line.Contains("://") && line.Contains("@")) return;
        LogBox.AppendText($"[{DateTime.Now:HH:mm:ss}] {line}\n");
        if (LogBox.Text.Length > 40000) LogBox.Text = LogBox.Text[^30000..];
        LogBox.ScrollToEnd();
    }
    private void Persist()
    {
        if (ModeBox.SelectedValue is string mode && RoutingModes.IsPrimary(mode)) settings.Mode = mode;
        settings.Tun = TunBox.IsChecked == true;
        settings.TunStack = VpnCore.NormalizeTunStack(
            (TunStackBox.SelectedItem as ComboBoxItem)?.Tag as string);
        settings.TunDnsHijack = TunDnsHijackBox.IsChecked == true;
        settings.TunStrictRoute = TunStrictRouteBox.IsChecked == true;
        settings.AutoLearn = AutoLearnBox.IsChecked == true;
        settings.SelectedNode = (ServerBox.SelectedItem as ProxyNode)?.Name ?? settings.SelectedNode;
        if (SubscriptionList.SelectedItem is SubscriptionProfile selectedProfile)
            settings.ProtectedUrl = selectedProfile.ProtectedUrl;
        UserStore.SaveSettings(settings);
    }
    private void ShowOnboarding()
    {
        firstRunScreenHandled = true;
        MainTabs.Visibility = Visibility.Collapsed;
        OnboardingPanel.Visibility = Visibility.Visible;
        OnboardingPanel.SubscriptionUrl = SubscriptionBox.Text.Trim();
        OnboardingPanel.FocusLink();
    }

    private void HideOnboarding()
    {
        OnboardingPanel.Visibility = Visibility.Collapsed;
        MainTabs.Visibility = Visibility.Visible;
        MainTabs.SelectedIndex = 0;
        UpdateConnectionUi();
    }

    private void RefreshSubscriptionList(SubscriptionProfile? chosen = null, bool preserveEditor = false)
    {
        suppressProfileSelection = true;
        string? id = chosen?.Id ?? (SubscriptionList.SelectedItem as SubscriptionProfile)?.Id;
        SubscriptionList.ItemsSource = null;
        SubscriptionList.ItemsSource = profiles.ToList();
        SubscriptionList.SelectedItem = profiles.FirstOrDefault(p => p.Id == id);
        suppressProfileSelection = false;
        if (!preserveEditor) DisplaySelectedSubscription();
        ImportSummary.Text = $"{profiles.Count} подписок • {nodes.Count} серверов";
        UpdateConnectionUi();
    }

    private void DisplaySelectedSubscription()
    {
        if (suppressProfileSelection) return;
        var selected = SubscriptionList.SelectedItem as SubscriptionProfile;
        SubscriptionNameBox.Text = selected?.Name ?? "";
        SubscriptionBox.Text = selected?.Url ?? "";
        string selectedFormat = selected?.Format ?? "auto";
        foreach (var option in SubscriptionFormatBox.Items.OfType<ComboBoxItem>())
            if ((string?)option.Tag == selectedFormat)
            {
                SubscriptionFormatBox.SelectedItem = option;
                break;
            }
        RemoveSubscriptionButton.IsEnabled = selected != null;
        EditSubscriptionButton.IsEnabled = selected != null;
        ImportButton.Content = selected == null ? "Добавить подписку" : "Сохранить и обновить";
        SubscriptionEditor.Header = selected == null ? "Добавить подписку" : "Изменить подписку";
        ProfileStatusText.Text = selected == null
            ? "Укажи название и HTTPS-ссылку нового источника"
            : $"{selected.NodeCount} серверов • {selected.DetectedFormat}" +
              (selected.LastUpdatedUtc.HasValue
                  ? $" • {selected.LastUpdatedUtc.Value.ToLocalTime():dd.MM HH:mm}" : "");
    }

    private void SubscriptionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => DisplaySelectedSubscription();

    private (object? Profile, string Name, string Url, object? Format, bool Expanded) CaptureSubscriptionEditor()
        => (SubscriptionList.SelectedItem, SubscriptionNameBox.Text, SubscriptionBox.Text,
            SubscriptionFormatBox.SelectedItem, SubscriptionEditor.IsExpanded);

    private void NewSubscription_Click(object sender, RoutedEventArgs e)
    {
        SubscriptionList.SelectedItem = null;
        SubscriptionEditor.IsExpanded = true;
        ShowSubscriptionEditor(SubscriptionBox);
    }

    private void EditSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (SubscriptionList.SelectedItem is not SubscriptionProfile) return;
        SubscriptionEditor.IsExpanded = true;
        ShowSubscriptionEditor(SubscriptionNameBox);
    }

    private void ShowSubscriptionEditor(TextBox focus)
    {
        SubscriptionSection.IsExpanded = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            SubscriptionsPageScroll.ScrollToTop();
            focus.BringIntoView();
            focus.Focus();
        }));
    }

    private void RemoveSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (importing || SubscriptionList.SelectedItem is not SubscriptionProfile selected) return;
        bool confirmed = confirmRemoveSubscriptionOverride != null
            ? confirmRemoveSubscriptionOverride(selected)
            : ClientDialog.Show(this,
                $"Удалить подписку «{selected.Name}» и убрать её серверы из списка?\n" +
                "Текущее VPN-соединение не будет разорвано.",
                "Удаление подписки", MessageBoxButton.YesNo, MessageBoxImage.Question,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return;
        try
        {
            var next = profiles.Where(p => p.Id != selected.Id).ToList();
            SubscriptionProfileStore.Save(next);
            profiles = next;
            profileNodes.Remove(selected.Id);
            if (profiles.Count == 0) settings.ProtectedUrl = "";
            else settings.ProtectedUrl = profiles[0].ProtectedUrl;
            UserStore.SaveSettings(settings);
            RebuildServerList();
            RefreshSubscriptionList(profiles.FirstOrDefault());
            AppendLog("Удалена подписка: " + selected.Name);
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось удалить подписку: " + ex.GetType().Name);
            ProfileStatusText.Text = "Не удалось сохранить удаление.";
        }
    }

    private void RebuildServerList()
    {
        ResetServerLatencySession();
        var preferred = (ServerBox.SelectedItem as ProxyNode)?.Name ?? settings.SelectedNode;
        var merged = SubscriptionProfileStore.Merge(profiles
            .Where(p => profileNodes.ContainsKey(p.Id))
            .Select(p => (p, (IReadOnlyList<ProxyNode>)profileNodes[p.Id])));
        nodes = merged;
        // Rebinding the list must never cause a VPN reconnect on its own.
        bool wasImporting = importing;
        importing = true;
        try
        {
            ServerBox.ItemsSource = null;
            ServerBox.ItemsSource = nodes;
            ServerBox.SelectedItem = nodes.FirstOrDefault(n => n.Name == preferred)
                ?? nodes.FirstOrDefault(n => n.Name.EndsWith(" · " + preferred, StringComparison.Ordinal))
                ?? nodes.FirstOrDefault();
            RefreshNodeChoices();
            UpdateEngineInfo();
        }
        finally { importing = wasImporting; }
        UpdateLatencyUi();
        ImportSummary.Text = $"{profiles.Count} подписок • {nodes.Count} серверов";
        UpdateConnectionUi();
    }

    private void StoreImportedProfile(SubscriptionProfile profile,
        SubscriptionImporter.Result imported, bool persist, bool select = true)
    {
        var limited = imported.Nodes.Take(SubscriptionImporter.MaxServersPerSubscription).ToList();
        profileNodes[profile.Id] = limited;
        profile.NodeCount = limited.Count;
        profile.LastUpdatedUtc = DateTime.UtcNow;
        profile.DetectedFormat = imported.DetectedFormat;
        if (persist)
        {
            SubscriptionProfileStore.Save(profiles);
            SubscriptionCache.Save(profile.Url, limited);
        }
        RebuildServerList();
        if (select) RefreshSubscriptionList(profile);
        if (imported.ServerLimitReached || imported.Nodes.Count > limited.Count)
        {
            if (select)
                ProfileStatusText.Text = $"Добавлены первые {limited.Count} серверов. Лимит на одну ссылку — {SubscriptionImporter.MaxServersPerSubscription}.";
            AppendLog($"Подписка «{profile.Name}»: применён лимит {SubscriptionImporter.MaxServersPerSubscription} серверов.");
        }
    }

    private void ApplyImportedSubscription(SubscriptionImporter.Result imported)
    {
        var profile = profiles.FirstOrDefault(p =>
            string.Equals(p.Url, imported.SubscriptionUrl, StringComparison.Ordinal))
            ?? new SubscriptionProfile
            {
                Name = "ProstoKVN — основная",
                ProtectedUrl = UserStore.EncryptUrl(imported.SubscriptionUrl),
                Format = "auto"
            };
        if (!profiles.Contains(profile)) profiles.Add(profile);
        settings.ProtectedUrl = profile.ProtectedUrl; // Keep rollback compatibility.
        StoreImportedProfile(profile, imported, persist: true);
        UserStore.SaveSettings(settings);
        AppendLog($"Подписка настроена: {profile.Name}, {imported.Nodes.Count} серверов.");
    }

    private async Task LoadSavedSubscriptionAsync()
    {
        if (startupSubscriptionLoadStarted) return;
        startupSubscriptionLoadStarted = true;
        if (profiles.Count == 0) return;

        foreach (var profile in profiles)
        {
            if (!SubscriptionCache.TryLoad(profile.Url, out var cached, out _)) continue;
            profileNodes[profile.Id] = cached;
            profile.NodeCount = cached.Count;
        }
        if (profileNodes.Count > 0)
        {
            RebuildServerList();
            RefreshSubscriptionList(profiles.FirstOrDefault());
            AppendLog($"Восстановлено {nodes.Count} серверов из защищённого кэша.");
        }
        await RefreshAllSubscriptionsAsync(startup: true);
    }

    private async void RefreshAllSubscriptions_Click(object sender, RoutedEventArgs e)
        => await RefreshAllSubscriptionsAsync();

    private async Task RefreshAllSubscriptionsAsync(bool startup = false)
    {
        if (importing || profiles.Count == 0) return;
        importing = true;
        ImportButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        RefreshAllSubscriptionsButton.IsEnabled = false;
        var editorBeforeLoad = CaptureSubscriptionEditor();
        try
        {
            int failures = 0;
            string? accessMessage = null;
            bool staleEdit = false;
            var snapshot = profiles.ToArray();
            ImportSummary.Text = $"Обновляю подписки: {snapshot.Length} источников...";
            // Fetch at most three subscriptions concurrently; apply UI/data
            // changes strictly in profile order after all requests finish.
            using var limiter = new System.Threading.SemaphoreSlim(3, 3);
            var loads = snapshot.Select(async profile =>
            {
                string url = profile.Url;
                if (!SubscriptionImporter.IsValidUrl(url))
                    return (Profile: profile, Url: url,
                        Result: (SubscriptionImporter.Result?)null, Error: (Exception?)null);
                await limiter.WaitAsync();
                try
                {
                    var imported = startup && profile.Format == "auto"
                        ? await StartupSubscriptionLoader(url)
                        : await SubscriptionImporter.LoadAsync(url, profile.Format);
                    return (Profile: profile, Url: url,
                        Result: (SubscriptionImporter.Result?)imported, Error: (Exception?)null);
                }
                catch (Exception ex)
                {
                    return (Profile: profile, Url: url,
                        Result: (SubscriptionImporter.Result?)null, Error: (Exception?)ex);
                }
                finally { limiter.Release(); }
            }).ToArray();
            var responses = await Task.WhenAll(loads);
            foreach (var response in responses)
            {
                if (isWindowClosing) return;
                var profile = response.Profile;
                string url = response.Url;
                if (response.Error != null)
                {
                    failures++;
                    if (response.Error is SubscriptionAccessException access
                        && ReferenceEquals(SubscriptionList.SelectedItem, profile))
                        accessMessage = access.Message;
                    AppendLog($"Не удалось обновить «{profile.Name}»: {response.Error.GetType().Name}.");
                    continue;
                }
                if (response.Result == null || !profiles.Contains(profile) || profile.Url != url)
                    continue;
                // Do not overwrite an edit made while the requests were in flight.
                if (startup && ReferenceEquals(SubscriptionList.SelectedItem, profile)
                    && SubscriptionBox.Text.Trim() != url)
                {
                    staleEdit = true;
                    ImportSummary.Text = "Ссылка изменена";
                    AppendLog("Устаревший ответ подписки пропущен.");
                    continue;
                }
                StoreImportedProfile(profile, response.Result, persist: true, select: false);
                AppendLog($"Обновлена подписка «{profile.Name}»: {response.Result.Nodes.Count} серверов.");
            }
            RebuildServerList();
            if (!staleEdit && CaptureSubscriptionEditor() == editorBeforeLoad)
                RefreshSubscriptionList(SubscriptionList.SelectedItem as SubscriptionProfile
                    ?? profiles.FirstOrDefault());
            else
                RefreshSubscriptionList(preserveEditor: true);
            if (accessMessage != null && CaptureSubscriptionEditor() == editorBeforeLoad)
                ProfileStatusText.Text = accessMessage;
            if (staleEdit)
                ImportSummary.Text = "Ссылка изменена";
            else if (failures > 0)
                ImportSummary.Text = nodes.Count > 0
                    ? $"{nodes.Count} серверов • кэш, обновление недоступно"
                    : "Не удалось обновить подписки";
            else
                ImportSummary.Text = $"{profiles.Count} подписок • {nodes.Count} серверов";
        }
        finally
        {
            importing = false;
            if (!isWindowClosing)
            {
                ImportButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
                RefreshAllSubscriptionsButton.IsEnabled = true;
            }
        }
    }

    private async Task ImportAsync()
    {
        if (importing) return;
        string url = SubscriptionBox.Text.Trim();
        string name = SubscriptionNameBox.Text.Trim();
        string format = (SubscriptionFormatBox.SelectedItem as ComboBoxItem)?.Tag as string ?? "auto";
        if (!SubscriptionImporter.IsValidUrl(url))
        {
            ProfileStatusText.Text = "Укажи HTTPS-подписку или VLESS-ссылку.";
            return;
        }
        if (name.Length == 0) name = "Подписка " + (profiles.Count + 1);
        if (profiles.Count >= 30 && SubscriptionList.SelectedItem is not SubscriptionProfile)
        {
            ProfileStatusText.Text = "Максимум 30 подписок.";
            return;
        }

        var editedProfile = SubscriptionList.SelectedItem as SubscriptionProfile;
        var editorBeforeLoad = CaptureSubscriptionEditor();
        importing = true;
        ImportButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        RefreshAllSubscriptionsButton.IsEnabled = false;
        ProfileStatusText.Text = "Определяю формат и загружаю серверы...";
        try
        {
            var imported = await SubscriptionImporter.LoadAsync(url, format);
            if (isWindowClosing) return;
            var selected = editedProfile;
            var duplicate = profiles.FirstOrDefault(p => p.Url == url && p.Id != selected?.Id);
            if (duplicate != null)
                throw new InvalidOperationException("Эта подписка уже добавлена.");
            if (selected == null)
            {
                selected = new SubscriptionProfile();
                profiles.Add(selected);
            }
            string previousId = selected.Id;
            string previousUrl = selected.Url;
            selected.Name = name;
            selected.ProtectedUrl = UserStore.EncryptUrl(url);
            selected.Format = format;
            if (previousUrl.Length > 0 && previousUrl != url)
                profileNodes.Remove(previousId);
            settings.ProtectedUrl = selected.ProtectedUrl; // One legacy fallback for rollback.
            bool updateEditor = CaptureSubscriptionEditor() == editorBeforeLoad;
            StoreImportedProfile(selected, imported, persist: true, select: updateEditor);
            if (!updateEditor) RefreshSubscriptionList(preserveEditor: true);
            UserStore.SaveSettings(settings);
            AppendLog($"Добавлена/обновлена подписка «{selected.Name}»: {imported.Nodes.Count} серверов.");
        }
        catch (Exception ex)
        {
            ProfileStatusText.Text = ex is SubscriptionAccessException ? ex.Message
                : "Не удалось импортировать. Проверь ссылку и формат.";
            AppendLog("Импорт подписки: " + ex.GetType().Name);
        }
        finally
        {
            importing = false;
            ImportButton.IsEnabled = true;
            RefreshButton.IsEnabled = true;
            RefreshAllSubscriptionsButton.IsEnabled = true;
        }
    }

    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await RefreshAllSubscriptionsAsync();

    /// <summary>
    /// Serialize connection transitions and abandon stale requests after a
    /// server change, manual disconnect, or application shutdown.
    /// </summary>
    private async Task<bool> StartCoreForRevisionAsync(int revision)
    {
        if (isWindowClosing) return false;
        await connectionGate.WaitAsync();
        try
        {
            if (isWindowClosing || !recovery.Desired || revision != connectionRevision)
                return false;
            var connectionSettings = new AppSettings
            {
                Mode = settings.Mode, Tun = settings.Tun,
                TunStack = settings.TunStack, TunDnsHijack = settings.TunDnsHijack,
                TunStrictRoute = settings.TunStrictRoute,
                AutoLearn = settings.AutoLearn, SelectedNode = settings.SelectedNode,
                ProtectedUrl = settings.ProtectedUrl, Theme = settings.Theme
            };
            await core.StartAsync(nodes, rules, connectionSettings);
            if (isWindowClosing || !recovery.Desired || revision != connectionRevision)
            {
                core.Stop();
                return false;
            }
            activeSettings = connectionSettings;
            rulesNeedReconnect = false;
            rulesApplyError = null;
            appliedMode = connectionSettings.Mode;
            appliedTun = connectionSettings.Tun;
            appliedTunStack = VpnCore.NormalizeTunStack(connectionSettings.TunStack);
            appliedTunDnsHijack = connectionSettings.TunDnsHijack;
            appliedTunStrictRoute = connectionSettings.TunStrictRoute;
            ResetQualityForNewConnection();
            return true;
        }
        finally { connectionGate.Release(); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        if (connectionBusy) return;
        if (core.IsRunning || recovery.Desired)
        {
            Disconnect_Click(sender, e);
            return;
        }
        connectionBusy = true;
        ConnectButton.IsEnabled = false;
        UpdateConnectionUi();
        int revision = ++connectionRevision;
        try
        {
            if (nodes.Count == 0 && profiles.Count == 0)
            {
                if (SubscriptionImporter.IsValidUrl(SubscriptionBox.Text))
                    await ImportAsync();
                else
                {
                    ShowOnboarding();
                    return;
                }
            }
            if (nodes.Count == 0 && profiles.Count > 0)
                await RefreshAllSubscriptionsAsync();
            if (nodes.Count == 0 || revision != connectionRevision || isWindowClosing) return;
            recovery.Enable();
            vpnExitVerified = false;
            consecutiveFailedVpnChecks = 0;
            if (!await StartCoreForRevisionAsync(revision)) return;
            meter.Reset();
            recovery.Healthy();
            RecoveryText.Text = "Соединение под наблюдением";
            learner.ResetExitCountries();
            await VerifyConnectedAsync();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = "Не удалось подключиться";
            AppendLog("Ошибка запуска: " + ex.Message);
            ClientDialog.Show(this,ex.Message, "ProstoKVN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { connectionBusy = false; ConnectButton.IsEnabled = true; UpdateConnectionUi(); }
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        if (qualityActive) StopQualityDiagnostics();
        ++connectionRevision;
        vpnExitVerified = false;
        consecutiveFailedVpnChecks = 0;
        recovery.Disable();
        await connectionGate.WaitAsync();
        try { core.Stop(); }
        finally { connectionGate.Release(); }
        if (isWindowClosing) return;
        meter.Reset();
        RecoveryText.Text = "Автовосстановление отключено вручную";
        LatencyText.Text = "HTTPS-задержка: нет данных";
        UdpText.Text = "UDP: нет данных";
        StatusLabel.Text = "Не подключено";
        StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "ThemeFg193B3E");
        UpdateConnectionUi();
        AppendLog("Подключение остановлено, прежний системный прокси восстановлен.");
    }
    private void TunStack_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => SettingsChanged_Click(sender, new RoutedEventArgs());

    private void SettingsChanged_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool previousTun = settings.Tun;
        string previousStack = settings.TunStack;
        bool previousDns = settings.TunDnsHijack;
        bool previousStrict = settings.TunStrictRoute;
        Persist();
        TunAdvancedSection.IsEnabled = settings.Tun;
        UpdateConnectionUi();
        if (core.IsRunning && (previousTun != settings.Tun
            || previousStack != settings.TunStack
            || previousDns != settings.TunDnsHijack
            || previousStrict != settings.TunStrictRoute))
            AppendLog("Настройки TUN изменены. Переподключись для применения.");
    }
    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ModeBox.SelectedValue is not string mode || !RoutingModes.IsPrimary(mode)) return;
        bool selectedOnly = mode == "mine";
        if (selectedOnly) TunBox.IsChecked = true;
        TunBox.IsEnabled = !selectedOnly;
        TunAdvancedSection.IsEnabled = TunBox.IsChecked == true;
        AutoLearnBox.IsEnabled = mode == "smart";
        Persist();
        UpdateConnectionUi();
        if (core.IsRunning) AppendLog("Режим изменён. Переподключись для применения.");
    }
    private void ServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateLatencyUi();
        if (!IsLoaded || ServerBox.SelectedItem == null) return;
        UpdateEngineInfo();
        Persist();
        if (core.IsRunning && !importing) _ = ReconnectForSelectionAsync();
    }
    private async Task ReconnectForSelectionAsync()
    {
        connectionBusy = true;
        UpdateConnectionUi();
        try
        {
            ConnectButton.IsEnabled = false;
            int revision = ++connectionRevision;
            vpnExitVerified = false;
            consecutiveFailedVpnChecks = 0;
            if (!await StartCoreForRevisionAsync(revision)) return;
            learner.ResetExitCountries();
            await VerifyConnectedAsync();
            AppendLog("Сервер переключён: " + settings.SelectedNode);
        }
        catch (Exception ex) { StatusLabel.Text = "Ошибка переключения"; AppendLog("Сбой переключения: " + ex.Message); }
        finally { connectionBusy = false; ConnectButton.IsEnabled = true; UpdateConnectionUi(); }
    }
    private async Task VerifyConnectedAsync()
    {
        if (appliedMode == "direct")
        {
            vpnExitVerified = false;
            StatusLabel.Text = "Прямое соединение активно";
            return;
        }
        if (!core.IsRunning || vpnVerificationBusy) return;

        vpnVerificationBusy = true;
        int revision = connectionRevision;
        if (!vpnExitVerified) StatusLabel.Text = "Проверяю VPN-выход...";
        try
        {
            // Do not confuse an active local proxy process with a verified
            // VPN exit. After a cold start, the server may need time to dial.
            // MonitorTickAsync will retry this check without reconnecting.
            var verification = await ConnectionVerifier.VerifyAsync();
            if (revision != connectionRevision || !core.IsRunning || !recovery.Desired)
                return;

            vpnExitVerified = verification.Verified;
            consecutiveFailedVpnChecks = verification.Verified
                ? 0 : Math.Min(10, consecutiveFailedVpnChecks + 1);
            StatusLabel.Text = verification.Verified
                ? "Подключено • VPN-выход подтверждён."
                : "Соединение запущено • VPN-выход не подтверждён.";
            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty,
                verification.Verified ? "StatusPositive" : "StatusWarning");
            RecoveryText.Text = ConnectionStatusPolicy.MonitorText(vpnExitVerified);
            AppendLog("Проверка соединения: " + verification.Detail);
        }
        catch (Exception ex)
        {
            if (revision != connectionRevision || !core.IsRunning || !recovery.Desired)
                return;
            vpnExitVerified = false;
            consecutiveFailedVpnChecks = Math.Min(10, consecutiveFailedVpnChecks + 1);
            StatusLabel.Text = "Соединение запущено • проверка VPN-выхода недоступна.";
            StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarning");
            RecoveryText.Text = ConnectionStatusPolicy.MonitorText(false);
            AppendLog("Проверка соединения: " + ex.GetType().Name);
        }
        finally
        {
            // Rate-limit failed checks. Never block normal network traffic.
            if (revision == connectionRevision)
                lastVpnVerificationUtc = DateTime.UtcNow;
            vpnVerificationBusy = false;
            UpdateConnectionUi();
        }
    }
    private void ScheduleLearnedRulesApply()
    {
        pendingLearnedRules = true;
        // Batch rule discoveries so ChatGPT streaming requests aren't subjected
        // to a full Mihomo config reload for every new technical subdomain.
        if (!learnedRulesBatchTimer.IsEnabled && !learnedReloadBusy)
            learnedRulesBatchTimer.Start();
    }
    private async Task FlushLearnedRulesAsync()
    {
        if (!pendingLearnedRules || learnedReloadBusy) return;
        learnedReloadBusy = true;
        pendingLearnedRules = false;
        try
        {
            int removed = ServiceRuleGrouper.CompactStableRules(rules);
            if (removed > 0)
            {
                UserStore.SaveRules(rules);
                RefreshRules();
                AppendLog($"Объединено {removed} автоматических правил по сервисам.");
            }
            await ReloadRulesSafeAsync();
        }
        finally
        {
            learnedReloadBusy = false;
            if (pendingLearnedRules && !learnedRulesBatchTimer.IsEnabled)
                learnedRulesBatchTimer.Start();
        }
    }
    private async Task ReloadRulesSafeAsync()
    {
        await reloadGate.WaitAsync();
        try
        {
            if (isWindowClosing) return;
            await connectionGate.WaitAsync();
            try
            {
                rulesApplyError = null;
                if (!isWindowClosing && core.IsRunning && activeSettings != null)
                {
                    // Pending UI choices must never leak into the running tunnel.
                    var result = await core.ReloadRoutesAsync(nodes, rules, activeSettings);
                    rulesNeedReconnect = result == RouteReloadResult.ReconnectRequired;
                }
            }
            finally { connectionGate.Release(); }
        }
        catch (Exception ex)
        {
            if (!isWindowClosing)
            {
                rulesApplyError = "Правила сохранены, но не применены: " + ex.Message;
                rulesNeedReconnect = true;
                AppendLog(rulesApplyError);
            }
        }
        finally
        {
            reloadGate.Release();
            if (!isWindowClosing) UpdateConnectionUi();
        }
    }
    private void UpdateEngineInfo()
    {
        if (ServerBox.SelectedItem is ProxyNode selected)
            EngineInfoLabel.Text = "Автовыбор: " + EngineSelector.Display(selected);
        else EngineInfoLabel.Text = "Ядро определяется по протоколу автоматически";
    }
    private void ObserveDomainFromLog(string line)
    {
        if (!core.IsRunning || (activeSettings?.Mode ?? settings.Mode) != "smart" || AutoLearnBox.IsChecked != true) return;
        if (!line.Contains("-->") || line.Contains("vega-probe-", StringComparison.OrdinalIgnoreCase)) return;
        if (!line.Contains("[TCP]", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("[UDP]", StringComparison.OrdinalIgnoreCase)
            && !line.Contains("dial ", StringComparison.OrdinalIgnoreCase)) return;
        var match = System.Text.RegularExpressions.Regex.Match(line, @"-->\s+([a-z0-9.-]+):(?:80|443|8443)(?:\s|$)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!match.Success) return;
        string domain = match.Groups[1].Value.ToLowerInvariant();
        if (!VpnCore.IsDomainSafe(domain) || !learner.CanInspect(domain) || observedQueued.Contains(domain)) return;
        if (observedTraffic.Count >= 120)
        {
            string oldest = observedTraffic.Dequeue();
            observedQueued.Remove(oldest);
        }
        observedQueued.Add(domain);
        observedTraffic.Enqueue(domain);
    }
    private async Task MonitorTickAsync()
    {
        if (monitorBusy || !recovery.Desired || nodes.Count == 0) return;
        monitorBusy = true;
        int monitorRevision = connectionRevision;
        try
        {
            if (!core.IsTransportHealthy)
            {
                vpnExitVerified = false;
                StatusLabel.Text = "Сетевые ядра недоступны • ожидаю восстановления.";
                StatusLabel.SetResourceReference(TextBlock.ForegroundProperty, "StatusWarning");
                if (recovery.ShouldRecover(false, DateTime.UtcNow))
                {
                    recovery.Failed(DateTime.UtcNow);
                    RecoveryText.Text = $"Восстановление соединения: попытка {recovery.Attempts}/5";
                    AppendLog(RecoveryText.Text);
                    try
                    {
                        int revision = ++connectionRevision;
                        vpnExitVerified = false;
                        consecutiveFailedVpnChecks = 0;
                        if (!await StartCoreForRevisionAsync(revision)) return;
                        meter.Reset();
                        RecoveryText.Text = "Ядра восстановлены, проверяю соединение";
                        await VerifyConnectedAsync();
                        recovery.ObserveHealthy(DateTime.UtcNow);
                    }
                    catch (Exception ex)
                    {
                        AppendLog("Ошибка восстановления: " + ex.Message);
                        if (recovery.Exhausted)
                            RecoveryText.Text = "Автовосстановление остановлено после 5 неудачных попыток";
                    }
                }
                return;
            }
            if (++monitorTicks % 4 == 0)
            {
                // An HTTP check runs at most once per ~48 seconds, not every tick.
                var snapshot = await core.ReadMonitorAsync(meter, measureDelay: true);
                if (monitorRevision != connectionRevision || !recovery.Desired || !core.IsRunning)
                    return;
                LatencyText.Text = snapshot.HttpsLatencyMs.HasValue
                    ? $"HTTPS-задержка: {snapshot.HttpsLatencyMs} мс"
                    : "HTTPS-задержка: измерение недоступно";
                UpdateUdp(snapshot);
            }
            else
            {
                var snapshot = await core.ReadMonitorAsync(meter, measureDelay: false);
                if (monitorRevision != connectionRevision || !recovery.Desired || !core.IsRunning)
                    return;
                UpdateUdp(snapshot);
            }
            if (vpnExitVerified || appliedMode == "direct")
                recovery.ObserveHealthy(DateTime.UtcNow);
            // If the first verification ran before the VPN dial completed,
            // retry while local cores stay up. No disconnect or engine restart.
            if (ConnectionStatusPolicy.ShouldRetry(recovery.Desired,
                core.IsTransportHealthy, vpnExitVerified, vpnVerificationBusy,
                lastVpnVerificationUtc, DateTime.UtcNow))
                await VerifyConnectedAsync();
            if (monitorRevision != connectionRevision || !recovery.Desired || !core.IsRunning)
                return;
            // Never restart on a single failed IP service. Only after three
            // consecutive complete checks, and with the existing bounded backoff.
            if (appliedMode != "direct" && consecutiveFailedVpnChecks >= 3
                && recovery.ShouldRecover(false, DateTime.UtcNow))
            {
                recovery.Failed(DateTime.UtcNow);
                RecoveryText.Text = $"VPN-выход не отвечает, переподключаю ({recovery.Attempts}/5)";
                AppendLog(RecoveryText.Text);
                int revision = ++connectionRevision;
                consecutiveFailedVpnChecks = 0;
                vpnExitVerified = false;
                try
                {
                    if (!await StartCoreForRevisionAsync(revision)) return;
                    meter.Reset();
                    await VerifyConnectedAsync();
                }
                catch (Exception ex)
                {
                    AppendLog("Автовосстановление VPN: " + ex.GetType().Name);
                }
                return;
            }
            RecoveryText.Text = ConnectionStatusPolicy.MonitorText(vpnExitVerified,
                appliedMode == "direct");
        }
        catch (Exception ex)
        {
            if (monitorRevision == connectionRevision && recovery.Desired)
            {
                RecoveryText.Text = "Диагностика недоступна: " + ex.GetType().Name;
                AppendLog("Монитор соединения: " + ex.Message);
            }
            // Do not restart healthy transport processes merely because an HTTP
            // latency probe or local telemetry request has timed out.
        }
        finally { monitorBusy = false; UpdateConnectionUi(); }
    }

    private void UpdateUdp(MonitorSnapshot snapshot)
    {
        static string Speed(long value) => value >= 1024 * 1024
            ? $"{value / 1048576.0:F2} МБ/с" : $"{value / 1024.0:F1} КБ/с";
        UdpText.Text = $"UDP: {snapshot.UdpConnections} потоков"
            + $" (Discord: {snapshot.DiscordUdpConnections})"
            + $" • ↑ {Speed(snapshot.UdpUpBytesPerSecond)}"
            + $" ↓ {Speed(snapshot.UdpDownBytesPerSecond)}";
        QualityUpdateUdp(snapshot);
    }

    private async Task LearnTickAsync()
    {
        if (testing || !core.IsRunning || (activeSettings?.Mode ?? settings.Mode) != "smart" || AutoLearnBox.IsChecked != true) return;
        testing = true;
        try
        {
            if (UserStore.PruneExpiredRules(rules) > 0)
            {
                RefreshRules();
                ScheduleLearnedRulesApply();
                AppendLog("Истёк срок пробного правила: маршрут будет обновлён пакетно.");
            }
            int processed = 0;
            var checks = new List<Task>();
            while (observedTraffic.Count > 0 && processed < 4)
            {
                string domain = observedTraffic.Dequeue();
                observedQueued.Remove(domain);
                if (!learner.CanInspect(domain)) continue;
                checks.Add(learner.InspectAsync(domain, rules, settings.AutoLearn, nodes, settings.SelectedNode));
                processed++;
            }
            if (processed == 0)
            {
                var domains = await core.ActiveDomainsAsync();
                foreach (var domain in domains.Where(learner.CanInspect).Take(4))
                    checks.Add(learner.InspectAsync(domain, rules, settings.AutoLearn, nodes, settings.SelectedNode));
            }
            await Task.WhenAll(checks);
        }
        catch (Exception ex) { AppendLog("Диагностика обучения: " + ex.Message); }
        finally { testing = false; }
    }
}
