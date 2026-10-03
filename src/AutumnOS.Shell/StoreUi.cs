using AutumnOS.Contracts;
using AutumnOS.Store;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace AutumnOS.Shell;

public sealed partial class MainWindow
{
    private IStoreCatalog? storeCatalog;
    private readonly StackPanel storeBody = new() { Spacing = 18 };
    private readonly TextBlock storeStatus = new() { TextWrapping = TextWrapping.Wrap, Opacity = 0.75 };
    private readonly TextBlock storeHeading = new() { Text = "发现", FontSize = 30, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
    private readonly TextBlock storeSource = new() { Text = "GitHub 公开应用", FontSize = 12, Opacity = 0.65 };
    private readonly TextBox storeSearch = new() { PlaceholderText = "搜索公开应用", MaxLength = 100, MinWidth = 180 };
    private readonly ComboBox storeCategory = StoreChoice("StoreCategory", "所有分类", "PM · 仓库自标", "SQ · 社区自标", "未分类", "标签冲突");
    private readonly ComboBox storeInstalled = StoreChoice("StoreInstalledFilter", "全部应用", "已安装", "尚未安装", "有更新（已读详情）");
    private readonly ComboBox storeCompatibility = StoreChoice("StoreCompatibility", "所有兼容状态", "有兼容版本", "无兼容版本");
    private readonly ComboBox storeOffline = StoreChoice("StoreOffline", "所有离线能力", "支持离线", "需要网络");
    private readonly ComboBox storeRuntime = StoreChoice("StoreRuntime", "所有运行环境", "Web");
    private readonly TextBox storeOwner = new() { PlaceholderText = "来源 owner/name", MaxLength = 100, Width = 160 };
    private readonly List<CatalogRepository> storeRepositories = [];
    private readonly Dictionary<long, CatalogDetails> storeDetails = [];
    private readonly DispatcherTimer storeDebounce = new() { Interval = TimeSpan.FromMilliseconds(450) };
    private CancellationTokenSource? storeQuery;
    private long storeRevision;
    private int storePage;
    private bool storeHasMore, storeBusy;
    private string storeSection = "discover";
    private bool renderingStoreDiscovery;
    private readonly Grid storeFilters = new() { RowSpacing = 10, ColumnSpacing = 10 };

    private static ComboBox StoreChoice(string id, params string[] options)
    {
        var box = new ComboBox { MinWidth = 130, MaxWidth = 230 };
        foreach (var text in options) box.Items.Add(text);
        box.SelectedIndex = 0;
        AutomationProperties.SetAutomationId(box, id);
        return box;
    }

    private void InitializeStoreUi()
    {
        AutomationProperties.SetAutomationId(storeStatus, "StoreStatus");
        AutomationProperties.SetAutomationId(storeHeading, "StoreHeading");
        AutomationProperties.SetAutomationId(storeSearch, "StoreSearch");
        AutomationProperties.SetAutomationId(storeOwner, "StoreOwnerFilter");
        AutomationProperties.SetAutomationId(storeSource, "StoreSource");
        for (int row = 0; row < 2; row++) storeFilters.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        for (int column = 0; column < 3; column++) storeFilters.ColumnDefinitions.Add(new ColumnDefinition());
        int filterPosition = 0;
        foreach (var element in new FrameworkElement[] { storeCategory, storeInstalled, storeCompatibility, storeOffline, storeRuntime, storeOwner })
        {
            Grid.SetRow(element, filterPosition / 3); Grid.SetColumn(element, filterPosition++ % 3);
            element.HorizontalAlignment = HorizontalAlignment.Stretch; storeFilters.Children.Add(element);
        }
        StorePanel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(205) });
        StorePanel.ColumnDefinitions.Add(new ColumnDefinition());
        var sidebar = new StackPanel { Padding = new Thickness(18, 22, 18, 24), Spacing = 12 };
        sidebar.Children.Add(ActionButton("‹  桌面", "StoreHome", OnBackground));
        sidebar.Children.Add(new TextBlock { Text = "Autumn\nStore", FontSize = 29, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, Margin = new Thickness(8, 24, 0, 20) });
        foreach (var (key, title, icon) in new[] { ("discover", "发现", "\uE721"), ("installed", "我的应用", "\uE8F1"), ("downloads", "下载任务", "\uE896") })
        {
            var label = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
            label.Children.Add(new FontIcon { Glyph = icon, FontSize = 17 }); label.Children.Add(new TextBlock { Text = title });
            var button = ActionButton(title, "StoreNav-" + key, (_, _) => ShowStoreSection(key));
            button.Content = label; button.HorizontalAlignment = HorizontalAlignment.Stretch;
            button.HorizontalContentAlignment = HorizontalAlignment.Left;
            sidebar.Children.Add(button);
        }
        sidebar.Children.Add(new Border { Height = 1, Background = (Brush)Application.Current.Resources["DividerStrokeColorDefaultBrush"], Margin = new Thickness(4, 12, 4, 10) });
        sidebar.Children.Add(ActionButton("安装 .autumn", "StoreLocalInstall", OnLocalPackageInstall));
        sidebar.Children.Add(ActionButton("网络与下载", "StoreNetworkSettings", (_, e) => { SelectSettingsCategory("github"); OnSettings(StorePanel, e); }));
        sidebar.Children.Add(Paragraph(BrandInfo.ProducerCredit));
        var sideSurface = new Border { Background = (Brush)Root.Resources["SidebarSurface"], Child = new ScrollViewer { Content = sidebar, HorizontalScrollMode = ScrollMode.Disabled } };
        StorePanel.Children.Add(sideSurface);
        var main = new Grid { Padding = new Thickness(28, 24, 28, 24), RowSpacing = 16 };
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        main.RowDefinitions.Add(new RowDefinition());
        var heading = new Grid { ColumnSpacing = 20 };
        heading.ColumnDefinitions.Add(new ColumnDefinition()); heading.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(270) });
        var headingWords = new StackPanel { Spacing = 4 }; headingWords.Children.Add(storeHeading); headingWords.Children.Add(storeSource);
        heading.Children.Add(headingWords); Grid.SetColumn(storeSearch, 1); storeSearch.VerticalAlignment = VerticalAlignment.Center; heading.Children.Add(storeSearch);
        main.Children.Add(heading); Grid.SetRow(storeStatus, 1); main.Children.Add(storeStatus);
        var scroll = new ScrollViewer { Content = storeBody, HorizontalScrollMode = ScrollMode.Disabled, HorizontalContentAlignment = HorizontalAlignment.Stretch };
        Grid.SetRow(scroll, 2); main.Children.Add(scroll); Grid.SetColumn(main, 1); StorePanel.Children.Add(main);
        storeDebounce.Tick += async (_, _) => { storeDebounce.Stop(); await SearchStoreAsync(false); };
        storeSearch.TextChanged += (_, _) => { if (storeSection != "discover") return; storeDebounce.Stop(); storeQuery?.Cancel(); storeDebounce.Start(); };
        foreach (var filter in new[] { storeCategory, storeInstalled, storeCompatibility, storeOffline, storeRuntime }) filter.SelectionChanged += (_, _) => { if (storeSection == "discover") RenderStoreDiscovery(); };
        storeOwner.TextChanged += (_, _) => { if (storeSection == "discover") RenderStoreDiscovery(); };
        StorePanel.AllowDrop = true;
        StorePanel.DragOver += OnStoreDragOver; StorePanel.Drop += OnStoreDrop;
        InitializeNetworkSettingsUi();
        InitializeManagedAppUi();
    }

    private void OnStore(object sender, RoutedEventArgs e)
    {
        if (ConsumeHeldClick(sender)) return;
        appHost?.Background(); developerPreviewHost?.Background(); BackgroundManagedApps();
        ShowPage(StorePanel); ShowStoreSection("discover");
    }
    private void OnDownloads(object sender, RoutedEventArgs e)
    {
        if (ConsumeHeldClick(sender)) return;
        appHost?.Background(); developerPreviewHost?.Background(); BackgroundManagedApps();
        ShowPage(StorePanel); ShowStoreSection("downloads");
    }
    private void ShowStoreSection(string section)
    {
        storeDebounce.Stop(); storeQuery?.Cancel(); storeRevision++;
        storeSection = section; storeBusy = false;
        storeHeading.Text = section switch { "downloads" => "下载任务", "installed" => "我的应用", _ => "发现" };
        storeSearch.Visibility = section == "discover" ? Visibility.Visible : Visibility.Collapsed;
        if (section == "downloads") RenderDownloadTasks();
        else if (section == "installed") RenderInstalledApplications();
        else if (storeRepositories.Count == 0) _ = SearchStoreAsync(false);
        else RenderStoreDiscovery();
    }
    private async Task SearchStoreAsync(bool nextPage)
    {
        if (storeCatalog is null || (nextPage && (storeBusy || !storeHasMore))) return;
        storeQuery?.Cancel(); storeQuery?.Dispose();
        storeQuery = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = storeQuery.Token; long revision = ++storeRevision;
        storeBusy = true; storeStatus.Text = "正在读取 GitHub 公开仓库…";
        if (!nextPage) { storeRepositories.Clear(); storePage = 0; RenderStoreDiscovery(); }
        try
        {
            var page = await storeCatalog.SearchAsync(storeSearch.Text, nextPage ? storePage + 1 : 1, token);
            if (token.IsCancellationRequested || revision != storeRevision || storeSection != "discover") return;
            foreach (var item in page.Repositories) if (storeRepositories.All(x => x.RepositoryId != item.RepositoryId)) storeRepositories.Add(item);
            storePage = page.Page; storeHasMore = page.HasMore;
            storeStatus.Text = $"{(page.IsCached ? "缓存" : "已读取")} {storeRepositories.Count} 个仓库 · {page.FetchedAt.ToLocalTime():MM-dd HH:mm}" +
                (page.IncompleteResults ? " · GitHub 返回了不完整结果" : "") + (page.WarningCode is { } warning ? " · " + StoreError(warning) : "") + "\n筛选作用于已加载结果；兼容、离线和更新筛选需要先读取应用详情。";
        }
        catch (OperationCanceledException) { if (revision == storeRevision) storeStatus.Text = "搜索已取消。"; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (revision == storeRevision) storeStatus.Text = StoreError(error is CatalogException ce ? ce.Code : "STORE_NETWORK_ERROR") + " · 可重试；网络失败不代表没有应用。"; }
        finally { if (revision == storeRevision) { storeBusy = false; RenderStoreDiscovery(); } }
    }
    private void RenderStoreDiscovery()
    {
        if (storeSection != "discover" || renderingStoreDiscovery) return;
        renderingStoreDiscovery = true;
        try
        {
        storeBody.Children.Clear();
        var hero = new StackPanel { Spacing = 9 };
        hero.Children.Add(new TextBlock { Text = "让好奇心，多一个入口。", FontSize = 27, Foreground = Brush("29493E"), FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        hero.Children.Add(new TextBlock { Text = "探索社区里的小小世界，在你的桌面继续。", FontSize = 14, Foreground = Brush("466457"), TextWrapping = TextWrapping.Wrap });
        hero.Children.Add(new TextBlock { Text = "公开浏览 · 无需登录", FontSize = 12, Foreground = Brush("466457"), Margin = new Thickness(0, 12, 0, 0) });
        storeBody.Children.Add(new Border { CornerRadius = new CornerRadius(24), Padding = new Thickness(26), Background = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(1, 1), GradientStops = { new GradientStop { Offset = 0, Color = Color("DFEBD9") }, new GradientStop { Offset = 1, Color = Color("EEE5D6") } } }, Child = hero });
        storeBody.Children.Add(storeFilters);
        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        var refresh = ActionButton("刷新", "StoreRefresh", async (_, _) => await SearchStoreAsync(false)); refresh.IsEnabled = !storeBusy;
        var cancel = ActionButton("取消读取", "StoreCancelSearch", (_, _) => storeQuery?.Cancel()); cancel.IsEnabled = storeBusy;
        actions.Children.Add(refresh); actions.Children.Add(cancel); storeBody.Children.Add(actions);
        int shown = 0;
        foreach (var repository in storeRepositories.Where(MatchesStoreFilters))
        {
            shown++;
            storeDetails.TryGetValue(repository.RepositoryId, out var details);
            var content = new Grid { ColumnSpacing = 18, HorizontalAlignment = HorizontalAlignment.Stretch };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto }); content.ColumnDefinitions.Add(new ColumnDefinition()); content.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            content.Children.Add(new Border { Width = 56, Height = 56, CornerRadius = new CornerRadius(17), Background = Brush("DCE6DC"), Child = new FontIcon { Glyph = "\uE8F1", Foreground = Brush("4D715B"), FontSize = 26 } });
            var words = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
            words.Children.Add(new TextBlock { Text = details?.Store?.Name ?? repository.Name, FontSize = 18, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis });
            words.Children.Add(new TextBlock { Text = details?.Store?.Description ?? repository.Description, MaxLines = 2, TextWrapping = TextWrapping.Wrap, Opacity = 0.75 });
            words.Children.Add(new TextBlock { Text = (details?.Store?.Developer.Name ?? repository.Owner) + " · " + repository.CategoryLabel, FontSize = 11, Opacity = 0.65, TextWrapping = TextWrapping.Wrap });
            Grid.SetColumn(words, 1); content.Children.Add(words);
            var label = new TextBlock { Text = StoreInstalledLabel(details), VerticalAlignment = VerticalAlignment.Center, FontSize = 12 }; Grid.SetColumn(label, 2); content.Children.Add(label);
            var card = new Button { Content = content, HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch, Padding = new Thickness(18), CornerRadius = new CornerRadius(20) };
            AutomationProperties.SetAutomationId(card, "StoreRepository-" + repository.RepositoryId);
            card.Click += async (_, _) => await OpenStoreDetailsAsync(repository); storeBody.Children.Add(card);
        }
        if (shown == 0) storeBody.Children.Add(StoreEmpty(storeBusy ? "正在寻找新的发现" : storeRepositories.Count == 0 ? "这里还没有搜索结果" : "没有符合当前筛选的应用", storeBusy ? "正在读取真实公开仓库，请稍候。" : "可以更换关键词、清除筛选或稍后刷新。商店不会填入演示商品。"));
        if (storeHasMore) { var more = ActionButton("加载下一页", "StoreMore", async (_, _) => await SearchStoreAsync(true)); more.IsEnabled = !storeBusy; storeBody.Children.Add(more); }
        }
        catch (Exception error) when (error is not OutOfMemoryException)
        { storeStatus.Text = "STORE_VIEW_FAILED · " + error.GetType().Name + " · " + error.HResult.ToString("X8"); }
        finally { renderingStoreDiscovery = false; }
    }
    private bool MatchesStoreFilters(CatalogRepository repository)
    {
        if (storeCategory.SelectedIndex > 0 && repository.Category != new[] { CatalogCategory.Unclassified, CatalogCategory.Pm, CatalogCategory.Sq, CatalogCategory.Unclassified, CatalogCategory.Conflict }[storeCategory.SelectedIndex]) return false;
        if (!repository.FullName.Contains(storeOwner.Text.Trim(), StringComparison.OrdinalIgnoreCase)) return false;
        storeDetails.TryGetValue(repository.RepositoryId, out var details);
        bool installed = StoreHasInstalledRepository(repository.RepositoryId);
        if (storeInstalled.SelectedIndex == 1 && !installed || storeInstalled.SelectedIndex == 2 && installed || storeInstalled.SelectedIndex == 3 && !StoreHasUpdate(details)) return false;
        if (storeCompatibility.SelectedIndex > 0 && (details is null || details.Versions.Any(v => v.CanInstall) != (storeCompatibility.SelectedIndex == 1))) return false;
        if (storeOffline.SelectedIndex > 0 && (details?.Store is null || details.Store.OfflineCapable != (storeOffline.SelectedIndex == 1))) return false;
        return storeRuntime.SelectedIndex == 0 || details?.Versions.Any(v => v.Manifest?.Runtime == "web") == true;
    }
    private async Task OpenStoreDetailsAsync(CatalogRepository repository)
    {
        if (storeCatalog is null) return;
        storeDebounce.Stop(); storeQuery?.Cancel(); storeQuery?.Dispose(); storeQuery = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        var token = storeQuery.Token; long revision = ++storeRevision; storeSection = "detail";
        storeHeading.Text = repository.Name; storeSearch.Visibility = Visibility.Collapsed; storeBody.Children.Clear();
        storeBody.Children.Add(ActionButton("‹  返回发现", "StoreBack", (_, _) => ShowStoreSection("discover")));
        storeBody.Children.Add(ActionButton("取消读取", "StoreCancelDetails", (_, _) => storeQuery?.Cancel()));
        storeStatus.Text = "正在读取应用清单与版本…";
        try
        {
            var details = await storeCatalog.GetDetailsAsync(repository, token);
            if (token.IsCancellationRequested || revision != storeRevision) return;
            storeVersionPages[repository.RepositoryId] = 1;
            storeDetails[repository.RepositoryId] = details; RenderStoreDetails(details);
        }
        catch (OperationCanceledException) { if (revision == storeRevision) storeStatus.Text = "详情读取已取消，可以返回发现。"; }
        catch (Exception error) when (error is not OutOfMemoryException)
        { if (revision == storeRevision) storeStatus.Text = StoreError(error is CatalogException ce ? ce.Code : "STORE_NETWORK_ERROR"); }
    }
    private void RenderStoreDetails(CatalogDetails details)
    {
        storeSection = "detail"; storeBody.Children.Clear();
        storeHeading.Text = details.Store?.Name ?? details.Repository.Name;
        storeStatus.Text = (details.IsCached ? "缓存资料 · " : "公开资料 · ") + details.FetchedAt.ToLocalTime().ToString("MM-dd HH:mm") + " · " + details.Repository.CategoryLabel;
        storeBody.Children.Add(ActionButton("‹  返回发现", "StoreBack", (_, _) => ShowStoreSection("discover")));
        storeBody.Children.Add(Paragraph(details.Store?.Description ?? details.Repository.Description));
        storeBody.Children.Add(Paragraph("开发者：" + (details.Store?.Developer.Name ?? details.Repository.Owner) + "\n来源：" + details.Repository.FullName + "\n应用 ID：" + (details.Store?.AppId ?? "清单不可用") + "\nrepositoryId：" + details.Repository.RepositoryId));
        storeBody.Children.Add(Paragraph("分类由仓库自行标注。安装校验不会授予运行时权限；一般社区应用的运行隔离仍待验证，当前可浏览、下载和安装，运行入口会说明限制。"));
        foreach (var warning in details.Warnings) storeBody.Children.Add(Paragraph(StoreError(warning)));
        AddStoreScreenshots(details);
        var channels = StoreChoice("StoreVersionChannel", "稳定版本", "稳定与预览", "仅预览");
        channels.SelectedIndex = StoreAllowsPreview(details) ? 1 : 0;
        var compatible = new CheckBox { Content = "只显示兼容版本", IsChecked = false }; AutomationProperties.SetAutomationId(compatible, "StoreOnlyCompatible");
        var versions = new StackPanel { Spacing = 12 };
        storeBody.Children.Add(new TextBlock { Text = "选择版本", FontSize = 22, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
        storeBody.Children.Add(channels); storeBody.Children.Add(compatible); storeBody.Children.Add(versions);
        void RenderVersions()
        {
            versions.Children.Clear();
            foreach (var version in details.Versions.Where(v => channels.SelectedIndex == 1 || v.Prerelease == (channels.SelectedIndex == 2)).Where(v => compatible.IsChecked != true || v.Compatible))
            {
                var content = new StackPanel { Spacing = 10 };
                content.Children.Add(new TextBlock { Text = version.Version + (version.Prerelease ? "  ·  预览" : "  ·  稳定"), FontSize = 19, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold });
                content.Children.Add(Paragraph(version.PublishedAt.ToLocalTime().ToString("yyyy-MM-dd") + "\n" + version.Notes));
                if (version.Manifest is { } manifest)
                    content.Children.Add(Paragraph($"{manifest.Runtime} · {manifest.Bytes / 1024.0:N1} KiB · 最低宿主 {manifest.MinHostVersion} / SDK {manifest.MinSdkVersion}\n权限：{string.Join("、", manifest.Permissions.Select(PermissionTitle))}\n" + StorePermissionChanges(manifest)));
                if (!version.CanInstall) content.Children.Add(Paragraph(StoreError(version.UnavailableReason ?? "RELEASE_INCOMPATIBLE")));
                var install = ActionButton(StoreVersionActionLabel(details, version), "StoreInstall-" + version.ReleaseId, async (_, _) => await BeginStoreInstallAsync(details, version));
                bool sourceConflict = details.Store is { } product && applicationInstaller?.Find(product.AppId) is { } installed && !StoreSourceMatches(installed, details.Repository);
                install.IsEnabled = version.CanInstall && details.Store is not null && !sourceConflict; content.Children.Add(install);
                versions.Children.Add(StoreCard(content));
            }
            if (versions.Children.Count == 0) versions.Children.Add(StoreEmpty("没有符合条件的版本", "开发者需要提供 autumn.release.json 和兼容的 .autumn 附件。源码 ZIP、普通 EXE 和草稿不会作为安装包。"));
        }
        channels.SelectionChanged += (_, _) => RenderVersions(); compatible.Checked += (_, _) => RenderVersions(); compatible.Unchecked += (_, _) => RenderVersions(); RenderVersions();
        if (details.HasMoreVersions) storeBody.Children.Add(ActionButton("读取更早的版本", "StoreMoreVersions", async (_, _) => await LoadMoreStoreVersionsAsync(details)));
    }
    private Border StoreCard(UIElement child) => new() { Child = child, CornerRadius = new CornerRadius(20), Padding = new Thickness(22), BorderThickness = new Thickness(1), BorderBrush = (Brush)Root.Resources["StoreCardStroke"], Background = (Brush)Root.Resources["StoreCardSurface"] };
    private Border StoreEmpty(string title, string body)
    { var content = new StackPanel { Spacing = 12, Padding = new Thickness(10, 20, 10, 20) }; content.Children.Add(new TextBlock { Text = title, FontSize = 22 }); content.Children.Add(Paragraph(body)); return StoreCard(content); }
    private static string StoreError(string code) => code switch
    {
        "STORE_RATE_LIMITED" or "GITHUB_RATE_LIMITED" => "GitHub 请求暂时受限，请按提示稍后重试。",
        "STORE_OFFLINE" or "STORE_NETWORK_ERROR" => "无法连接 GitHub，请检查网络或 GitHub 加速设置。",
        "STORE_MANIFEST_MISSING" => "仓库尚未提供应用展示清单。",
        "RELEASE_INCOMPATIBLE" => "此版本与当前运行环境不兼容。",
        "PACKAGE_APP_RUNNING" => "应用仍在运行，请先保存并结束，再继续安装。",
        _ => code + " · 此项尚不可用，原有应用和数据保留。"
    };
}
