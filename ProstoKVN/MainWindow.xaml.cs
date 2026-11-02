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
    private readonly SmartRouteLearner learner = new();
    private readonly DispatcherTimer learnTimer = new() { Interval = TimeSpan.FromSeconds(7) };
    private readonly DispatcherTimer learnedRulesBatchTimer = new()
    {
        Interval = TimeSpan.FromSeconds(8)
    };
    private readonly System.Threading.SemaphoreSlim reloadGate = new(1, 1);
    private bool pendingLearnedRules;
    private bool learnedReloadBusy;
    private AppSettings settings = new();
    private List<RouteRule> rules = new();
    private List<ProxyNode> nodes = new();
    private bool importing, testing;
    private bool firstRunScreenHandled;
    private bool startupSubscriptionLoadStarted;
    private bool isWindowClosing;
    private bool checkingUpdates;
    private GitHubUpdate? readyUpdate;
    // Injectable to test startup without contacting a real subscription server.
    public Func<string, Task<SubscriptionImporter.Result>> StartupSubscriptionLoader { get; set; }
        = SubscriptionImporter.LoadAsync;
    private readonly Queue<string> observedTraffic = new();
    private readonly HashSet<string> observedQueued = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> expandedServices = new(StringComparer.OrdinalIgnoreCase);
    public MainWindow()
    {
        InitializeComponent();
        var appVersion = typeof(MainWindow).Assembly.GetName().Version;
        string release = appVersion == null ? "dev" : $"{appVersion.Major}.{appVersion.Minor}"
            + (appVersion.Build > 0 ? $".{appVersion.Build}" : "");
        Title = "ProstoKVN " + release;
        VersionText.Text = "Windows • " + release;
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
        ApplyTheme(settings.Theme, save: false);
        rules = UserStore.LoadRules();
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
        SubscriptionBox.Text = UserStore.DecryptUrl(settings.ProtectedUrl);
        AutoLearnBox.IsChecked = settings.AutoLearn;
        TunBox.IsChecked = settings.Tun;
        ModeBox.SelectedIndex = settings.Mode == "vpn" ? 1 : settings.Mode == "direct" ? 2 : 0;
        UserStore.PruneExpiredRules(rules);
        RefreshRules();
        RefreshNodeChoices();
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
        Closing += (_, _) =>
        {
            isWindowClosing = true;
            recovery.Disable();
            monitorTimer.Stop();
            learnTimer.Stop();
            learnedRulesBatchTimer.Stop();
            learner.Dispose();
            core.Dispose();
        };
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
        MainTabs.SelectedIndex = 2;
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
        var result = MessageBox.Show(
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
            Close();
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
        ThemeButton.Content = normalized == ThemeManager.Dark ? "☀ Светлая" : "☾ Тёмная";
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
        settings.Mode = ModeBox.SelectedIndex == 1 ? "vpn" : ModeBox.SelectedIndex == 2 ? "direct" : "smart";
        settings.Tun = TunBox.IsChecked == true;
        settings.AutoLearn = AutoLearnBox.IsChecked == true;
        settings.SelectedNode = (ServerBox.SelectedItem as ProxyNode)?.Name ?? settings.SelectedNode;
        var url = SubscriptionBox.Text.Trim();
        if (Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme == "https")
            settings.ProtectedUrl = UserStore.EncryptUrl(url);
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
    }

    private async Task LoadSavedSubscriptionAsync()
    {
        // One automatic import per application window, never an automatic VPN connection.
        if (startupSubscriptionLoadStarted) return;
        startupSubscriptionLoadStarted = true;
        string url = UserStore.DecryptUrl(settings.ProtectedUrl).Trim();
        if (!SubscriptionImporter.IsValidUrl(url)
            || !string.Equals(SubscriptionBox.Text.Trim(), url, StringComparison.Ordinal)
            || importing) return;

        if (SubscriptionCache.TryLoad(url, out var cached, out var savedUtc))
        {
            nodes = cached;
            ServerBox.ItemsSource = nodes;
            ServerBox.SelectedItem = nodes.FirstOrDefault(n => n.Name == settings.SelectedNode)
                ?? nodes.FirstOrDefault();
            RefreshNodeChoices();
            UpdateEngineInfo();
            ImportSummary.Text = $"{nodes.Count} серверов из защищённого кэша";
            AppendLog($"Восстановлены серверы из кэша ({savedUtc.ToLocalTime():dd.MM HH:mm}).");
        }
        importing = true;
        ConnectButton.IsEnabled = false;
        ImportButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        ImportSummary.Text = nodes.Count > 0 ? $"{nodes.Count} серверов • обновляю..." : "Загружаю серверы...";
        EngineInfoLabel.Text = "Загрузка сохранённой подписки...";
        AppendLog("Загружаю список серверов из сохранённой подписки.");
        try
        {
            var imported = await StartupSubscriptionLoader(url);
            if (isWindowClosing) return;
            // Never replace a subscription being edited while the request is in flight.
            if (!string.Equals(SubscriptionBox.Text.Trim(), url, StringComparison.Ordinal))
            {
                AppendLog("Подписка изменена во время загрузки; устаревший ответ пропущен.");
                ImportSummary.Text = "Ссылка изменена";
                UpdateEngineInfo();
                return;
            }
            ApplyImportedSubscription(imported);
            AppendLog("Список серверов загружен автоматически.");
        }
        catch (Exception)
        {
            if (!isWindowClosing)
            {
                ImportSummary.Text = nodes.Count > 0
                    ? $"{nodes.Count} серверов • кэш, обновление недоступно"
                    : "Не удалось загрузить серверы";
                UpdateEngineInfo();
                AppendLog(nodes.Count > 0
                    ? "HTTPS-подписка недоступна; оставлен защищённый список серверов."
                    : "HTTPS-подписка недоступна. Проверь VPN и нажми «Обновить серверы».");
            }
        }
        finally
        {
            importing = false;
            if (!isWindowClosing)
            {
                ConnectButton.IsEnabled = true;
                ImportButton.IsEnabled = true;
                RefreshButton.IsEnabled = true;
            }
        }
    }

    private void ApplyImportedSubscription(SubscriptionImporter.Result imported)
    {
        nodes = imported.Nodes;
        SubscriptionBox.Text = imported.SubscriptionUrl;
        ServerBox.ItemsSource = null;
        ServerBox.ItemsSource = nodes;
        ServerBox.SelectedItem = nodes.FirstOrDefault(n => n.Name == settings.SelectedNode)
            ?? nodes.FirstOrDefault(n => Convert.ToString(n.Values["type"]) == "hysteria2")
            ?? nodes[0];
        RefreshNodeChoices();
        UpdateEngineInfo();
        ImportSummary.Text = imported.Summary;
        Persist();
        SubscriptionCache.Save(imported.SubscriptionUrl, nodes);
        AppendLog($"Подписка успешно настроена: {nodes.Count} серверов.");
    }

    private static string ChangeSubscriptionPath(string url, string path)
    {
        var uri = new Uri(url);
        var builder = new UriBuilder(uri);
        string[] tokens = builder.Path.Trim('/').Split('/');
        if (tokens.Length >= 2 && (tokens[0] == "json" || tokens[0] == "sub"))
            tokens[0] = path;
        builder.Path = "/" + string.Join("/", tokens);
        return builder.Uri.AbsoluteUri;
    }
    private async Task ImportAsync()
    {
        if (importing) return;
        importing = true;
        ImportButton.IsEnabled = false;
        try
        {
            var url = SubscriptionBox.Text.Trim();
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
                throw new InvalidOperationException("Введи HTTPS-ссылку подписки.");
            var jsonUrl = ChangeSubscriptionPath(url, "json");
            var subUrl = ChangeSubscriptionPath(url, "sub");
            var results = new List<ProxyNode>();
            var errors = new List<string>();
            try
            {
                var fromJson = await JsonSubscription.LoadAsync(jsonUrl);
                results.AddRange(fromJson);
                AppendLog($"JSON: получено {fromJson.Count} совместимых узлов.");
            }
            catch (Exception ex) { errors.Add("JSON: " + ex.Message); }
            try
            {
                var (fromSub, skipped) = await SubscriptionReader.LoadAsync(subUrl);
                var known = new HashSet<string>(results.Select(Key), StringComparer.Ordinal);
                int added = 0;
                foreach (var n in fromSub)
                    if (known.Add(Key(n))) { results.Add(n); added++; }
                AppendLog($"SUB: найдено {fromSub.Count}, добавлено {added}; пропущено {skipped.Count}.");
            }
            catch (Exception ex) { errors.Add("SUB: " + ex.Message); }
            if (results.Count == 0) throw new InvalidOperationException("Не удалось загрузить узлы. " + string.Join("; ", errors));
            int suffix = 1;
            var usedNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var n in results)
            {
                string name = n.Name;
                suffix = 1;
                while (!usedNames.Add(n.Name)) n.Name = name + " #" + ++suffix;
                n.Values["name"] = n.Name;
            }
            nodes = results;
            ServerBox.ItemsSource = null;
            ServerBox.ItemsSource = nodes;
            ServerBox.SelectedItem = nodes.FirstOrDefault(x => x.Name == settings.SelectedNode)
                ?? nodes.FirstOrDefault(x => Convert.ToString(x.Values["type"]) == "hysteria2")
                ?? nodes[0];
            RefreshNodeChoices();
            UpdateEngineInfo();
            ImportSummary.Text = $"{nodes.Count} серверов";
            Persist();
            SubscriptionCache.Save(url, nodes);
            AppendLog($"Подписка импортирована: {nodes.Count} узлов. " + (errors.Count > 0 ? "Часть форматов недоступна." : ""));
        }
        catch (Exception ex)
        {
            ImportSummary.Text = nodes.Count > 0
                ? $"{nodes.Count} серверов • обновление недоступно"
                : "Ошибка загрузки подписки";
            AppendLog("Ошибка обновления подписки: " + ex.GetType().Name
                + ". Проверь VPN-маршрут или повтори попытку.");
        }
        finally { importing = false; ImportButton.IsEnabled = true; }
    }
    private static string Key(ProxyNode n)
    {
        string S(string key) => n.Values.TryGetValue(key, out var value) ? Convert.ToString(value) ?? "" : "";
        return string.Join("|", S("type"), S("server"), S("port"), S("uuid"), S("network"));
    }
    private async void Import_Click(object sender, RoutedEventArgs e) => await ImportAsync();
    private async void Refresh_Click(object sender, RoutedEventArgs e) => await ImportAsync();
    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            if (nodes.Count == 0 && !SubscriptionImporter.IsValidUrl(SubscriptionBox.Text))
            {
                ShowOnboarding();
                return;
            }
            if (nodes.Count == 0 && SubscriptionImporter.IsValidUrl(SubscriptionBox.Text))
                await ImportAsync();
            if (nodes.Count == 0) return;
            recovery.Enable();
            await core.StartAsync(nodes, rules, settings);
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
            MessageBox.Show(ex.Message, "ProstoKVN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ConnectButton.IsEnabled = true; }
    }
    private void Disconnect_Click(object sender, RoutedEventArgs e)
    {
        recovery.Disable();
        core.Stop();
        meter.Reset();
        RecoveryText.Text = "Автовосстановление отключено вручную";
        LatencyText.Text = "HTTPS-задержка: нет данных";
        UdpText.Text = "UDP: нет данных";
        StatusLabel.Text = "Не подключено";
        StatusLabel.Foreground = System.Windows.Media.Brushes.LightGray;
        AppendLog("Подключение остановлено, прежний системный прокси восстановлен.");
    }
    private void SettingsChanged_Click(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        bool previousTun = settings.Tun;
        Persist();
        if (core.IsRunning && previousTun != settings.Tun)
            AppendLog("Настройка TUN изменена. Переподключись для применения.");
    }
    private void ModeBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded) return;
        Persist();
        if (core.IsRunning) AppendLog("Режим изменён. Переподключись для применения.");
    }
    private void ServerBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || ServerBox.SelectedItem == null) return;
        UpdateEngineInfo();
        Persist();
        if (core.IsRunning && !importing) _ = ReconnectForSelectionAsync();
    }
    private async Task ReconnectForSelectionAsync()
    {
        try
        {
            ConnectButton.IsEnabled = false;
            await core.StartAsync(nodes, rules, settings);
            learner.ResetExitCountries();
            await VerifyConnectedAsync();
            AppendLog("Сервер переключён: " + settings.SelectedNode);
        }
        catch (Exception ex) { StatusLabel.Text = "Ошибка переключения"; AppendLog("Сбой переключения: " + ex.Message); }
        finally { ConnectButton.IsEnabled = true; }
    }
    private async Task VerifyConnectedAsync()
    {
        if (settings.Mode == "direct") { StatusLabel.Text = "Прямое соединение активно"; return; }
        StatusLabel.Text = "Проверяю подключение...";
        try
        {
            // Verify the actual outbound VPN proxy, not one Cloudflare page.
            // The selected server may be healthy while that page returns
            // anti-bot HTTP 403; avoid mislabeling it as a failed connection.
            var verification = await ConnectionVerifier.VerifyAsync();
            StatusLabel.Text = verification.Verified
                ? "Подключено • VPN-выход подтверждён."
                : "Соединение запущено • VPN-выход не подтверждён.";
            StatusLabel.Foreground = verification.Verified
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Orange;
            AppendLog("Проверка соединения: " + verification.Detail);
        }
        catch (Exception ex) { StatusLabel.Text = "Не удалось проверить соединение"; AppendLog("Проверка соединения: " + ex.Message); }
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
            if (core.IsRunning) await core.ReloadRoutesAsync(nodes, rules, settings);
        }
        catch (Exception ex) { AppendLog("Не удалось применить правила: " + ex.Message); }
        finally { reloadGate.Release(); }
    }
    private static string ServiceGroupKey(object? data)
        => (data as System.Windows.Data.CollectionViewGroup)?.Name?.ToString() ?? "";

    private void ServiceGroup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander)
            expander.IsExpanded = expandedServices.Contains(ServiceGroupKey(expander.DataContext));
    }
    private void ServiceGroup_Expanded(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && ServiceGroupKey(expander.DataContext) is { Length: > 0 } key)
            expandedServices.Add(key);
    }
    private void ServiceGroup_Collapsed(object sender, RoutedEventArgs e)
    {
        if (sender is Expander expander && ServiceGroupKey(expander.DataContext) is { Length: > 0 } key)
            expandedServices.Remove(key);
    }

    private void RefreshRules()
    {
        double savedOffset = RulesPageScroll?.VerticalOffset ?? 0;
        RouteRule? selected = RulesList.SelectedItem as RouteRule;
        RulesList.ItemsSource = null;
        var ordered = rules.OrderBy(x => x.ServiceName)
            .ThenByDescending(x => x.Manual)
            .ThenBy(x => x.Domain).ToList();
        var view = new System.Windows.Data.ListCollectionView(ordered);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(RouteRule.ServiceName)));
        RulesList.ItemsSource = view;
        if (selected != null && rules.Contains(selected)) RulesList.SelectedItem = selected;
        if (savedOffset > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => RulesPageScroll?.ScrollToVerticalOffset(savedOffset)));
    }
    private void UpdateEngineInfo()
    {
        if (ServerBox.SelectedItem is ProxyNode selected)
            EngineInfoLabel.Text = "Автовыбор: " + EngineSelector.Display(selected);
        else EngineInfoLabel.Text = "Ядро определяется по протоколу автоматически";
    }
    private void RefreshNodeChoices()
    {
        RuleNodeBox.ItemsSource = new[] { "Авто (основной)" }.Concat(nodes.Select(n => n.Name)).ToList();
        RuleNodeBox.SelectedIndex = 0;
    }
    private void RuleRouteBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RuleNodeBox == null) return;
        RuleNodeBox.IsEnabled = ((RuleRouteBox.SelectedItem as ComboBoxItem)?.Tag as string) == "VEGA";
    }
    private void RulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RulesList.SelectedItem is not RouteRule selected || RuleLabelBox == null)
            return;
        // Selecting an existing rule loads it into the editor. Saving makes
        // the updated entry manual, protecting the name from auto-learning.
        DomainBox.Text = selected.Domain;
        RuleLabelBox.Text = selected.Label ?? "";
        for (int i = 0; i < RuleRouteBox.Items.Count; i++)
        {
            if ((RuleRouteBox.Items[i] as ComboBoxItem)?.Tag as string == selected.Route)
            {
                RuleRouteBox.SelectedIndex = i;
                break;
            }
        }
        int nodeIndex = nodes.FindIndex(n => n.Name == selected.PreferredNode);
        RuleNodeBox.SelectedIndex = nodeIndex >= 0 ? nodeIndex + 1 : 0;
    }
    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        if (!RouteTarget.TryParse(DomainBox.Text, out var target))
        {
            MessageBox.Show("Введите корректный домен, IP-адрес или подсеть CIDR.\n"
                + "Примеры: example.com, 8.8.8.8, 1.2.3.0/24, 2001:db8::/32.",
                "ProstoKVN", MessageBoxButton.OK, MessageBoxImage.Warning);
            DomainBox.Focus();
            return;
        }
        string route = ((RuleRouteBox.SelectedItem as ComboBoxItem)?.Tag as string) ?? "DIRECT";
        string preferred = route == "VEGA" && RuleNodeBox.SelectedIndex > 0
            && RuleNodeBox.SelectedIndex <= nodes.Count
            ? nodes[RuleNodeBox.SelectedIndex - 1].Name : "";
        rules.RemoveAll(x => RouteTarget.TryParse(x.Domain, out var oldTarget)
            && oldTarget.Value.Equals(target.Value, StringComparison.OrdinalIgnoreCase));
        rules.Add(new RouteRule
        {
            Domain = target.Value,
            Label = RuleLabelBox.Text.Trim(),
            Route = route,
            PreferredNode = preferred,
            Manual = true
        });
        UserStore.SaveRules(rules);
        RefreshRules();
        DomainBox.Clear();
        RuleLabelBox.Clear();
        RulesList.SelectedItem = null;
        RuleNodeBox.SelectedIndex = 0;
        AppendLog($"Добавлено исключение: {target.DisplayKind} {target.Value} → "
            + (route == "VEGA" ? "СЕРВЕР" : route));
        await ReloadRulesSafeAsync();
    }
    private async void AutoRoute_Click(object sender, RoutedEventArgs e)
    {
        string domain = ProbeBox.Text.Trim().TrimEnd('.').ToLowerInvariant();
        if (!VpnCore.IsDomainSafe(domain)) { ProbeResult.Text = "Введите домен, например chatgpt.com"; return; }
        if (!core.IsRunning || nodes.Count == 0) { ProbeResult.Text = "Сначала подключись."; return; }
        ProbeResult.Text = "Проверяю прямой, выбранный и альтернативные маршруты...";
        try
        {
            await learner.InspectAsync(domain, rules, true, nodes, settings.SelectedNode, force: true);
            var effective = rules.FirstOrDefault(r => r.Domain.Equals(domain, StringComparison.OrdinalIgnoreCase));
            var check = await learner.ProbeDetailsAsync(domain);
            ProbeResult.Text = RouteAssessment.Describe(domain, check, effective);
            if (!check.Direct.Usable && !check.Vpn.Usable)
                ProbeResult.Text += "\nПроверка маршрутов не прошла. Ручное правило не гарантирует доступность сайта.";
            AppendLog($"Диагностика {domain}: прямой {check.Direct.State}, сервер {check.Vpn.State}.");
        }
        catch (Exception ex) { ProbeResult.Text = "Проверка не завершена: " + ex.Message; }
    }
    private async void RemoveRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesList.SelectedItem is not RouteRule selected) return;
        rules.Remove(selected);
        UserStore.SaveRules(rules);
        RefreshRules();
        DomainBox.Clear();
        RuleLabelBox.Clear();
        RuleNodeBox.SelectedIndex = 0;
        await ReloadRulesSafeAsync();
    }
    private async void Probe_Click(object sender, RoutedEventArgs e)
    {
        string domain = ProbeBox.Text.Trim().ToLowerInvariant();
        if (!VpnCore.IsDomainSafe(domain)) { ProbeResult.Text = "Введите домен, например example.com"; return; }
        if (!core.IsRunning) { ProbeResult.Text = "Сначала подключись."; return; }
        ProbeResult.Text = "Проверяю два независимых маршрута...";
        try
        {
            var check = await learner.ProbeDetailsAsync(domain);
            RouteRule? effective = rules.Where(r => domain == r.Domain || domain.EndsWith("." + r.Domain, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(r => r.Manual).ThenByDescending(r => r.Domain.Length).FirstOrDefault();
            ProbeResult.Text = RouteAssessment.Describe(domain, check, effective);
            AppendLog($"Проверка {domain}: прямой {check.Direct.State}, сервер {check.Vpn.State}.");
        }
        catch (Exception ex) { ProbeResult.Text = ex.Message; }
    }
    private void ObserveDomainFromLog(string line)
    {
        if (!core.IsRunning || settings.Mode != "smart" || AutoLearnBox.IsChecked != true) return;
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
        try
        {
            if (!core.IsTransportHealthy)
            {
                if (recovery.ShouldRecover(false, DateTime.UtcNow))
                {
                    recovery.Failed(DateTime.UtcNow);
                    RecoveryText.Text = $"Восстановление соединения: попытка {recovery.Attempts}/5";
                    AppendLog(RecoveryText.Text);
                    try
                    {
                        await core.StartAsync(nodes, rules, settings);
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
                LatencyText.Text = snapshot.HttpsLatencyMs.HasValue
                    ? $"HTTPS-задержка: {snapshot.HttpsLatencyMs} мс"
                    : "HTTPS-задержка: измерение недоступно";
                UpdateUdp(snapshot);
            }
            else
            {
                var snapshot = await core.ReadMonitorAsync(meter, measureDelay: false);
                UpdateUdp(snapshot);
            }
            recovery.ObserveHealthy(DateTime.UtcNow);
            RecoveryText.Text = "VPN работает • контроль каждые 12 секунд";
        }
        catch (Exception ex)
        {
            RecoveryText.Text = "Диагностика недоступна: " + ex.GetType().Name;
            AppendLog("Монитор соединения: " + ex.Message);
            // Do not restart healthy transport processes merely because an HTTP
            // latency probe or local telemetry request has timed out.
        }
        finally { monitorBusy = false; }
    }

    private void UpdateUdp(MonitorSnapshot snapshot)
    {
        static string Speed(long value) => value >= 1024 * 1024
            ? $"{value / 1048576.0:F2} МБ/с" : $"{value / 1024.0:F1} КБ/с";
        UdpText.Text = $"UDP: {snapshot.UdpConnections} потоков"
            + $" (Discord: {snapshot.DiscordUdpConnections})"
            + $" • ↑ {Speed(snapshot.UdpUpBytesPerSecond)}"
            + $" ↓ {Speed(snapshot.UdpDownBytesPerSecond)}";
    }

    private async Task LearnTickAsync()
    {
        if (testing || !core.IsRunning || settings.Mode != "smart" || AutoLearnBox.IsChecked != true) return;
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