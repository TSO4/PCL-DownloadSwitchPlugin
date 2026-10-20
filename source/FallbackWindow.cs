// SPDX-License-Identifier: MIT
// 独立备用下载界面：目标目录明确可见，选择/新建目录，版本与加载器列表、取消和日志。
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Media;
using Forms = System.Windows.Forms;
namespace PclDownloadSwitch.Backup
{
    internal sealed class FallbackWindow : Window
    {
        private readonly InstallerNetwork network = new InstallerNetwork();
        private readonly InstallerCatalog catalog;
        private readonly ComboBox games, kinds, loaders, source, gameFolders;
        private readonly TextBox directory, java, versionName;
        private readonly CheckBox snapshots, javaAuto;
        private readonly Button refresh, browse, create, folderRefresh, javaBrowse, javaRefresh, install, cancel;
        private readonly TextBlock state, detail, javaState;
        private readonly ProgressBar progress;
        private readonly int host;
        private readonly long hostStarted;
        private readonly string existingCache;
        private List<GameVersion> allGames = new List<GameVersion>();
        private CancellationTokenSource listing, running, javaDetection;
        private readonly Dictionary<string, int> javaRequirements = new Dictionary<string, int>();
        private int generation, javaGeneration;
        private bool changing, installing, closeAfterCancel, closed;
        private string suggestedName = "";
        internal FallbackWindow(string folder, int host, long hostStarted)
        {
            this.host = host; this.hostStarted = hostStarted; existingCache = folder; catalog = new InstallerCatalog(network);
            Title = "备用下载 · Real Knife — The Origin of 404";
            Width = 900; Height = 790; MinWidth = 740; MinHeight = 680;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Brush("#0F192A"); Foreground = Brush("#EDF4FF");
            FontFamily = new FontFamily("Microsoft YaHei UI");
            var root = new Grid { Margin = new Thickness(26, 20, 26, 22), Background = Background };
            root.SetValue(TextElement.FontFamilyProperty, FontFamily);
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            root.RowDefinitions.Add(new RowDefinition());
            root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Content = root;
            var header = new StackPanel();
            var title = Text("备用下载", 28, "#EFF5FF"); title.FontWeight = FontWeights.SemiBold;
            header.Children.Add(title);
            header.Children.Add(Text("原版与加载器独立安装  /  不依赖 PCL 下载模块", 13, "#88B9F7"));
            header.Margin = new Thickness(0, 0, 0, 22); root.Children.Add(header);
            var body = new StackPanel();
            var scroll = new ScrollViewer { Content = body, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
            Grid.SetRow(scroll, 1); root.Children.Add(scroll);
            AddLabel(body, "PCL 已添加的游戏目录");
            gameFolders = Select("PCL 已添加的游戏目录"); body.Children.Add(gameFolders);
            AddLabel(body, "安装到游戏目录");
            directory = Input(folder, "游戏目录"); directory.IsReadOnly = true;
            body.Children.Add(directory);
            var folderActions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 10, 0, 8) };
            browse = Button("选择目录", 112); create = Button("新建目录", 112); create.Margin = new Thickness(10, 0, 0, 0);
            folderRefresh = Button("刷新 PCL 目录", 142); folderRefresh.Margin = new Thickness(10, 0, 0, 0);
            folderActions.Children.Add(browse); folderActions.Children.Add(create); folderActions.Children.Add(folderRefresh); body.Children.Add(folderActions);
            var hint = Text("默认使用当前 PCL 游戏目录。此处应是 .minecraft 一类的游戏根目录，里面包含 versions、libraries、assets。", 12, "#A3B6D0");
            hint.TextWrapping = TextWrapping.Wrap; body.Children.Add(hint);
            AddLabel(body, "下载源");
            source = Select("下载源");
            source.ItemsSource = new[] { "自动（列表优先官方，文件优先镜像）", "官方", "镜像（BMCLAPI）优先（失败回退官方）" };
            source.SelectedIndex = 0; body.Children.Add(source);
            var selection = new Grid { Margin = new Thickness(0, 20, 0, 0) };
            selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1.25, GridUnitType.Star) });
            selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(14) });
            selection.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            var left = new StackPanel(); selection.Children.Add(left);
            var right = new StackPanel(); Grid.SetColumn(right, 2); selection.Children.Add(right); body.Children.Add(selection);
            AddLabel(left, "Minecraft 版本");
            games = Select("Minecraft 版本"); games.IsEditable = true; games.IsReadOnly = false; games.IsTextSearchEnabled = true; left.Children.Add(games);
            snapshots = new CheckBox { Content = Text("包含快照和历史版本", 12, "#B9CBE0"), Margin = new Thickness(0, 10, 0, 10) };
            left.Children.Add(snapshots);
            AddLabel(right, "安装类型");
            kinds = Select("安装类型"); kinds.ItemsSource = new[] { "原版", "Forge", "Fabric", "NeoForge", "Quilt" }; kinds.SelectedIndex = 0; right.Children.Add(kinds);
            AddLabel(body, "加载器版本");
            loaders = Select("加载器版本"); loaders.IsEnabled = false; body.Children.Add(loaders);
            refresh = Button("刷新版本列表", 142); refresh.Margin = new Thickness(0, 12, 0, 12); refresh.HorizontalAlignment = HorizontalAlignment.Left; body.Children.Add(refresh);
            AddLabel(body, "版本名称（自动填写版本和加载器，可自定义）");
            versionName = Input("", "新安装的版本名称");
            versionName.ToolTip = "安装后在 PCL 版本列表显示此名称；最多 80 字，不可重名或包含路径符号。";
            body.Children.Add(versionName);
            AddLabel(body, "Java（自动检测并匹配所选 Minecraft）");
            javaAuto = new CheckBox { Content = Text("自动选择 Java", 12, "#B9CBE0"),
                IsChecked = true, Margin = new Thickness(0, 0, 0, 8) };
            body.Children.Add(javaAuto);
            var javaRow = new DockPanel();
            javaBrowse = Button("选择 Java", 112); javaBrowse.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(javaBrowse, Dock.Right); javaRow.Children.Add(javaBrowse);
            javaRefresh = Button("重新检测", 112); javaRefresh.Margin = new Thickness(10, 0, 0, 0);
            DockPanel.SetDock(javaRefresh, Dock.Right); javaRow.Children.Add(javaRefresh);
            java = Input("", "Java 可执行文件路径"); java.IsReadOnly = true; javaRow.Children.Add(java); body.Children.Add(javaRow);
            javaState = Text("选择 MC 版本后自动检测已安装的 Java。", 12, "#A3B6D0");
            javaState.TextWrapping = TextWrapping.Wrap; javaState.Margin = new Thickness(0, 8, 0, 0); body.Children.Add(javaState);
            var note = Text("文件会校验完整性。Quilt 暂用官方源；Forge / NeoForge 官方安装器内部请求由安装器处理。\n" +
                "选择其他目录后，需要在 PCL 的版本选择中添加该目录。账号登录与游戏启动仍交给 PCL。", 12, "#A3B6D0");
            note.TextWrapping = TextWrapping.Wrap; note.LineHeight = 22; note.Margin = new Thickness(0, 12, 0, 8); body.Children.Add(note);
            var bottom = new StackPanel { Margin = new Thickness(0, 14, 0, 0) }; Grid.SetRow(bottom, 2); root.Children.Add(bottom);
            state = Text("准备读取官方版本列表…", 14, "#B9D7FF"); state.TextWrapping = TextWrapping.Wrap; bottom.Children.Add(state);
            progress = new ProgressBar { Height = 5, Margin = new Thickness(0, 12, 0, 12), Foreground = Brush("#65AEFF"), Background = Brush("#243955") };
            bottom.Children.Add(progress);
            detail = Text("", 11, "#A6B9D2"); detail.TextWrapping = TextWrapping.Wrap; detail.MaxHeight = 72; bottom.Children.Add(detail);
            var actions = new DockPanel { Margin = new Thickness(0, 14, 0, 0), LastChildFill = false };
            install = Button("开始安装", 135); install.Background = Brush("#2C6CB0"); DockPanel.SetDock(install, Dock.Right); actions.Children.Add(install);
            cancel = Button("取消安装", 112); cancel.IsEnabled = false; cancel.Margin = new Thickness(0, 0, 12, 0); DockPanel.SetDock(cancel, Dock.Right); actions.Children.Add(cancel);
            var log = Button("查看日志", 112); DockPanel.SetDock(log, Dock.Left); actions.Children.Add(log);
            bottom.Children.Add(actions);
            browse.Click += delegate { ChooseFolder(false); };
            create.Click += delegate { ChooseFolder(true); };
            folderRefresh.Click += delegate { RefreshFolders(); };
            gameFolders.SelectionChanged += delegate {
                var selected = gameFolders.SelectedItem as BackupGameFolder;
                if (!installing && selected != null) { directory.Text = selected.Path; UpdateActions(); }
            };
            javaBrowse.Click += delegate {
                using (var picker = new Forms.OpenFileDialog { Title = "选择已安装的 java.exe", Filter = "Java 程序|java.exe;javaw.exe|可执行文件|*.exe" })
                    if (picker.ShowDialog() == Forms.DialogResult.OK) {
                        string selected = picker.FileName;
                        if (Path.GetFileName(selected).Equals("javaw.exe", StringComparison.OrdinalIgnoreCase) && File.Exists(Path.Combine(Path.GetDirectoryName(selected), "java.exe")))
                            selected = Path.Combine(Path.GetDirectoryName(selected), "java.exe");
                        javaAuto.IsChecked = false; java.IsReadOnly = false;
                        java.Text = selected;
                        CheckJavaObserved();
                    }
            };
            javaAuto.Click += delegate { java.IsReadOnly = javaAuto.IsChecked == true; CheckJavaObserved(); };
            javaRefresh.Click += delegate { CheckJavaObserved(); };
            java.LostKeyboardFocus += delegate { if (javaAuto.IsChecked != true) CheckJavaObserved(); };
            refresh.Click += async delegate { await LoadGames(); };
            source.SelectionChanged += async delegate {
                network.Sources.Mode = (DownloadSource)Math.Max(0, source.SelectedIndex);
                if (!changing) await LoadGames();
            };
            snapshots.Click += delegate { FilterGames(); };
            games.SelectionChanged += async delegate { if (!changing) await LoadLoaders(); };
            kinds.SelectionChanged += async delegate { if (!changing) await LoadLoaders(); };
            loaders.SelectionChanged += delegate { UpdateActions(); };
            install.Click += async delegate { await Install(); };
            cancel.Click += delegate { if (running != null) { running.Cancel(); state.Text = "正在取消，请等当前操作结束…"; } };
            log.Click += delegate {
                try {
                    Directory.CreateDirectory(DiagnosticsLog.Folder);
                    Process.Start(new ProcessStartInfo("explorer.exe", "\"" + DiagnosticsLog.Folder + "\"") { UseShellExecute = false });
                } catch (Exception e) { ShowError(e); }
            };
            Loaded += async delegate { await LoadGames(); };
            Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e) {
                if (!installing) return;
                e.Cancel = true;
                if (MessageBox.Show(this, "正在安装。是否取消安装并在清理完成后关闭窗口？", "备用下载",
                    MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) == MessageBoxResult.Yes) {
                    closeAfterCancel = true; running.Cancel(); state.Text = "正在取消并清理…";
                }
            };
            Closed += delegate { closed = true; if (listing != null) listing.Cancel(); if (javaDetection != null) javaDetection.Cancel(); network.Dispose(); };
            RefreshFolders();
            UpdateActions();
        }
        private void RefreshFolders()
        {
            if (installing) return;
            string selectedPath = directory.Text;
            try
            {
                var folders = FallbackState.GameFolders(host, hostStarted);
                if (selectedPath.Length != 0 && !folders.Any(f => String.Equals(f.Path, selectedPath, StringComparison.OrdinalIgnoreCase)))
                    folders.Insert(0, new BackupGameFolder { Name = "当前所选目录", Path = selectedPath });
                gameFolders.ItemsSource = folders;
                gameFolders.SelectedItem = folders.FirstOrDefault(f => String.Equals(f.Path, selectedPath, StringComparison.OrdinalIgnoreCase));
                gameFolders.IsEnabled = folders.Count != 0;
                gameFolders.ToolTip = folders.Count == 0 ? "先接入 PCL 后刷新，或使用下方“选择目录”。" : "读取 PCL 现有目录列表，不修改 PCL 的目录设置。";
            }
            catch (Exception error) { ShowError(error); }
        }
        internal static string SuggestedName(GameVersion game, string kind, LoaderVersion loader)
        {
            if (game == null) return "";
            if (kind == "原版" || loader == null) return game.Id;
            string version = loader.Version;
            if (kind == "Forge" && version.StartsWith(game.Id + "-", StringComparison.Ordinal))
                version = version.Substring(game.Id.Length + 1);
            return game.Id + "-" + kind + "_" + version;
        }
        private void UpdateSuggestedName()
        {
            string next = SuggestedName(games.SelectedItem as GameVersion, Convert.ToString(kinds.SelectedItem), loaders.SelectedItem as LoaderVersion);
            if (versionName.Text.Length == 0 || versionName.Text == suggestedName) versionName.Text = next;
            suggestedName = next;
        }
        private void ChooseFolder(bool makeNew)
        {
            using (var picker = new Forms.FolderBrowserDialog { Description = makeNew ?
                "选择位置并点击“新建文件夹”，创建专门的游戏目录后选中它。" : "选择游戏根目录（包含或将包含 versions、libraries、assets）",
                ShowNewFolderButton = true, SelectedPath = Directory.Exists(directory.Text) ? directory.Text : "" })
            {
                if (picker.ShowDialog() != Forms.DialogResult.OK) return;
                directory.Text = Path.GetFullPath(picker.SelectedPath);
                RefreshFolders();
                state.Text = "目标目录已更新"; detail.Text = directory.Text;
                UpdateActions();
            }
        }
        private async Task LoadGames()
        {
            if (installing || closed) return;
            if (listing != null) listing.Cancel();
            listing = new CancellationTokenSource(); int request = ++generation;
            var token = listing.Token;
            refresh.IsEnabled = false; state.Text = "读取 Mojang 官方版本列表…"; progress.IsIndeterminate = true;
            try
            {
                var versions = await catalog.Games(token);
                if (request != generation || closed) return;
                allGames = versions; state.Text = "版本列表已更新";
                FilterGames();
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (!closed) ShowError(error); }
            finally { if (!closed) { refresh.IsEnabled = !installing; progress.IsIndeterminate = false; UpdateActions(); } }
        }
        private void FilterGames()
        {
            string selected = (games.SelectedItem as GameVersion) == null ? "" : ((GameVersion)games.SelectedItem).Id;
            changing = true;
            var visible = allGames.Where(v => snapshots.IsChecked == true || v.Type == "release").ToList();
            games.ItemsSource = visible;
            games.SelectedItem = visible.FirstOrDefault(v => v.Id == selected) ?? visible.FirstOrDefault();
            changing = false;
            LoadLoadersObserved();
        }
        private async void LoadLoadersObserved() { await LoadLoaders(); }
        private async Task LoadLoaders()
        {
            if (installing || closed) return;
            if (listing != null) listing.Cancel();
            listing = new CancellationTokenSource(); int request = ++generation;
            loaders.ItemsSource = null;
            var game = games.SelectedItem as GameVersion; string kind = Convert.ToString(kinds.SelectedItem);
            CheckJavaObserved();
            if (game == null || kind == "原版") { state.Text = "选择目录和版本后开始安装"; UpdateActions(); return; }
            state.Text = "读取 " + kind + " 官方版本列表…"; progress.IsIndeterminate = true; UpdateActions();
            try
            {
                var versions = await catalog.Loaders(kind, game.Id, listing.Token);
                if (request != generation || closed) return;
                loaders.ItemsSource = versions; loaders.SelectedIndex = versions.Count == 0 ? -1 : 0;
                state.Text = versions.Count == 0 ? "此 MC 版本没有可用的 " + kind + " 安装版本" : kind + " 列表已更新";
            }
            catch (OperationCanceledException) { }
            catch (Exception error) { if (request == generation && !closed) ShowError(error); }
            finally { if (request == generation && !closed) { progress.IsIndeterminate = false; UpdateActions(); } }
        }
        private async void CheckJavaObserved() { await CheckJava(); }
        private async Task CheckJava()
        {
            if (closed || installing) return;
            if (javaDetection != null) javaDetection.Cancel();
            var operation = new CancellationTokenSource(); javaDetection = operation;
            int request = ++javaGeneration;
            bool automatic = javaAuto.IsChecked == true;
            string selected = automatic ? "" : java.Text;
            if (automatic) java.Text = "";
            var game = games.SelectedItem as GameVersion;
            if (game == null) { javaState.Text = "选择 MC 版本后自动检测已安装的 Java。"; operation.Dispose(); javaDetection = null; return; }
            javaState.Text = automatic ? "正在检测已安装的 Java…" : "正在检查指定的 Java…";
            javaState.Foreground = Brush("#A3B6D0");
            try
            {
                int minimum;
                if (!javaRequirements.TryGetValue(game.Id, out minimum))
                {
                    // 此处用于 UI 预选；真正安装仍通过哈希校验后的版本文件重新验证要求。
                    var metadata = Json.Parse(await network.Text(game.Url, operation.Token));
                    if (Json.Required(metadata, "id") != game.Id) throw new InvalidDataException("Java 要求与所选 MC 版本不一致。");
                    minimum = (int)Json.Number(Json.Get(metadata, "javaVersion"), "majorVersion", 8);
                    javaRequirements[game.Id] = minimum;
                }
                if (!automatic && String.IsNullOrWhiteSpace(selected))
                    throw new InvalidOperationException("请选择 java.exe，或勾选“自动选择 Java”。");
                var runtime = await Task.Run(() => InstallerJava.ResolveRuntime(selected, minimum, operation.Token));
                if (closed || request != javaGeneration || installing) return;
                if (automatic) java.Text = runtime.Path;
                javaState.Text = (automatic ? "已自动选择 Java " : "已检查 Java ") + runtime.Major +
                    " · " + game.Id + " 要求 Java " + minimum +
                    (runtime.Major == minimum ? "。\n" : "；更高版本仍可能与个别加载器不兼容。\n") +
                    "此处用于 Forge / NeoForge 安装，游戏启动时仍由 PCL 选择 Java。";
                DiagnosticsLog.Write("BACKUP_JAVA_SELECT", game.Id + " → Java " + runtime.Major + "：" + runtime.Path);
            }
            catch (OperationCanceledException) { }
            catch (Exception error)
            {
                if (closed || request != javaGeneration || installing) return;
                javaState.Text = error.GetBaseException().Message + "\n原版 / Fabric / Quilt 的安装不需要运行 Java。";
                javaState.Foreground = Brush("#F6C184");
                DiagnosticsLog.Write("BACKUP_JAVA_SELECT", "界面 Java 检查未通过。", error);
            }
            finally
            {
                if (ReferenceEquals(javaDetection, operation)) javaDetection = null;
                operation.Dispose();
            }
        }
        private void UpdateActions()
        {
            UpdateSuggestedName();
            bool mod = Convert.ToString(kinds.SelectedItem) != "原版";
            loaders.IsEnabled = !installing && mod;
            install.IsEnabled = !installing && games.SelectedItem is GameVersion &&
                (!mod || loaders.SelectedItem is LoaderVersion) && !String.IsNullOrWhiteSpace(directory.Text);
        }
        private async Task Install()
        {
            var game = games.SelectedItem as GameVersion;
            var loader = Convert.ToString(kinds.SelectedItem) == "原版" ? null : loaders.SelectedItem as LoaderVersion;
            if (game == null || (Convert.ToString(kinds.SelectedItem) != "原版" && loader == null)) return;
            try { InstallerService.ValidateName(versionName.Text, directory.Text, loader == null ? null : game.Id); }
            catch (Exception error) { ShowError(error); return; }
            bool first;
            using (var gate = new Mutex(true, @"Local\RealKnife.BackupInstaller.Install", out first))
            {
                if (!first) { state.Text = "另一个备用安装任务正在运行，请等它结束。"; return; }
                try
                {
                    if (FallbackState.HostBusy(host, hostStarted)) { state.Text = "PCL 正在下载/安装，或任务状态暂不可读，请等任务结束。"; return; }
                    installing = true; running = new CancellationTokenSource();
                    if (listing != null) listing.Cancel();
                    if (javaDetection != null) javaDetection.Cancel();
                    foreach (Control control in new Control[] { games, kinds, loaders, source, snapshots, directory, browse, create, gameFolders, folderRefresh, java, javaBrowse, javaRefresh, javaAuto, versionName, refresh, install }) control.IsEnabled = false;
                    cancel.IsEnabled = true; detail.Text = "安装到：" + directory.Text;
                    FallbackState.Begin(directory.Text);
                    var report = new Progress<InstallProgress>(p => {
                        if (closed) return;
                        state.Text = p.Message; progress.IsIndeterminate = p.Total <= 0;
                        if (p.Total > 0) { progress.Maximum = p.Total; progress.Value = p.Done; }
                    });
                    string version = await new InstallerService(network, existingCache).Install(game, loader, directory.Text,
                        javaAuto.IsChecked == true ? "" : java.Text, report, running.Token, versionName.Text);
                    FallbackState.Complete(host, hostStarted, Path.GetFullPath(directory.Text), version);
                    state.Text = "安装完成：" + version;
                    detail.Text = "游戏目录：" + directory.Text + "\n回到 PCL 版本选择查看；其他目录需先添加，未刷新时可手动刷新。";
                }
                catch (OperationCanceledException) { state.Text = "安装已取消"; detail.Text = "原有版本未被覆盖；已完成的共享资源可能保留，方便后续安装。"; }
                catch (Exception error) { ShowError(error); }
                finally
                {
                    try { FallbackState.End(); } catch (Exception error) { DiagnosticsLog.Write("BACKUP_STATE", "结束任务标记失败。", error); }
                    installing = false; if (running != null) { running.Dispose(); running = null; }
                    foreach (Control control in new Control[] { games, kinds, source, snapshots, directory, browse, create, gameFolders, folderRefresh, java, javaBrowse, javaRefresh, javaAuto, versionName, refresh }) control.IsEnabled = true;
                    cancel.IsEnabled = false; progress.IsIndeterminate = false; UpdateActions();
                    gate.ReleaseMutex();
                    if (closeAfterCancel) Close();
                }
            }
        }
        private void ShowError(Exception error)
        {
            DiagnosticsLog.Write("BACKUP_UI_ERROR", "备用模块操作失败。", error);
            state.Text = "操作失败 · 请查看日志";
            detail.Text = error.GetBaseException().Message;
            detail.ToolTip = error.ToString() + "\n" + DiagnosticsLog.LogPath;
        }
        private static SolidColorBrush Brush(string hex) { return new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); }
        private static TextBlock Text(string value, double size, string color) { return new TextBlock { Text = value, FontSize = size, Foreground = Brush(color) }; }
        private static void AddLabel(Panel panel, string label) { var text = Text(label, 13, "#D6E5F7"); text.Margin = new Thickness(0, 10, 0, 8); panel.Children.Add(text); }
        private static TextBox Input(string value, string name)
        {
            var box = new TextBox { Text = value, Padding = new Thickness(10, 8, 10, 8), FontSize = 13,
                Background = Brush("#17263B"), Foreground = Brush("#EFF5FF"), BorderBrush = Brush("#405773") };
            AutomationProperties.SetName(box, name); return box;
        }
        private static ComboBox Select(string name)
        {
            var box = new ComboBox { Padding = new Thickness(8, 7, 8, 7), FontSize = 13, MinHeight = 34,
                Foreground = Brushes.Black, Background = Brush("#E3EEFA") };
            // 显式设置浅色下拉框文字，避免继承宿主的深绿色 TextBlock 样式。
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new Binding());
            text.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
            box.ItemTemplate = new DataTemplate { VisualTree = text };
            AutomationProperties.SetName(box, name); return box;
        }
        private static Button Button(string label, double width)
        {
            var button = new Button { Content = Text(label, 13, "#F4F8FF"), Width = width, Padding = new Thickness(10, 8, 10, 8),
                Background = Brush("#284768"), BorderBrush = Brush("#46688C"), Cursor = System.Windows.Input.Cursors.Hand };
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(6));
            border.SetBinding(Border.BackgroundProperty, new Binding("Background") { RelativeSource = RelativeSource.TemplatedParent });
            border.SetBinding(Border.PaddingProperty, new Binding("Padding") { RelativeSource = RelativeSource.TemplatedParent });
            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            presenter.SetBinding(ContentPresenter.ContentProperty, new Binding("Content") { RelativeSource = RelativeSource.TemplatedParent });
            border.AppendChild(presenter);
            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = border };
            var style = new Style(typeof(Button));
            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.45)); style.Triggers.Add(disabled);
            var hover = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            hover.Setters.Add(new Setter(UIElement.OpacityProperty, 0.85)); style.Triggers.Add(hover);
            button.Style = style;
            AutomationProperties.SetName(button, label); return button;
        }
    }
}
