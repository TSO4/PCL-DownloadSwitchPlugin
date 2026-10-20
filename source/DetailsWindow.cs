// SPDX-License-Identifier: MIT
// 详情窗口：简介与作者、随包源码、SHA-256 校验码比较、诊断日志。
// 此文件只创建插件自己的窗口，不替换或修改 PCL 的界面资源。
using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;

namespace PclDownloadSwitch
{
    internal sealed class DetailsWindow : Window
    {
        internal const string Disclaimer =
            "本插件以源代码及编译文件的形式按 MIT 许可证“按原样”提供，不作无故障或适用于特定用途的保证。\n\n" +
            "第三方修改、重新编译、重新打包或冒用作者名义分发的版本，不代表原作者发布或认可。第三方植入恶意代码、侵犯他人权益等行为所产生的责任，由相应责任人依法承担。\n\n" +
            "在适用法律允许的范围内，作者不对使用、修改或分发本插件造成的损失承担责任；本说明不排除法律规定不得免除的责任。";

        private readonly ContentControl body;
        private readonly Button[] navigation = new Button[6];
        private FrameworkElement about;
        private FrameworkElement source;
        private FrameworkElement hashes;
        private FrameworkElement diagnostics;
        private FrameworkElement settings;
        private FrameworkElement fallback;

        internal DetailsWindow(Window parent)
        {
            Owner = parent;
            Title = "PCL 下载开关 · 详情";
            Width = Math.Min(900, SystemParameters.WorkArea.Width - 32);
            Height = Math.Min(650, SystemParameters.WorkArea.Height - 32);
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            FontFamily = new FontFamily("Microsoft YaHei UI");
            ShowInTaskbar = false;

            var shell = new Border
            {
                Background = Ui.Brush("#101A2A"), BorderBrush = Ui.Brush("#435873"),
                BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(16)
            };
            Content = shell;
            var layout = new Grid();
            layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(66) });
            layout.RowDefinitions.Add(new RowDefinition());
            shell.Child = layout;

            var titlebar = new DockPanel { Margin = new Thickness(24, 16, 16, 12) };
            // 只拖动自己窗口的空白标题区域，按钮点击不会触发拖动。
            titlebar.MouseLeftButtonDown += delegate(object sender, MouseButtonEventArgs e)
            {
                if (e.OriginalSource is TextBlock || e.OriginalSource == titlebar) DragMove();
            };
            layout.Children.Add(titlebar);
            var close = Ui.IconButton("close", "关闭详情", 36);
            close.Click += delegate { Close(); };
            DockPanel.SetDock(close, Dock.Right);
            titlebar.Children.Add(close);
            var title = Ui.Text("下载开关  /  详情", 20, "#EFF5FF");
            title.FontWeight = FontWeights.SemiBold;
            title.VerticalAlignment = VerticalAlignment.Center;
            titlebar.Children.Add(title);

            var content = new Grid { Margin = new Thickness(14, 0, 14, 14) };
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(154) });
            content.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
            content.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetRow(content, 1);
            layout.Children.Add(content);
            var sidebar = new DockPanel { Margin = new Thickness(0, 4, 0, 0) };
            content.Children.Add(sidebar);
            var foot = Ui.Text("VERSION " + ReleaseInfo.Version + "\nMIT LICENSE\n源码随包提供", 10, "#8298B8");
            foot.LineHeight = 20;
            foot.Margin = new Thickness(12, 10, 0, 6);
            DockPanel.SetDock(foot, Dock.Bottom);
            sidebar.Children.Add(foot);
            var links = new StackPanel();
            sidebar.Children.Add(links);
            string[] names = { "简介与作者", "源代码", "SHA-256 校验", "诊断与日志", "设置", "备用下载" };
            for (int i = 0; i < names.Length; i++)
            {
                int index = i;
                var button = Ui.Button(names[i], names[i], double.NaN);
                button.HorizontalContentAlignment = HorizontalAlignment.Left;
                button.Margin = new Thickness(0, 0, 0, 8);
                button.Padding = new Thickness(12, 12, 10, 12);
                button.Click += delegate { Select(index); };
                navigation[i] = button;
                links.Children.Add(button);
            }
            body = new ContentControl { HorizontalContentAlignment = HorizontalAlignment.Stretch, VerticalContentAlignment = VerticalAlignment.Stretch };
            Grid.SetColumn(body, 2);
            content.Children.Add(body);
            Select(0);
        }

        // 页面只创建一次：切换栏目后，选中的源码和输入的校验码仍保留。
        private void Select(int index)
        {
            for (int i = 0; i < navigation.Length; i++)
            {
                navigation[i].Background = Ui.Brush(i == index ? "#284B75" : "#17243A");
                navigation[i].Foreground = Ui.Brush(i == index ? "#F5FAFF" : "#AFC1DB");
            }
            if (index == 0) body.Content = about ?? (about = BuildAbout());
            if (index == 1) body.Content = source ?? (source = BuildSource());
            if (index == 2) body.Content = hashes ?? (hashes = new HashView());
            if (index == 3) body.Content = diagnostics ?? (diagnostics = new DiagnosticsView());
            if (index == 4) body.Content = settings ?? (settings = new SettingsView());
            if (index == 5) body.Content = fallback ?? (fallback = FallbackBridge.View());
        }

        private FrameworkElement BuildAbout()
        {
            var stack = new StackPanel { Margin = new Thickness(16, 8, 20, 12) };
            var heading = Ui.Text("把选择权留在开关上。", 25, "#EEF5FF");
            heading.FontWeight = FontWeights.SemiBold;
            stack.Children.Add(heading);
            var byline = Ui.Text("作者  " + Runtime.Author, 13, "#85BAFF");
            byline.Margin = new Thickness(0, 12, 0, 20);
            stack.Children.Add(byline);
            AddSection(stack, "简介",
                "第三方独立插件。已验证基准为 PCL 正式版 2.99.0.0；其他构建按下载页结构检查，不保证所有未来版本都兼容。\n" +
                "接入已运行的 PCL，在当前进程内切换下载页使用的加载器；不改写原 PCL.exe，也不替换已有配置。手动接入默认关闭；设置里的自动接入需要主动确认。\n" +
                "PCL 原本能下载时会先询问是否继续，继续可查看辅助面板；不会给它增加限制。\n" +
                "启用期间保持下载页绑定；关闭开关后停止保持并恢复原版行为。\n" +
                "检测到 PCL 任务等待或执行时锁定开关，整个下载、校验、解压和安装过程都保持当前状态；任务完成、失败或取消后自动解锁。");
            AddSection(stack, "怎么使用",
                "1. 完整解压插件，像平常一样正常打开 PCL。\n" +
                "2. 等主界面出现后，打开「PCL下载开关.exe」，打开右上角开关。\n" +
                "3. 进入「下载 → 原版游戏」；关闭开关后恢复原版行为。\n" +
                "详情 → 设置可开启“启动 PCL 后自动接入并启用下载”，默认关闭；开启前会提示风险。\n" +
                "详情 → 备用下载可独立安装原版与加载器，默认使用当前 PCL 游戏目录，也能选择或新建其他目录。\n" +
                "左下角信息图标打开详情；右上角横线收成小胶囊；最右侧叉号关闭面板并保留本次状态。\n" +
                "隐藏后，在 PCL 主窗口按 Ctrl+Alt+D 重新显示。");
            AddSection(stack, "开源与署名",
                "插件自身源码采用 MIT 许可证，可阅读、修改和再分发；请保留许可证与版权声明。\n" +
                "PCL 原作者：龙腾猫跃。本插件不代表 PCL 官方；PCL 及其他项目的权利与许可保持不变。\n" +
                "本插件不会绕过 Minecraft 账号或购买验证。");
            AddSection(stack, "免责声明", Disclaimer);
            return new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        }

        // 源码以 UTF-8 资源编入 DLL，查看时无需联网，也无需外部源文件。
        // 显示的是构建时输入的文件；它本身并不是二进制可信性的证明。
        private FrameworkElement BuildSource()
        {
            var grid = new Grid { Margin = new Thickness(2, 5, 2, 2) };
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            grid.RowDefinitions.Add(new RowDefinition());
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var heading = Ui.Text("完整源码", 22, "#EFF5FF");
            heading.Margin = new Thickness(0, 0, 0, 14);
            grid.Children.Add(heading);
            var toolbar = new DockPanel { Margin = new Thickness(0, 0, 0, 12) };
            Grid.SetRow(toolbar, 1);
            grid.Children.Add(toolbar);
            var copy = Ui.Button("复制全文", "复制当前源码文件", 100);
            DockPanel.SetDock(copy, Dock.Right);
            toolbar.Children.Add(copy);
            var selector = new ComboBox
            {
                ItemsSource = new[] { "Plugin.cs", "DetailsWindow.cs", "HashView.cs", "DownloadActivity.cs", "DiagnosticsLog.cs", "DiagnosticsView.cs", "SettingsView.cs", "PluginSettings.cs", "Launcher.cs", "Compatibility.cs", "AutoWatcher.cs", "AutoProgress.cs", "FallbackBridge.cs", "FallbackState.cs", "FallbackMain.cs", "FallbackWindow.cs", "InstallModel.cs", "DownloadSources.cs", "InstallerNetwork.cs", "InstallerCatalog.cs", "InstallerJava.cs", "InstallerService.cs", "NativeBridge.cpp", "build.ps1", "LICENSE", "Mono.Cecil.LICENSE.txt" },
                SelectedIndex = 0, Margin = new Thickness(0, 0, 12, 0), Padding = new Thickness(8, 6, 8, 6),
                FontSize = 13, MinWidth = 160, VerticalContentAlignment = VerticalAlignment.Center
            };
            AutomationProperties.SetName(selector, "选择源码文件");
            Ui.BlackComboText(selector);
            toolbar.Children.Add(selector);
            var code = new TextBox
            {
                IsReadOnly = true, AcceptsReturn = true, AcceptsTab = true,
                FontFamily = new FontFamily("Consolas, Microsoft YaHei UI"), FontSize = 12,
                Background = Ui.Brush("#0B1320"), Foreground = Ui.Brush("#D9E8FA"),
                BorderBrush = Ui.Brush("#354B65"), Padding = new Thickness(12),
                TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                HorizontalScrollBarVisibility = ScrollBarVisibility.Auto
            };
            AutomationProperties.SetName(code, "源码正文，可选择复制");
            Grid.SetRow(code, 2);
            grid.Children.Add(code);
            Action load = delegate
            {
                code.Text = ReadSource((string)selector.SelectedItem);
                code.ScrollToHome();
                copy.Content = "复制全文";
            };
            selector.SelectionChanged += delegate { load(); };
            copy.Click += delegate
            {
                try { Clipboard.SetText(code.Text); copy.Content = "已复制 ✓"; }
                catch { copy.Content = "复制失败"; }
            };
            load();
            var note = Ui.Text("读取本版编译时内嵌的源码，只读展示，不执行文本。压缩包 source 提供同一份源码及构建脚本。", 10, "#849BBA");
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 10, 0, 0);
            Grid.SetRow(note, 3);
            grid.Children.Add(note);
            return grid;
        }

        internal static string ReadSource(string name)
        {
            // build.ps1 将本次构建使用的文本嵌入 DLL；查看时不联网、不反编译、不执行源码。
            using (var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PclDownloadSwitch.Source." + name))
            {
                if (stream == null) return "未找到内嵌源文件：" + name;
                using (var reader = new StreamReader(stream, System.Text.Encoding.UTF8, true))
                    return reader.ReadToEnd();
            }
        }

        private static void AddSection(Panel stack, string title, string content)
        {
            var label = Ui.Text(title, 15, "#E1EDFF");
            label.FontWeight = FontWeights.SemiBold;
            label.Margin = new Thickness(0, 2, 0, 8);
            stack.Children.Add(label);
            var text = Ui.Text(content, 12, "#ACBFDA");
            text.LineHeight = 23;
            text.TextWrapping = TextWrapping.Wrap;
            text.Margin = new Thickness(0, 0, 0, 22);
            stack.Children.Add(text);
        }
    }

    // 自有窗口共用的视觉样式；局部模板不会影响 PCL 原有控件。
    internal static class Ui
    {
        internal static SolidColorBrush Brush(string value)
        {
            return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
        }
        internal static TextBlock Text(string text, double size, string color)
        {
            return new TextBlock { Text = text, FontSize = size, Foreground = Brush(color), FontFamily = new FontFamily("Microsoft YaHei UI") };
        }
        // 使用矢量路径绘制图标，不依赖系统字体里 i / − / × 的字形。
        internal static Button IconButton(string kind, string accessibleName, double size)
        {
            var button = Button("", accessibleName, size);
            button.Height = size;
            button.Padding = new Thickness(5);
            button.Margin = new Thickness(2, 0, 0, 0);
            button.Background = Brushes.Transparent;
            button.ToolTip = accessibleName;
            button.ContentTemplate = null;
            var group = new GeometryGroup();
            if (kind == "info")
            {
                group.Children.Add(new EllipseGeometry(new Point(8, 8), 6.25, 6.25));
                group.Children.Add(Geometry.Parse("M 8,7.3 L 8,11.1 M 7.7,4.8 L 8.3,4.8"));
            }
            else group.Children.Add(Geometry.Parse(kind == "close" ?
                "M 3.5,3.5 L 12.5,12.5 M 12.5,3.5 L 3.5,12.5" : "M 3,8 L 13,8"));
            var icon = new System.Windows.Shapes.Path
            {
                Data = group, Stroke = Brush("#BCD0EA"), StrokeThickness = 1.5,
                StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
                Width = 16, Height = 16
            };
            button.Content = new Viewbox { Width = 16, Height = 16, Child = icon };
            return button;
        }
        internal static void BlackComboText(ComboBox box)
        {
            // 选中项和展开列表均显式使用黑色，防止 PCL 的文字样式覆盖。
            box.Foreground = Brushes.Black;
            box.Background = Brushes.WhiteSmoke;
            var text = new FrameworkElementFactory(typeof(TextBlock));
            text.SetBinding(TextBlock.TextProperty, new System.Windows.Data.Binding());
            text.SetValue(TextBlock.ForegroundProperty, Brushes.Black);
            box.ItemTemplate = new DataTemplate { VisualTree = text };
        }
        internal static Button Button(string text, string accessibleName, double width)
        {
            var button = new Button
            {
                Content = text, Width = width, Background = Brush("#263F5F"),
                Foreground = Brush("#E9F3FF"), Padding = new Thickness(12, 8, 12, 8),
                BorderThickness = new Thickness(0), Cursor = Cursors.Hand, FontSize = 12,
                FontFamily = new FontFamily("Microsoft YaHei UI")
            };
            button.Style = null;
            // PCL 的隐式 TextBlock 样式会盖住按钮继承的文字颜色。
            // 对内容模板中的 TextBlock 直接绑定按钮颜色，避免深色面板出现灰暗文字。
            button.ContentTemplate = (DataTemplate)XamlReader.Parse(
                "<DataTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" +
                "<TextBlock Text='{Binding}' Foreground='{Binding Foreground, RelativeSource={RelativeSource AncestorType={x:Type Button}}}' " +
                "FontSize='{Binding FontSize, RelativeSource={RelativeSource AncestorType={x:Type Button}}}' FontFamily='Microsoft YaHei UI'/></DataTemplate>");
            button.Template = (ControlTemplate)XamlReader.Parse(
                "<ControlTemplate xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml' TargetType='{x:Type Button}'>" +
                "<Border x:Name='Face' Background='{TemplateBinding Background}' CornerRadius='7' Padding='{TemplateBinding Padding}'><ContentPresenter HorizontalAlignment='{TemplateBinding HorizontalContentAlignment}' VerticalAlignment='Center'/></Border>" +
                "<ControlTemplate.Triggers><Trigger Property='IsMouseOver' Value='True'><Setter TargetName='Face' Property='Opacity' Value='0.8'/></Trigger><Trigger Property='IsKeyboardFocused' Value='True'><Setter TargetName='Face' Property='BorderThickness' Value='1'/><Setter TargetName='Face' Property='BorderBrush' Value='#85BAFF'/></Trigger></ControlTemplate.Triggers></ControlTemplate>");
            AutomationProperties.SetName(button, accessibleName);
            return button;
        }
    }
}
