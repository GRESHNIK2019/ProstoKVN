using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace ProstoKVN;

public partial class MainWindow
{
    private readonly Dictionary<string, bool> ruleGroupExpansion = new(StringComparer.Ordinal);
    private bool restoringRuleGroup;

    private void RuleGroup_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not Expander group || group.DataContext is not System.Windows.Data.CollectionViewGroup data) return;
        restoringRuleGroup = true;
        try
        {
            group.IsExpanded = !string.IsNullOrWhiteSpace(RuleSearchBox.Text)
                || !ruleGroupExpansion.TryGetValue(data.Name?.ToString() ?? "", out bool expanded) || expanded;
        }
        finally { restoringRuleGroup = false; }
    }

    private void RuleGroup_ExpansionChanged(object sender, RoutedEventArgs e)
    {
        if (!restoringRuleGroup && string.IsNullOrWhiteSpace(RuleSearchBox.Text)
            && sender is Expander { IsLoaded: true } group
            && group.DataContext is System.Windows.Data.CollectionViewGroup data)
            ruleGroupExpansion[data.Name?.ToString() ?? ""] = group.IsExpanded;
    }

    private bool HasPendingConnectionSettings => core.IsRunning && appliedMode != null
        && (appliedMode != settings.Mode || appliedTun != settings.Tun
            || appliedTunStack != VpnCore.NormalizeTunStack(settings.TunStack)
            || appliedTunDnsHijack != settings.TunDnsHijack
            || appliedTunStrictRoute != settings.TunStrictRoute || rulesNeedReconnect);

    private void Window_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == System.Windows.Input.Key.Escape && AddRuleSection.IsExpanded)
        {
            CloseRuleEditor_Click(sender, new RoutedEventArgs());
            e.Handled = true;
        }
    }

    private void RoutingLink_Click(object sender, RoutedEventArgs e)
    {
        MainTabs.SelectedIndex = 2;
        RulesPageScroll.ScrollToTop();
    }

    private void RuleSource_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded && RulesList != null) RefreshRules();
    }

    private void UpdateRoutingUi()
    {
        RoutingSection.Visibility = settings.Mode == "smart" ? Visibility.Visible : Visibility.Collapsed;
        CoverageModeHint.Text = RoutingPolicy.RequiresTun(settings.Mode)
            ? "Обязателен для режима «Только выбранное»" : settings.Tun ? "Игры, приложения и UDP" : "Приложения с системным прокси";
        ModeDescription.Text = settings.Mode switch
        {
            "vpn" => "Весь интернет через VPN. Ручные исключения могут направлять трафик напрямую или блокировать его.",
            "mine" => "Через VPN идут только включённые вами сайты и приложения. Остальной трафик — напрямую.",
            _ => "Discord, Telegram и AI-сервисы используют VPN. Остальное — по вашим правилам и результатам проверки доступности."
        };
        int count = RoutingPolicy.Select(rules, settings.Mode, true).Count();
        RoutingSummary.Text = settings.Mode switch
        {
            "mine" => $"Только выбранное · {count} включённых правил · По умолчанию: напрямую",
            "vpn" => $"По умолчанию: VPN · Ручных исключений: {count}",
            _ => $"По умолчанию: напрямую · Ручных правил: {count} · Автоматические сервисы: VPN"
        };
        string modeTitle = RoutingModes.Title(settings.Mode);
        CoverageHint.Text = modeTitle + " · " + (settings.Tun ? "Все приложения (TUN)" : "Системный прокси");
        CoverageHint.ToolTip = ModeDescription.Text;
        RulesApplyStatus.Text = rulesApplyError ?? (HasPendingConnectionSettings
            ? "Изменения сохранены. Нажмите «Применить», чтобы переподключиться с новыми настройками."
            : core.IsRunning ? "Правила применены к текущему подключению. Новые соединения используют выбранный маршрут."
                : "Правила сохранены. Они начнут действовать при подключении.");
        ApplyRulesButton.Visibility = HasPendingConnectionSettings ? Visibility.Visible : Visibility.Collapsed;
        RulesApplyStatus.SetResourceReference(TextBlock.ForegroundProperty,
            rulesApplyError != null || HasPendingConnectionSettings ? "StatusWarning" : "TextMuted");
        UpdateYouTubePresetUi();
        RoutingActiveStatus.Text = !core.IsRunning ? "Сейчас: отключено. Правила начнут работать после подключения."
            : HasPendingConnectionSettings ? $"Сейчас: {RoutingModes.Title(appliedMode ?? "smart")}. Есть изменения — нажмите «Применить»."
            : $"Сейчас работает: {RoutingModes.Title(appliedMode ?? settings.Mode)}.";
    }

    private void UpdateYouTubePresetUi()
    {
        if (YouTubePresetCard == null) return;
        YouTubePresetCard.Visibility = settings.Mode == "mine" && !applicationRulePage
            ? Visibility.Visible : Visibility.Collapsed;
        int count = YouTubeRouting.ActiveDomainCount(rules, settings.Mode);
        YouTubePresetStatus.Text = $"Через VPN: {count} из {YouTubeRouting.Domains.Count} адресов сервиса. Ручные исключения имеют приоритет.";
        EnableYouTubeButton.IsEnabled = count < YouTubeRouting.Domains.Count;
        EnableYouTubeButton.Content = EnableYouTubeButton.IsEnabled ? "Включить YouTube" : "YouTube включён";
    }

    private async void EnableYouTube_Click(object sender, RoutedEventArgs e)
    {
        var updated = YouTubeRouting.Enable(rules);
        try { UserStore.SaveRules(updated); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            rulesApplyError = "Не удалось сохранить правила YouTube: " + ex.Message;
            UpdateRoutingUi();
            return;
        }
        rules = updated;
        RuleSourceBox.SelectedIndex = 0;
        RuleSearchBox.Text = "YouTube";
        RefreshRules();
        AppendLog("Включён набор YouTube: сайт, видеосерверы, обложки и API. Ручные исключения сохранены.");
        await ReloadRulesSafeAsync();
    }

    private void RuleSearch_Changed(object sender, TextChangedEventArgs e)
    {
        if (RulesList == null || RuleSearchBox == null) return;
        if (applicationRulePage) applicationRuleQuery = RuleSearchBox.Text;
        else siteRuleQuery = RuleSearchBox.Text;
        RefreshRules();
    }

    private void SitesRules_Click(object sender, RoutedEventArgs e) => SwitchRuleCategory(false);
    private void ApplicationsRules_Click(object sender, RoutedEventArgs e) => SwitchRuleCategory(true);

    private void UpdateRuleCategoryButtons()
    {
        if (SitesRulesButton == null || ApplicationsRulesButton == null) return;
        SitesRulesButton.FontWeight = applicationRulePage ? FontWeights.Normal : FontWeights.SemiBold;
        ApplicationsRulesButton.FontWeight = applicationRulePage ? FontWeights.SemiBold : FontWeights.Normal;
        SitesRulesButton.SetResourceReference(Control.BackgroundProperty,
            applicationRulePage ? "SurfaceRaised" : "AccentSoft");
        ApplicationsRulesButton.SetResourceReference(Control.BackgroundProperty,
            applicationRulePage ? "AccentSoft" : "SurfaceRaised");
        SitesRulesButton.SetResourceReference(Control.BorderBrushProperty,
            applicationRulePage ? "Line" : "AccentSolid");
        ApplicationsRulesButton.SetResourceReference(Control.BorderBrushProperty,
            applicationRulePage ? "AccentSolid" : "Line");
    }

    private void SwitchRuleCategory(bool applications)
    {
        if (RuleSearchBox == null || RulesList == null || applicationRulePage == applications) return;
        if (applicationRulePage) applicationRuleQuery = RuleSearchBox.Text;
        else siteRuleQuery = RuleSearchBox.Text;
        applicationRulePage = applications;
        RulesList.SelectedItem = null;
        editingRule = null;
        AddRuleSection.IsExpanded = false;
        DomainBox.Clear();
        RuleLabelBox.Clear();
        MyRuleEnabledBox.IsChecked = true;
        DomainScopeBox.SelectedIndex = 0;
        RuleTargetKindBox.SelectedIndex = applications ? 1 : 0;
        RuleTargetKindLabel.Text = applications ? "Приложения" : "Сайты и IP";
        RuleSearchLabel.Text = applications ? "Поиск приложений" : "Поиск сайтов и IP";
        RuleSearchBox.ToolTip = applications ? "Имя процесса, путь к .exe или название"
            : "Домен, IP-адрес или название сайта";
        NewRuleButton.Content = applications ? "Добавить приложение" : "Добавить сайт или IP";
        RuleGroupsSection.Header = applications ? "Приложения — сохранённые правила"
            : "Сайты и IP — сохранённые правила";
        RulesEmptyText.Text = applications
            ? "Приложений пока нет. Добавьте программу, чтобы настроить её маршрут."
            : "Сайты и IP не найдены. Измените поиск или добавьте правило.";
        RuleSearchBox.Text = applications ? applicationRuleQuery : siteRuleQuery;
        UpdateRuleCategoryButtons();
        UpdateYouTubePresetUi();
        RefreshRules();
        RulesPageScroll.ScrollToTop();
    }

    private void NewRule_Click(object sender, RoutedEventArgs e)
    {
        BypassTunBox.IsChecked = false;
        RulesList.SelectedItem = null;
        editingRule = null;
        RuleTargetKindBox.SelectedIndex = applicationRulePage ? 1 : 0;
        MyRuleEnabledBox.IsChecked = true;
        DomainScopeBox.SelectedIndex = 0;
        DomainBox.Clear();
        RuleLabelBox.Clear();
        RuleRouteBox.SelectedIndex = 1;
        RuleNodeBox.SelectedIndex = 0;
        RuleValidationText.Text = "";
        RuleAdvancedSection.IsExpanded = false;
        AddRuleSection.Header = "Новое правило";
        AddRuleSection.IsExpanded = true;
        DomainBox.BringIntoView();
        DomainBox.Focus();
    }

    private void EditRule_Click(object sender, RoutedEventArgs e)
    {
        if (RulesList.SelectedItem is not RouteRule selected) return;
        LoadRuleEditor(selected);
        RuleValidationText.Text = "";
        RuleAdvancedSection.IsExpanded = !string.IsNullOrWhiteSpace(editingRule?.Label) || !string.IsNullOrWhiteSpace(editingRule?.PreferredNode);
        AddRuleSection.Header = "Изменить правило";
        AddRuleSection.IsExpanded = true;
        DomainBox.BringIntoView();
        DomainBox.Focus();
    }

    private void CloseRuleEditor_Click(object sender, RoutedEventArgs e)
    {
        AddRuleSection.IsExpanded = false;
        NewRuleButton.Focus();
    }

    private void RefreshRules()
    {
        double savedOffset = RulesPageScroll?.VerticalOffset ?? 0;
        RouteRule? selected = RulesList.SelectedItem as RouteRule;
        RulesList.ItemsSource = null;
        string query = RuleSearchBox?.Text.Trim() ?? "";
        var ordered = rules.Where(x => (RouteTarget.TryParse(x.Domain, out var target) && target.IsApplication) == applicationRulePage)
            .Where(x => query.Length == 0
                || x.Domain.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.ServiceName.Contains(query, StringComparison.OrdinalIgnoreCase)
                || x.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
            .OrderBy(x => x.ServiceName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(x => x.DisplayTarget, StringComparer.CurrentCultureIgnoreCase)
            .ThenByDescending(x => x.Manual).ToList();
        int source = RuleSourceBox?.SelectedIndex ?? 0;
        if (source == 0) ordered = ordered.Where(x => x.Manual || x.MyRuleEnabled == true).ToList();
        else if (source == 1) ordered = ordered.Where(x => !x.Manual).ToList();
        var view = new System.Windows.Data.ListCollectionView(ordered);
        if (!applicationRulePage)
            view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(RouteRule.ServiceName)));
        RulesList.ItemsSource = view;
        if (RuleCountText != null) RuleCountText.Text = $"Показано: {ordered.Count} · Всего сохранено: {rules.Count}";
        if (RulesEmptyText != null)
            RulesEmptyText.Visibility = ordered.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (selected != null && ordered.Contains(selected)) RulesList.SelectedItem = selected;
        if (savedOffset > 0)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,
                new Action(() => RulesPageScroll?.ScrollToVerticalOffset(savedOffset)));
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
        UpdateDomainScopeVisibility();
    }
    private bool IsApplicationRule => RuleTargetKindBox.SelectedIndex == 1;

    private void RuleTargetKind_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (ChooseApplicationButton == null || DomainBox == null || ApplicationRuleHint == null) return;
        bool application = IsApplicationRule;
        ChooseApplicationButton.Visibility = ApplicationRuleHint.Visibility = application
            ? Visibility.Visible : Visibility.Collapsed;
        RuleAddressHint.Visibility = application ? Visibility.Collapsed : Visibility.Visible;
        RuleTargetCaption.Text = application ? "Путь к приложению или имя процесса"
            : "Адрес сайта или IP";
        DomainBox.ToolTip = application ? "Полный путь к .exe или имя процесса, например chrome.exe"
            : "Домен: example.com • IP: 8.8.8.8 • Подсеть: 1.2.3.0/24 • IPv6: 2001:db8::/32";
        System.Windows.Automation.AutomationProperties.SetName(DomainBox,
            application ? "Путь или имя приложения" : "Сайт, IP-адрес или подсеть");
        UpdateDomainScopeVisibility();
        if (application && IsLoaded && AddRuleSection.IsExpanded)
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => DomainBox.BringIntoView()));
    }

    private void DomainBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (RuleValidationText != null) RuleValidationText.Text = "";
        UpdateDomainScopeVisibility();
    }

    private void UpdateDomainScopeVisibility()
    {
        if (DomainScopeSection == null || DomainBox == null || RuleTargetKindBox == null)
            return;
        bool isDomain = !IsApplicationRule
            && RouteTarget.TryParseUserInput(DomainBox.Text, out var target) && target.IsDomain;
        DomainScopeSection.Visibility = isDomain ? Visibility.Visible : Visibility.Collapsed;
        if (BypassTunSection != null)
            BypassTunSection.Visibility = !IsApplicationRule && RouteTarget.TryParseUserInput(DomainBox.Text, out var address)
                && address.IsAddress && ((RuleRouteBox.SelectedItem as ComboBoxItem)?.Tag as string) == "DIRECT"
                    ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ChooseApplication_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = "Выберите приложение для правила",
            Filter = "Приложения Windows (*.exe)|*.exe",
            CheckFileExists = true,
            Multiselect = false
        };
        if (picker.ShowDialog(this) != true) return;
        if (!RouteTarget.TryParseApplication(picker.FileName, out var target))
        {
            ClientDialog.Show(this,"Этот путь нельзя использовать в правиле. Выберите .exe без запятых в пути.",
                "ProstoKVN", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DomainBox.Text = target.DisplayValue;
        if (string.IsNullOrWhiteSpace(RuleLabelBox.Text))
            RuleLabelBox.Text = System.IO.Path.GetFileNameWithoutExtension(picker.FileName);
    }
    private void RulesList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (SelectedRuleHint == null || RemoveRuleButton == null) return;
        if (RulesList.SelectedItem is not RouteRule selected)
        {
            editingRule = null;
            SelectedRuleHint.Text = "Выберите правило для изменения или удаления";
            SelectedRuleHint.ToolTip = null;
            RemoveRuleButton.IsEnabled = false;
            EditRuleButton.IsEnabled = false;
            return;
        }
        editingRule = selected;
        SelectedRuleHint.Text = "Выбрано: " + selected.DisplayTarget;
        SelectedRuleHint.ToolTip = selected.ToString();
        RemoveRuleButton.IsEnabled = true;
        EditRuleButton.IsEnabled = true;
        if (RuleLabelBox == null) return;
        LoadRuleEditor(selected);
    }

    private void LoadRuleEditor(RouteRule selected)
    {
        editingRule = selected;
        bool application = RouteTarget.TryParse(selected.Domain, out var selectedTarget) && selectedTarget.IsApplication;
        RuleTargetKindBox.SelectedIndex = application ? 1 : 0;
        DomainBox.Text = selected.DisplayTarget;
        DomainScopeBox.SelectedIndex = selectedTarget.Kind == RouteTargetKind.DomainExact ? 1 : 0;
        UpdateDomainScopeVisibility();
        RuleLabelBox.Text = selected.Label ?? "";
        MyRuleEnabledBox.IsChecked = selected.IncludedInMine;
        BypassTunBox.IsChecked = selected.BypassTun;
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
        EditRule_Click(sender, e);
    }

    private async void RuleContextDelete_Click(object sender, RoutedEventArgs e)
        => await DeleteRuleAsync(RuleFromContextMenu(sender));
    private async void AddRule_Click(object sender, RoutedEventArgs e)
    {
        RouteTarget target;
        bool valid = IsApplicationRule
            ? RouteTarget.TryParseApplication(DomainBox.Text, out target)
            : RouteTarget.TryParseUserInput(DomainBox.Text, out target) && !target.IsApplication;
        if (valid && !IsApplicationRule && target.Kind == RouteTargetKind.Domain
            && DomainScopeBox.SelectedIndex == 1)
            valid = RouteTarget.TryParse("domain-exact:" + target.Value, out target);
        if (!valid)
        {
            RuleValidationText.Text = IsApplicationRule
                ? "Выберите .exe или введите имя процесса (chrome.exe) либо полный путь. Запятые и маски не поддерживаются."
                : "Введите домен, ссылку на сайт, IP или подсеть. Например: example.com, 8.8.8.8, 1.2.3.0/24.";
            DomainBox.Focus();
            return;
        }
        string route = ((RuleRouteBox.SelectedItem as ComboBoxItem)?.Tag as string) ?? "DIRECT";
        string preferred = route == "VEGA" && RuleNodeBox.SelectedIndex > 0
            && RuleNodeBox.SelectedIndex <= nodes.Count
            ? nodes[RuleNodeBox.SelectedIndex - 1].Name : "";
        if (!string.IsNullOrWhiteSpace(preferred))
        {
            var preferredNode = nodes.First(n => n.Name == preferred);
            var selectedNode = nodes.FirstOrDefault(n => n.Name == settings.SelectedNode) ?? nodes.FirstOrDefault();
            if (preferredNode != selectedNode && EngineSelector.Choose(preferredNode) == TunnelEngine.Xray)
            {
                RuleValidationText.Text = "Для этого сервера нужен отдельный Xray. Выберите его основным на экране подключения или используйте основной сервер в правиле.";
                return;
            }
        }
        var updatedRules = rules.ToList();
        updatedRules.RemoveAll(x => ReferenceEquals(x, editingRule) || (RouteTarget.TryParse(x.Domain, out var oldTarget)
            && oldTarget.Kind == target.Kind && oldTarget.Value.Equals(target.Value, StringComparison.OrdinalIgnoreCase)));
        updatedRules.Add(new RouteRule
        {
            Domain = target.Value,
            Label = RuleLabelBox.Text.Trim(),
            Route = route,
            PreferredNode = preferred,
            Manual = true,
            MyRuleEnabled = MyRuleEnabledBox.IsChecked == true,
            BypassTun = route == "DIRECT" && target.IsAddress && BypassTunBox.IsChecked == true
        });
        try { UserStore.SaveRules(updatedRules); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            RuleValidationText.Text = "Не удалось сохранить правило: " + ex.Message;
            return;
        }
        rules.Clear();
        rules.AddRange(updatedRules);
        RefreshRules();
        DomainBox.Clear();
        RuleLabelBox.Clear();
        RulesList.SelectedItem = null;
        RuleNodeBox.SelectedIndex = 0;
        MyRuleEnabledBox.IsChecked = true;
        AppendLog($"Добавлено исключение: {target.DisplayKind} {target.DisplayValue} → "
            + (route == "VEGA" ? "СЕРВЕР" : route));
        if (target.IsApplication && !settings.Tun)
            AppendLog("Для перехвата всех соединений приложения включите TUN в настройках подключения и переподключитесь с правами администратора.");
        AddRuleSection.IsExpanded = false;
        await ReloadRulesSafeAsync();
    }
    private async void RuleEnabled_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { DataContext: RouteRule rule } box || !rules.Contains(rule)) return;
        var updated = rules.Select(r => r.Snapshot()).ToList();
        updated[rules.IndexOf(rule)].MyRuleEnabled = box.IsChecked == true;
        try { UserStore.SaveRules(updated); }
        catch (Exception ex) when (ex is System.IO.IOException or UnauthorizedAccessException)
        {
            rulesApplyError = "Не удалось сохранить выбор: " + ex.Message;
            RefreshRules(); UpdateRoutingUi(); return;
        }
        rules = updated;
        RefreshRules();
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
            : ClientDialog.Show(this,
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
            ClientDialog.Show(this,
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
            string mode = activeSettings?.Mode ?? settings.Mode;
            RouteRule? effective = RoutingPolicy.Select(rules, mode, true)
                .Concat(RoutingPolicy.Select(rules, mode, false))
                .FirstOrDefault(r => RouteTarget.TryParse(r.Domain, out var target) && target.Matches(domain, null));
            ProbeResult.Text = RouteAssessment.Describe(domain, check, effective);
            AppendLog($"Проверка {domain}: прямой {check.Direct.State}, сервер {check.Vpn.State}.");
        }
        catch (Exception ex) { ProbeResult.Text = ex.Message; }
    }
}
