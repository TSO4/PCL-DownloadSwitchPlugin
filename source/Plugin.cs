// SPDX-License-Identifier: MIT
// 插件运行时与悬浮面板。只选择 PCL 已有的下载列表加载器，不改写原程序文件。
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Expression = System.Linq.Expressions.Expression;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

[assembly: AssemblyTitle("PCL 下载开关 · Real Knife — The Origin of 404")]
[assembly: AssemblyDescription("独立的第三方下载页开关；原 PCL 程序文件保持不变。")]
[assembly: AssemblyVersion("1.3.2.0")]

namespace PclDownloadSwitch
{
    // CLR 桥接要求的公开静态入口。只调度至 PCL 自己的 UI 线程，不接管主循环。
    public static class PluginEntry
    {
        public static int Attach(string unused)
        {
            try
            {
                var app = Application.Current;
                if (app == null || app.Dispatcher.HasShutdownStarted)
                {
                    DiagnosticsLog.Write("ATTACH_UI", "PCL 界面未就绪或正在关闭。");
                    return 10;
                }
                app.Dispatcher.Invoke(new Action(Runtime.Install), TimeSpan.FromSeconds(8));
                return 0;
            }
            catch (Exception error) { Runtime.Log("attach", error); return 11; }
        }
    }

    internal static class Runtime
    {
        internal const string Author = "Real Knife — The Origin of 404";
        internal static bool Enabled;
        internal static bool NativeAvailable, CanSwitch, VerifiedBuild;
        internal static string HostVersion = "未知", HostPath = "";
        private static bool automaticPending;
        internal static Overlay Panel;
        internal static Window MainWindow;
        internal static readonly ConditionalWeakTable<FrameworkElement, DownloadPage> Pages =
            new ConditionalWeakTable<FrameworkElement, DownloadPage>();
        internal static readonly List<WeakReference> KnownPages = new List<WeakReference>();
        internal static readonly string PluginFolder = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
        internal static readonly string DataFolder = Path.Combine(PluginFolder, "data");
        private static bool installed;
        private static DispatcherTimer bindingWatch;
        private static DispatcherTimer activityWatch;
        private static DownloadActivity activity;
        internal static bool SwitchLocked { get { return activity != null && activity.Locked; } }
        internal static bool HostTasksBusy { get { return activity == null || activity.HostBusy; } }
        internal static string LockReason { get { return activity == null ? "" : activity.Reason; } }
        private const BindingFlags Flags = BindingFlags.Instance | BindingFlags.Static |
            BindingFlags.Public | BindingFlags.NonPublic;

        internal static void Install()
        {
            if (installed)
            {
                if (Panel != null) Panel.Restore();
                return;
            }
            // 用 WPF 的类级 Loaded 事件观察目标页面，避免定时扫描窗口或修改 IL。
            var main = Application.Current.Windows.OfType<Window>()
                .FirstOrDefault(w => w.GetType().FullName == "PCL.FormMain");
            if (main == null) throw new InvalidOperationException("请等 PCL 主界面加载完成再打开插件。");
            var request = AttachRequest.ReadForCurrentProcess(main.GetType().Assembly);
            HostVersion = request.HostVersion;
            HostPath = request.HostPath;
            NativeAvailable = request.NativeAvailable;
            CanSwitch = request.CanSwitch;
            VerifiedBuild = request.VerifiedBuild;
            try
            {
                activity = new DownloadActivity(main.GetType().Assembly);
                activity.Refresh();
            }
            catch (Exception error)
            {
                if (CanSwitch) throw;
                DiagnosticsLog.Write("GUARD_UNAVAILABLE", "此 PCL 构建的任务状态无法读取，备用模块将要求等待或独立操作。", error);
            }
            if (CanSwitch)
            {
                EventManager.RegisterClassHandler(typeof(FrameworkElement), FrameworkElement.LoadedEvent,
                    new RoutedEventHandler(OnLoaded), true);
            }
            AttachMain(main);
            if (CanSwitch) Visit(main);
            // 只在开关启用时，每秒检查已观察到的可见下载页绑定。
            // 只有目标引用被重置才重新绑定，不周期性重刷列表或发网络请求。
            bindingWatch = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            bindingWatch.Tick += delegate
            {
                if (!Enabled) return;
                try
                {
                    foreach (var weak in KnownPages.ToArray())
                    {
                        var page = weak.Target as DownloadPage;
                        if (page != null && page.View.IsVisible) page.Apply(true);
                    }
                }
                catch (Exception error) { bindingWatch.Stop(); Fail(error); }
            };
            // 任务观察独立于绑定保持：开关关闭、面板隐藏时仍需保护用户的下载任务。
            activityWatch = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
            activityWatch.Tick += delegate { RefreshActivity(); };
            if (activity != null) activityWatch.Start();
            installed = true;
            FallbackBridge.Start();
            Panel.UpdateState();
            Log("Plugin " + ReleaseInfo.Version + " loaded; PCL " + HostVersion +
                "; native=" + NativeAvailable + "; canSwitch=" + CanSwitch + "; automatic=" + request.Automatic);
            if (request.Automatic && !NativeAvailable && CanSwitch && SettingsStore.Load().AutoAttach)
            {
                if (SwitchLocked)
                {
                    automaticPending = true;
                    DiagnosticsLog.Write("AUTO_DEFER", "PCL 有任务进行中，自动启用延后至任务结束。");
                }
                else SetEnabled(true);
            }
        }

        // 接入时主窗口可能早已 Loaded；遍历当前视觉树一次发现已打开的下载页。
        private static void Visit(DependencyObject root)
        {
            var element = root as FrameworkElement;
            if (element != null && element.GetType().FullName == "PCL.PageDownloadInstall") Observe(element);
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++) Visit(VisualTreeHelper.GetChild(root, i));
        }

        private static void AttachMain(Window window)
        {
            if (MainWindow != null) return;
            MainWindow = window;
            Panel = new Overlay(MainWindow);
            Panel.Show();
            Panel.Anchor();
            MainWindow.Closed += delegate
            {
                if (bindingWatch != null) bindingWatch.Stop();
                if (activityWatch != null) activityWatch.Stop();
            };
            Log("Overlay attached.");
        }

        private static void Observe(FrameworkElement element)
        {
            if (!CanSwitch) return;
            DownloadPage page;
            if (!Pages.TryGetValue(element, out page))
            {
                page = new DownloadPage(element);
                Pages.Add(element, page);
                KnownPages.Add(new WeakReference(page));
                Log("Download page observed.");
            }
            if (element.IsVisible) page.Apply(Enabled);
        }

        private static void OnLoaded(object sender, RoutedEventArgs args)
        {
            var element = sender as FrameworkElement;
            if (element == null) return;
            string name = element.GetType().FullName;
            if (name != "PCL.PageDownloadInstall") return;
            element.Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(delegate
            {
                try
                {
                    Observe(element);
                }
                catch (Exception error) { Fail(error); }
            }));
        }

        internal static void SetEnabled(bool enabled)
        {
            automaticPending = false;
            if (!CanSwitch || NativeAvailable)
            {
                if (Panel != null) Panel.UpdateState();
                return;
            }
            // 点击时同步复查，避免只靠计时器造成刚开始任务就能切换的空隙。
            RefreshActivity();
            if (SwitchLocked)
            {
                DiagnosticsLog.Write("SWITCH_BLOCKED", "任务保护阻止切换；保留" + (Enabled ? "开启" : "关闭") + "状态。");
                if (Panel != null) Panel.UpdateState();
                return;
            }
            // 仅处理当前可见的下载页；切回隐藏页时，由页面可见性事件补上状态。
            bool previous = Enabled;
            Enabled = enabled;
            try
            {
                foreach (var weak in KnownPages.ToArray())
                {
                    var page = weak.Target as DownloadPage;
                    if (page != null && page.View.IsVisible) page.Apply(enabled);
                }
                Log(enabled ? "ON: original version-list loader selected." : "OFF: original restricted loader selected.");
                if (bindingWatch != null) { if (enabled) bindingWatch.Start(); else bindingWatch.Stop(); }
                if (Panel != null) { Panel.ClearError(); Panel.UpdateState(); }
            }
            catch (Exception error)
            {
                Enabled = previous;
                foreach (var weak in KnownPages.ToArray())
                {
                    var page = weak.Target as DownloadPage;
                    if (page == null || !page.View.IsVisible) continue;
                    try { page.Apply(previous); } catch { }
                }
                Fail(error);
            }
        }

        internal static void Fail(Exception error)
        {
            DiagnosticsLog.Write("SWITCH_ERROR", "页面切换或绑定保持失败。", error);
            automaticPending = false;
            if (bindingWatch != null) bindingWatch.Stop();
            if (SettingsStore.Load().AutoAttach)
                DiagnosticsLog.Write("AUTO_TEMP_PAUSED", "下载开关发生错误，本次自动操作已停止；重新启动同一路径的 PCL 后重新检查接入。");
            if (Panel != null) Panel.ShowError("切换失败 · SWITCH_ERROR\n点击左下角信息按钮查看日志");
        }
        internal static void RefreshActivity()
        {
            if (activity == null) return;
            bool wasLocked = activity.Locked;
            string previous = activity.Reason;
            activity.Refresh();
            if (Panel != null && (wasLocked != activity.Locked || previous != activity.Reason))
                Panel.UpdateState();
            if (automaticPending)
            {
                if (!SettingsStore.Load().AutoAttach) automaticPending = false;
                else if (!activity.Locked) { automaticPending = false; SetEnabled(true); }
            }
        }
        internal static FieldInfo Field(Type type, string name)
        {
            // PCL 的页面加载器字段在基类中，因此逐层查找，不假定固定偏移地址。
            for (var cursor = type; cursor != null; cursor = cursor.BaseType)
            {
                var field = cursor.GetField(name, Flags | BindingFlags.DeclaredOnly);
                if (field != null) return field;
            }
            throw new MissingFieldException(type.FullName, name);
        }
        internal static MethodInfo Method(Type type, string name, int count)
        {
            for (var cursor = type; cursor != null; cursor = cursor.BaseType)
            {
                var method = cursor.GetMethods(Flags | BindingFlags.DeclaredOnly)
                    .FirstOrDefault(m => m.Name == name && m.GetParameters().Length == count);
                if (method != null) return method;
            }
            throw new MissingMethodException(type.FullName, name);
        }
        internal static object Property(object instance, string name)
        {
            var property = instance.GetType().GetProperty(name, Flags);
            return property != null ? property.GetValue(instance, null) : Field(instance.GetType(), name).GetValue(instance);
        }
        internal static void Log(string message, Exception error = null)
        {
            DiagnosticsLog.Write(error == null ? "INFO" : "PLUGIN_ERROR", message, error);
        }
    }

    internal sealed class DownloadPage
    {
        internal readonly FrameworkElement View;
        private readonly object restricted;
        private readonly object normal;
        private readonly object loadingUi;
        private readonly FieldInfo loaderField;
        private readonly MethodInfo finish;
        private readonly MethodInfo stateChanged;
        private readonly MethodInfo forceExit;
        private readonly MethodInfo enter;
        private readonly MethodInfo restart;
        private bool subscribed;
        private bool currentEnabled;
        private bool renderPending;
        private DispatcherTimer deferred;
        private readonly List<Delegate> eventHandlers = new List<Delegate>();

        internal DownloadPage(FrameworkElement view)
        {
            View = view;
            loaderField = Runtime.Field(view.GetType(), "PageLoader");
            restricted = loaderField.GetValue(view);
            normal = Runtime.Field(view.GetType().Assembly.GetType("PCL.ModDownload", true), "DlClientListLoader").GetValue(null);
            loadingUi = Runtime.Property(view, "LoadMinecraft");
            finish = Runtime.Method(view.GetType(), "LoadMinecraft_OnFinish", 0);
            stateChanged = Runtime.Method(view.GetType(), "PageLoaderState", 3);
            forceExit = Runtime.Method(view.GetType(), "PageOnForceExit", 0);
            enter = Runtime.Method(view.GetType(), "PageOnEnter", 0);
            restart = Runtime.Method(view.GetType(), "PageLoaderRestart", 2);
            if (restricted == null || normal == null || loadingUi == null)
                throw new InvalidOperationException("PCL 下载页结构与适配版本不符。");
            // 用实际页面再次确认。原本使用正常加载器的页面不添加处理器或重绑。
            bool native = ReferenceEquals(restricted, normal);
            if (native != Runtime.NativeAvailable)
                DiagnosticsLog.Write("CAPABILITY_RUNTIME", "页面实际绑定与静态判断不同，以当前页面为准；native=" + native);
            Runtime.NativeAvailable = native;
            if (native)
            {
                Runtime.Enabled = false;
                if (Runtime.Panel != null) Runtime.Panel.UpdateState();
                return;
            }
            view.IsVisibleChanged += delegate
            {
                if (!view.IsVisible) return;
                try { Apply(Runtime.Enabled); }
                catch (Exception error) { Runtime.Fail(error); }
            };
        }

        internal void Apply(bool enabled)
        {
            if (ReferenceEquals(restricted, normal)) return;
            string pageState = Runtime.Property(View, "PageState").ToString();
            // PCL 正在退出页面/切换子页时，等原动画结束再改绑定。
            // 不抢跑它已经安排好的 PageOnEnter，否则连续切换会出现重复进入错误。
            if (pageState == "PageExit" || pageState == "ContentExit")
            {
                Defer();
                return;
            }
            object selected = enabled ? normal : restricted;
            if (currentEnabled == enabled && ReferenceEquals(loaderField.GetValue(View), selected) &&
                ReferenceEquals(Runtime.Property(loadingUi, "State"), selected)) return;
            if (enabled && !subscribed)
            {
                AddHandler(normal, "PreviewFinish", delegate(object[] values)
                {
                    if (!Runtime.Enabled || !currentEnabled) return;
                    View.Dispatcher.Invoke(new Action(delegate
                    {
                        if (!Runtime.Enabled || !currentEnabled) return;
                        string state = Runtime.Property(View, "PageState").ToString();
                        if (state == "PageExit" || state == "ContentExit")
                        {
                            renderPending = true;
                            Defer();
                        }
                        else finish.Invoke(View, null);
                    }));
                });
                AddHandler(normal, "OnStateChangedUi", delegate(object[] values)
                {
                    View.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        if (!Runtime.Enabled || !currentEnabled) return;
                        // 忽略排队期间已经过时的加载器状态通知。
                        if (values.Length >= 2 && Runtime.Property(normal, "State").ToString() != values[1].ToString()) return;
                        try { stateChanged.Invoke(View, values); }
                        catch (Exception error) { Runtime.Fail(error); }
                    }));
                });
                subscribed = true;
            }
            // 复用 PCL 原来的动画重置、加载器状态与列表渲染。
            // 不重写 IL、不改跳转指令，不替换下载 URL、账号或游戏启动参数。
            forceExit.Invoke(View, null);
            currentEnabled = enabled;
            if (!enabled) renderPending = false;
            loaderField.SetValue(View, selected);
            loadingUi.GetType().GetProperty("State").SetValue(loadingUi, selected, null);
            if (enabled)
            {
                string state = Runtime.Property(normal, "State").ToString();
                if (state == "Finished") finish.Invoke(View, null);
                else if (state != "Loading") restart.Invoke(View, new object[] { null, true });
            }
            // Empty 表示 PCL 还没有调用页面进入，此时只绑定，由 PCL 自己开始动画。
            if (pageState != "Empty") enter.Invoke(View, null);
            Runtime.Log("Page binding: " + Runtime.Property(selected, "Name") + "; state=" + Runtime.Property(selected, "State"));
        }

        private void Defer()
        {
            if (deferred == null)
            {
                deferred = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
                deferred.Tick += delegate
                {
                    if (!View.IsVisible) { deferred.Stop(); return; }
                    string state = Runtime.Property(View, "PageState").ToString();
                    if (state == "PageExit" || state == "ContentExit") return;
                    deferred.Stop();
                    try
                    {
                        if (renderPending && Runtime.Enabled && currentEnabled)
                        {
                            renderPending = false;
                            finish.Invoke(View, null);
                        }
                        Apply(Runtime.Enabled);
                    }
                    catch (Exception error) { Runtime.Fail(error); }
                };
            }
            deferred.Start();
        }

        private void AddHandler(object source, string name, Action<object[]> callback)
        {
            // 原事件使用 PCL 自己的类型；根据实际签名生成委托，避免捆绑 PCL 代码。
            var evt = source.GetType().GetEvent(name);
            if (evt == null) throw new MissingMemberException(source.GetType().FullName, name);
            var invoke = evt.EventHandlerType.GetMethod("Invoke");
            var parameters = invoke.GetParameters().Select(p => Expression.Parameter(p.ParameterType, p.Name)).ToArray();
            var values = Expression.NewArrayInit(typeof(object), parameters.Select(p => Expression.Convert(p, typeof(object))));
            var call = Expression.Call(Expression.Constant(callback), typeof(Action<object[]>).GetMethod("Invoke"), values);
            var handler = Expression.Lambda(evt.EventHandlerType, call, parameters).Compile();
            evt.AddEventHandler(source, handler);
            eventHandlers.Add(handler);
        }
    }

    internal sealed class Overlay : Window
    {
        private readonly Window owner;
        private readonly ToggleButton toggle;
        private readonly TextBlock status;
        private readonly Border shell;
        private readonly StackPanel tutorial;
        private readonly StackPanel expanded;
        private readonly Button compact;
        private readonly TextBlock compactLabel;
        private readonly KeyEventHandler restoreKey;
        private bool updating;
        private DetailsWindow detailsWindow;
        private string errorMessage;

        internal Overlay(Window parent)
        {
            owner = parent;
            Owner = parent;
            Title = "PCL 下载开关 · " + Runtime.Author;
            Width = 286;
            SizeToContent = SizeToContent.Height;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ShowActivated = false;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Topmost = false;

            shell = new Border
            {
                Background = Brush("#F21A2538"), BorderBrush = Brush("#546A89"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(13),
                Padding = new Thickness(15, 12, 15, 12), Margin = new Thickness(4)
            };
            shell.Effect = new System.Windows.Media.Effects.DropShadowEffect
            { Color = Colors.Black, Opacity = 0.22, BlurRadius = 10, ShadowDepth = 2 };
            Content = shell;
            var container = new Grid();
            shell.Child = container;
            expanded = new StackPanel();
            var stack = expanded;
            container.Children.Add(expanded);
            compactLabel = Text("MC 下载 · 已关闭  ↗", 11, "#E4EFFF");
            compact = new Button
            {
                Content = compactLabel, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
                Padding = new Thickness(0), Visibility = Visibility.Collapsed,
                Cursor = Cursors.Hand, ToolTip = "展开下载开关"
            };
            AutomationProperties.SetName(compact, "展开下载开关");
            compact.Click += delegate { SetMinimized(false); };
            container.Children.Add(compact);
            var titlebar = new DockPanel { Margin = new Thickness(0, 0, 0, 8) };
            stack.Children.Add(titlebar);
            var actions = new StackPanel { Orientation = Orientation.Horizontal };
            DockPanel.SetDock(actions, Dock.Right);
            titlebar.Children.Add(actions);
            var info = Ui.IconButton("info", "展开详情：简介、源码、校验与日志", 26);
            var minimize = Ui.IconButton("minus", "原地最小化", 26);
            var hide = Ui.IconButton("close", "关闭面板；Ctrl+Alt+D 重新显示", 26);
            actions.Children.Add(minimize);
            actions.Children.Add(hide);
            var eyebrow = Text("PCL  /  下载开关", 10, "#A6B9D2");
            eyebrow.VerticalAlignment = VerticalAlignment.Center;
            titlebar.Children.Add(eyebrow);
            minimize.Click += delegate { SetMinimized(true); };
            hide.Click += delegate { Hide(); };
            var row = new DockPanel();
            stack.Children.Add(row);
            toggle = new ToggleButton { Width = 44, Height = 24, Cursor = System.Windows.Input.Cursors.Hand };
            AutomationProperties.SetName(toggle, "恢复 MC 下载");
            toggle.Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type ToggleButton}'>" +
                "<Border x:Name='Track' CornerRadius='12' Background='#516078'><Grid>" +
                "<Ellipse x:Name='Knob' Width='18' Height='18' Fill='White' HorizontalAlignment='Left' Margin='3,0,0,0'><Ellipse.RenderTransform><TranslateTransform/></Ellipse.RenderTransform></Ellipse>" +
                "</Grid></Border><ControlTemplate.Triggers><Trigger Property='IsChecked' Value='True'>" +
                "<Setter TargetName='Track' Property='Background' Value='#4CA2FF'/>" +
                "<Trigger.EnterActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='20' Duration='0:0:0.15'/></Storyboard></BeginStoryboard></Trigger.EnterActions>" +
                "<Trigger.ExitActions><BeginStoryboard><Storyboard><DoubleAnimation Storyboard.TargetName='Knob' Storyboard.TargetProperty='(UIElement.RenderTransform).(TranslateTransform.X)' To='0' Duration='0:0:0.15'/></Storyboard></BeginStoryboard></Trigger.ExitActions>" +
                "</Trigger><Trigger Property='IsEnabled' Value='False'><Setter TargetName='Track' Property='Opacity' Value='0.4'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
            DockPanel.SetDock(toggle, Dock.Right);
            row.Children.Add(toggle);
            var heading = Text("恢复 MC 下载", 16, "#F3F7FF");
            heading.FontWeight = FontWeights.SemiBold;
            heading.VerticalAlignment = VerticalAlignment.Center;
            row.Children.Add(heading);
            status = Text("已关闭 · 使用原版行为", 11, "#A6B9D2");
            status.Margin = new Thickness(0, 8, 0, 0);
            status.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(status);
            var author = Text("作者  " + Runtime.Author, 10, "#8499B8");
            author.Margin = new Thickness(0, 6, 0, 0);
            stack.Children.Add(author);
            tutorial = new StackPanel { Margin = new Thickness(0, 13, 0, 0) };
            stack.Children.Add(tutorial);
            tutorial.Children.Add(new Border { Height = 1, Background = Brush("#43546E"), Margin = new Thickness(0, 0, 0, 11) });
            var welcome = Text("初次使用 · 三步开始", 13, "#DCEBFF");
            welcome.FontWeight = FontWeights.SemiBold;
            tutorial.Children.Add(welcome);
            var instructions = Text("① 打开右上方开关\n② 进入「下载 → 原版游戏」\n③ 关闭开关，恢复原版提示", 12, "#B7C9E2");
            instructions.LineHeight = 23;
            instructions.Margin = new Thickness(0, 6, 0, 9);
            tutorial.Children.Add(instructions);
            var details = Text("当前 PCL " + Runtime.HostVersion +
                "\n自动模式默认关闭，可在详情 → 设置开启\n隐藏面板后按 Ctrl+Alt+D 重新显示\n第三方独立插件 · PCL 原作者：龙腾猫跃", 10, "#91A9C9");
            details.LineHeight = 19;
            details.TextWrapping = TextWrapping.Wrap;
            details.Margin = new Thickness(0, 0, 0, 10);
            tutorial.Children.Add(details);
            info.Click += delegate
            {
                if (detailsWindow == null)
                {
                    detailsWindow = new DetailsWindow(owner);
                    detailsWindow.Closed += delegate { detailsWindow = null; };
                }
                detailsWindow.Show();
                detailsWindow.Activate();
            };
            var button = Ui.Button("知道了", "关闭首次使用提示", double.NaN);
            button.Padding = new Thickness(14, 5, 14, 5);
            button.HorizontalAlignment = HorizontalAlignment.Right;
            button.Click += delegate
            {
                tutorial.Visibility = Visibility.Collapsed;
                shell.BeginAnimation(Border.BorderBrushProperty, null);
                shell.BorderBrush = Brush("#546A89");
                try { Directory.CreateDirectory(Runtime.DataFolder); File.WriteAllText(Path.Combine(Runtime.DataFolder, "welcome.seen"), "1"); }
                catch (Exception error) { Runtime.Log("welcome state", error); }
                Dispatcher.BeginInvoke(new Action(Anchor));
            };
            tutorial.Children.Add(button);
            // 信息图标位于整个面板左下角；关闭与最小化保留在标题栏右上角。
            var footer = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 9, 0, 0) };
            footer.Children.Add(info);
            var footerHint = Text("简介 · 源码 · 校验 · 日志 · 设置", 10, "#8499B8");
            footerHint.VerticalAlignment = VerticalAlignment.Center;
            footerHint.Margin = new Thickness(7, 0, 0, 0);
            footer.Children.Add(footerHint);
            stack.Children.Add(footer);
            if (File.Exists(Path.Combine(Runtime.DataFolder, "welcome.seen")))
                tutorial.Visibility = Visibility.Collapsed;
            else
            {
                var borderBrush = new SolidColorBrush(Color.FromRgb(84, 106, 137));
                shell.BorderBrush = borderBrush;
                borderBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                    new ColorAnimation(Color.FromRgb(76, 162, 255), TimeSpan.FromSeconds(0.7))
                    { AutoReverse = true, RepeatBehavior = new RepeatBehavior(5) });
                toggle.ToolTip = "打开后恢复原版游戏版本列表；关闭后回到原版限制提示。";
            }
            toggle.Checked += delegate { if (!updating) Runtime.SetEnabled(true); };
            toggle.Unchecked += delegate { if (!updating) Runtime.SetEnabled(false); };
            owner.LocationChanged += delegate { Anchor(); };
            owner.SizeChanged += delegate { Anchor(); };
            owner.StateChanged += delegate { Dispatcher.BeginInvoke(new Action(Anchor)); };
            restoreKey = delegate(object sender, KeyEventArgs e)
            {
                if (e.Key == Key.D && (Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Alt)) == (ModifierKeys.Control | ModifierKeys.Alt))
                {
                    SetMinimized(false);
                    Show();
                    Anchor();
                    e.Handled = true;
                }
            };
            owner.PreviewKeyDown += restoreKey;
            owner.Closed += delegate { owner.PreviewKeyDown -= restoreKey; Close(); };
            SizeChanged += delegate { Anchor(); };
        }

        private void SetMinimized(bool value)
        {
            // 只收起自己的内容，不最小化 PCL；胶囊仍跟随主窗口。
            Width = value ? 172 : 286;
            expanded.Visibility = value ? Visibility.Collapsed : Visibility.Visible;
            compact.Visibility = value ? Visibility.Visible : Visibility.Collapsed;
            shell.Padding = value ? new Thickness(12, 8, 12, 8) : new Thickness(15, 12, 15, 12);
            Anchor();
        }

        internal void Anchor()
        {
            // WPF 使用设备无关像素；随主窗口移动和缩放更新右上角位置。
            if (owner.WindowState == WindowState.Minimized) return;
            double ownerLeft = owner.Left;
            double ownerTop = owner.Top;
            if (double.IsNaN(ownerLeft) || double.IsNaN(ownerTop)) return;
            Left = ownerLeft + Math.Max(0, owner.ActualWidth - Width - 12);
            Top = ownerTop + 60;
        }
        internal void UpdateState()
        {
            updating = true;
            toggle.IsChecked = Runtime.Enabled;
            updating = false;
            toggle.IsEnabled = !Runtime.SwitchLocked && Runtime.CanSwitch && !Runtime.NativeAvailable;
            toggle.Cursor = toggle.IsEnabled ? Cursors.Hand : Cursors.No;
            toggle.ToolTip = Runtime.SwitchLocked ? Runtime.LockReason + "，任务结束后自动解锁。" : "打开恢复原版游戏列表；关闭恢复原版限制提示。";
            ToolTipService.SetShowOnDisabled(toggle, true);
            status.Text = Runtime.Enabled ? "已启用 · 使用原有下载源" : "已关闭 · 使用原版行为";
            status.Foreground = Brush(Runtime.Enabled ? "#93D5BC" : "#A6B9D2");
            compactLabel.Text = Runtime.Enabled ? "MC 下载 · 已启用  ↗" : "MC 下载 · 已关闭  ↗";
            if (Runtime.NativeAvailable)
            {
                status.Text = "PCL 本身能下载 · 无需启用恢复功能";
                status.Foreground = Brush("#93D5BC");
                compactLabel.Text = "MC 下载 · 原生可用  ↗";
                toggle.ToolTip = "此 PCL 本身能下载，插件不会改动原生下载页。";
            }
            if (!String.IsNullOrEmpty(errorMessage))
            {
                status.Text = errorMessage;
                status.Foreground = Brush("#FFB0AE");
            }
            if (Runtime.SwitchLocked)
            {
                status.Text = (Runtime.Enabled ? "已启用" : "已关闭") + " · 开关已锁定\n" + Runtime.LockReason;
                status.Foreground = Brush("#E8BE77");
                compactLabel.Text = "MC 下载 · 已锁定  ↗";
            }
        }
        internal void Restore()
        {
            SetMinimized(false);
            Show();
            Anchor();
            Activate();
        }
        internal void ShowError(string message)
        {
            errorMessage = message;
            UpdateState();
        }
        internal void ClearError() { errorMessage = null; }
        private static TextBlock Text(string value, double size, string color)
        {
            return new TextBlock { Text = value, FontSize = size, Foreground = Brush(color), FontFamily = new FontFamily("Microsoft YaHei UI") };
        }
        private static SolidColorBrush Brush(string color)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        }
    }
}
