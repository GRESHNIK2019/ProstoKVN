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
    private GitHubUpdate? readyUpdate;
    // Injectable to test startup without contacting a real subscription server.
    public Func<string, Task<SubscriptionImporter.Result>> StartupSubscriptionLoader { get; set; }
        = url => SubscriptionImporter.LoadAsync(url);
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
        profiles = SubscriptionProfileStore.Load(settings);
        RefreshSubscriptionList(profiles.FirstOrDefault());
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
        Closing += async (_, args) =>
        {
            if (shutdownFinalized) return;
            // Delay closing until an in-flight StartAsync releases the gate,
            // otherwise the core can turn the system proxy back on after exit.
            args.Cancel = true;
            isWindowClosing = true;
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
    }

    private void RefreshSubscriptionList(SubscriptionProfile? chosen = null)
    {
        suppressProfileSelection = true;
        string? id = chosen?.Id ?? (SubscriptionList.SelectedItem as SubscriptionProfile)?.Id;
        SubscriptionList.ItemsSource = null;
        SubscriptionList.ItemsSource = profiles.ToList();
        SubscriptionList.SelectedItem = profiles.FirstOrDefault(p => p.Id == id);
        suppressProfileSelection = false;
        DisplaySelectedSubscription();
        ImportSummary.Text = $"{profiles.Count} подписок • {nodes.Count} серверов";
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
        ProfileStatusText.Text = selected == null
            ? "Укажи название и HTTPS-ссылку нового источника"
            : $"{selected.NodeCount} серверов • {selected.DetectedFormat}" +
              (selected.LastUpdatedUtc.HasValue
                  ? $" • {selected.LastUpdatedUtc.Value.ToLocalTime():dd.MM HH:mm}" : "");
    }

    private void SubscriptionList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => DisplaySelectedSubscription();

    private void NewSubscription_Click(object sender, RoutedEventArgs e)
    {
        SubscriptionList.SelectedItem = null;
        SubscriptionNameBox.Focus();
    }

    private void RemoveSubscription_Click(object sender, RoutedEventArgs e)
    {
        if (importing || SubscriptionList.SelectedItem is not SubscriptionProfile selected) return;
        bool confirmed = confirmRemoveSubscriptionOverride != null
            ? confirmRemoveSubscriptionOverride(selected)
            : MessageBox.Show(this,
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
        ImportSummary.Text = $"{profiles.Count} подписок • {nodes.Count} серверов";
    }

    private void StoreImportedProfile(SubscriptionProfile profile,
        SubscriptionImporter.Result imported, bool persist, bool select = true)
    {
        profileNodes[profile.Id] = imported.Nodes;
        profile.NodeCount = imported.Nodes.Count;
        profile.LastUpdatedUtc = DateTime.UtcNow;
        profile.DetectedFormat = imported.DetectedFormat;
        if (persist)
        {
            SubscriptionProfileStore.Save(profiles);
            SubscriptionCache.Save(profile.Url, imported.Nodes);
        }
        RebuildServerList();
        if (select) RefreshSubscriptionList(profile);
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
        try
        {
            int failures = 0;
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
            if (!staleEdit)
                RefreshSubscriptionList(SubscriptionList.SelectedItem as SubscriptionProfile
                    ?? profiles.FirstOrDefault());
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
            ProfileStatusText.Text = "Укажи корректную HTTPS-ссылку.";
            return;
        }
        if (name.Length == 0) name = "Подписка " + (profiles.Count + 1);
        if (profiles.Count >= 30 && SubscriptionList.SelectedItem is not SubscriptionProfile)
        {
            ProfileStatusText.Text = "Максимум 30 подписок.";
            return;
        }

        importing = true;
        ImportButton.IsEnabled = false;
        RefreshButton.IsEnabled = false;
        RefreshAllSubscriptionsButton.IsEnabled = false;
        ProfileStatusText.Text = "Определяю формат и загружаю серверы...";
        try
        {
            var imported = await SubscriptionImporter.LoadAsync(url, format);
            if (isWindowClosing) return;
            var selected = SubscriptionList.SelectedItem as SubscriptionProfile;
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
            StoreImportedProfile(selected, imported, persist: true);
            UserStore.SaveSettings(settings);
            AppendLog($"Добавлена/обновлена подписка «{selected.Name}»: {imported.Nodes.Count} серверов.");
        }
        catch (Exception ex)
        {
            ProfileStatusText.Text = "Не удалось импортировать. Проверь ссылку и формат.";
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
            await core.StartAsync(nodes, rules, settings);
            if (isWindowClosing || !recovery.Desired || revision != connectionRevision)
            {
                core.Stop();
                return false;
            }
            return true;
        }
        finally { connectionGate.Release(); }
    }

    private async void Connect_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
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
            if (nodes.Count == 0) return;
            recovery.Enable();
            int revision = ++connectionRevision;
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
            MessageBox.Show(ex.Message, "ProstoKVN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ConnectButton.IsEnabled = true; }
    }
    private async void Disconnect_Click(object sender, RoutedEventArgs e)
    {
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
            int revision = ++connectionRevision;
            vpnExitVerified = false;
            consecutiveFailedVpnChecks = 0;
            if (!await StartCoreForRevisionAsync(revision)) return;
            learner.ResetExitCountries();
            await VerifyConnectedAsync();
            AppendLog("Сервер переключён: " + settings.SelectedNode);
        }
        catch (Exception ex) { StatusLabel.Text = "Ошибка переключения"; AppendLog("Сбой переключения: " + ex.Message); }
        finally { ConnectButton.IsEnabled = true; }
    }
    private async Task VerifyConnectedAsync()
    {
        if (settings.Mode == "direct")
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
            StatusLabel.Foreground = verification.Verified
                ? System.Windows.Media.Brushes.LightGreen
                : System.Windows.Media.Brushes.Orange;
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
            StatusLabel.Foreground = System.Windows.Media.Brushes.Orange;
            RecoveryText.Text = ConnectionStatusPolicy.MonitorText(false);
            AppendLog("Проверка соединения: " + ex.GetType().Name);
        }
        finally
        {
            // Rate-limit failed checks. Never block normal network traffic.
            if (revision == connectionRevision)
                lastVpnVerificationUtc = DateTime.UtcNow;
            vpnVerificationBusy = false;
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
                if (!isWindowClosing && core.IsRunning)
                    await core.ReloadRoutesAsync(nodes, rules, settings);
            }
            finally { connectionGate.Release(); }
        }
        catch (Exception ex) { if (!isWindowClosing) AppendLog("Не удалось применить правила: " + ex.Message); }
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
        if (SelectedRuleHint == null || RemoveRuleButton == null) return;
        if (RulesList.SelectedItem is not RouteRule selected)
        {
            SelectedRuleHint.Text = "Выбери правило — правый клик откроет действия";
            SelectedRuleHint.ToolTip = null;
            RemoveRuleButton.IsEnabled = false;
            return;
        }
        SelectedRuleHint.Text = "Выбрано: " + selected.Domain;
        SelectedRuleHint.ToolTip = selected.ToString();
        RemoveRuleButton.IsEnabled = true;
        if (RuleLabelBox == null) return;
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

    // A right click must target its own row, not a different previously
    // selected rule. The selection highlight persists while the menu is open.
    private void RuleItem_RightButtonDown(object sender,
        System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is not ListBoxItem { DataContext: RouteRule clicked } row
            || !rules.Contains(clicked))
            return;

        RulesList.SelectedItem = clicked;
        // The empty ContextMenu is declared in XAML so WPF can open it on
        // the very first right-click. Build each item's actions on demand.
        if (row.ContextMenu is not ContextMenu menu) return;
        menu.Items.Clear();
        var edit = new MenuItem { Header = "Изменить правило" };
        edit.Click += RuleContextEdit_Click;
        var delete = new MenuItem { Header = "Удалить правило" };
        delete.Click += RuleContextDelete_Click;
        menu.Items.Add(edit);
        menu.Items.Add(new Separator());
        menu.Items.Add(delete);
    }

    private RouteRule? RuleFromContextMenu(object sender)
    {
        if (sender is MenuItem { Parent: ContextMenu menu }
            && menu.PlacementTarget is ListBoxItem { DataContext: RouteRule clicked }
            && rules.Contains(clicked))
            return clicked;
        return RulesList.SelectedItem as RouteRule;
    }

    private void RuleContextEdit_Click(object sender, RoutedEventArgs e)
    {
        if (RuleFromContextMenu(sender) is not RouteRule rule) return;
        RulesList.SelectedItem = rule;
        AddRuleSection.IsExpanded = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() =>
        {
            RulesPageScroll.ScrollToTop();
            RuleLabelBox.Focus();
            RuleLabelBox.SelectAll();
        }));
    }

    private async void RuleContextDelete_Click(object sender, RoutedEventArgs e)
        => await DeleteRuleAsync(RuleFromContextMenu(sender));
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
    private bool ruleDeleteBusy;
    // Test-only confirmation override for isolated UI regression tests.
    private Func<RouteRule, bool>? confirmDeleteOverride = null;

    private async void RemoveRule_Click(object sender, RoutedEventArgs e)
        => await DeleteRuleAsync(RulesList.SelectedItem as RouteRule);

    private async Task DeleteRuleAsync(RouteRule? selected)
    {
        if (selected == null || !rules.Contains(selected) || ruleDeleteBusy) return;

        string additional = selected.Manual ? ""
            : "\nЭто автоматическое правило может появиться снова при обучении маршрутов.";
        bool confirmed = confirmDeleteOverride != null
            ? confirmDeleteOverride(selected)
            : MessageBox.Show(this,
                $"Удалить правило «{selected.Domain}»?\nМаршрут: " +
                (selected.Route == "VEGA" ? "СЕРВЕР" : selected.Route) +
                additional + "\n\nИзменение будет сохранено и применено к VPN.",
                "Удаление правила • ProstoKVN",
                MessageBoxButton.YesNo, MessageBoxImage.Warning,
                MessageBoxResult.No) == MessageBoxResult.Yes;
        if (!confirmed) return;

        ruleDeleteBusy = true;
        try
        {
            if (!rules.Remove(selected)) return;
            try
            {
                UserStore.SaveRules(rules);
            }
            catch
            {
                rules.Add(selected);
                RefreshRules();
                throw;
            }
            RefreshRules();
            RulesList.SelectedItem = null;
            DomainBox.Clear();
            RuleLabelBox.Clear();
            RuleNodeBox.SelectedIndex = 0;
            AppendLog("Удалено правило: " + selected.Domain);
            await ReloadRulesSafeAsync();
        }
        catch (Exception ex)
        {
            AppendLog("Не удалось удалить правило: " + ex.GetType().Name);
            MessageBox.Show(this,
                "Не удалось сохранить удаление правила. Проверь права доступа к настройкам.",
                "ProstoKVN", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { ruleDeleteBusy = false; }
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
        int monitorRevision = connectionRevision;
        try
        {
            if (!core.IsTransportHealthy)
            {
                vpnExitVerified = false;
                StatusLabel.Text = "Сетевые ядра недоступны • ожидаю восстановления.";
                StatusLabel.Foreground = System.Windows.Media.Brushes.Orange;
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
            if (vpnExitVerified || settings.Mode == "direct")
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
            if (settings.Mode != "direct" && consecutiveFailedVpnChecks >= 3
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
                settings.Mode == "direct");
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