// SPDX-License-Identifier: MIT
// SHA-256 页面不内置插件“正确值”，只读取本机文件并比较用户给出的两个值。
using System;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace PclDownloadSwitch
{
    internal static class HashUtility
    {
        // 标准输入：去掉首尾空白后，必须刚好 64 个 ASCII 十六进制字符。
        // 大小写不影响数值；拒绝中间空格、0x 前缀、文件名和相似 Unicode 字符。
        internal static bool TryNormalize(string value, out string normalized)
        {
            normalized = null;
            string input = (value ?? "").Trim();
            if (input.Length != 64) return false;
            foreach (char c in input)
                if (!((c >= '0' && c <= '9') || (c >= 'a' && c <= 'f') || (c >= 'A' && c <= 'F')))
                    return false;
            normalized = input.ToUpperInvariant();
            return true;
        }

        internal static string FileHash(string path)
        {
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var hash = SHA256.Create())
                return BitConverter.ToString(hash.ComputeHash(stream)).Replace("-", "");
        }
    }

    internal sealed class HashView : ScrollViewer
    {
        private readonly ComboBox file;
        private readonly TextBox localHash;
        private readonly TextBox first;
        private readonly TextBox second;
        private readonly TextBlock result;
        private bool ready;

        internal HashView()
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto;
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
            var stack = new StackPanel { Margin = new Thickness(4, 8, 12, 6) };
            Content = stack;
            var title = Ui.Text("核对你手中的文件", 23, "#EFF5FF");
            title.FontWeight = FontWeights.SemiBold;
            stack.Children.Add(title);
            var intro = Ui.Text("不内置正确值，也不自动联网。以下校验码由本机文件的实际字节计算。", 12, "#A8BEDB");
            intro.TextWrapping = TextWrapping.Wrap;
            intro.Margin = new Thickness(0, 10, 0, 16);
            stack.Children.Add(intro);
            file = new ComboBox
            {
                ItemsSource = new[] { "PCL.DownloadSwitch.dll", "PCL下载开关.exe", "PCL.DownloadSwitch.Native.dll", "Mono.Cecil.dll", "PCL备用下载.exe" },
                SelectedIndex = 0, Padding = new Thickness(8, 5, 8, 5), Margin = new Thickness(0, 0, 0, 8)
            };
            AutomationProperties.SetName(file, "选择本机插件文件");
            Ui.BlackComboText(file);
            stack.Children.Add(file);
            localHash = Input("本机文件 SHA-256", true);
            stack.Children.Add(localHash);
            var actions = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 20) };
            var copy = Ui.Button("复制校验码", "复制本机文件 SHA-256", 116);
            var use = Ui.Button("填入 A", "把本机校验码填入 A", 84);
            use.Margin = new Thickness(8, 0, 0, 0);
            var refresh = Ui.Button("重新计算", "重新计算本机文件 SHA-256", 96);
            refresh.Margin = new Thickness(8, 0, 0, 0);
            actions.Children.Add(copy);
            actions.Children.Add(use);
            actions.Children.Add(refresh);
            stack.Children.Add(actions);
            stack.Children.Add(Ui.Text("SHA-256 比较器", 16, "#DBEAFE"));
            var hint = Ui.Text("各输入 64 位十六进制字符（0–9、A–F）。忽略大小写和首尾空白。", 11, "#8FA8C9");
            hint.Margin = new Thickness(0, 8, 0, 12);
            hint.TextWrapping = TextWrapping.Wrap;
            stack.Children.Add(hint);
            stack.Children.Add(Ui.Text("A", 11, "#A5BAD7"));
            first = Input("SHA-256 A", false);
            first.Margin = new Thickness(0, 5, 0, 10);
            stack.Children.Add(first);
            stack.Children.Add(Ui.Text("B", 11, "#A5BAD7"));
            second = Input("SHA-256 B", false);
            second.Margin = new Thickness(0, 5, 0, 14);
            stack.Children.Add(second);
            result = Ui.Text("等待输入 A 和 B", 15, "#A5BAD7");
            result.TextWrapping = TextWrapping.Wrap;
            AutomationProperties.SetName(result, "校验码比较结果");
            stack.Children.Add(result);
            var note = Ui.Text(
                "✓ 只表示两个有效校验码相同，✕ 表示不同；格式无效时不作比较。\n" +
                "比较结果不认证发布者，也不证明文件无恶意代码。请通过可信渠道取得对照值；被修改的程序可能伪造自己的显示结果。",
                11, "#8FA8C9");
            note.LineHeight = 21;
            note.TextWrapping = TextWrapping.Wrap;
            note.Margin = new Thickness(0, 18, 0, 4);
            stack.Children.Add(note);
            first.TextChanged += delegate { if (ready) Compare(); };
            second.TextChanged += delegate { if (ready) Compare(); };
            file.SelectionChanged += delegate { ReadLocal(); copy.Content = "复制校验码"; };
            refresh.Click += delegate { ReadLocal(); copy.Content = "复制校验码"; };
            copy.Click += delegate
            {
                string normalized;
                if (!HashUtility.TryNormalize(localHash.Text, out normalized)) return;
                try { Clipboard.SetText(normalized); copy.Content = "已复制 ✓"; }
                catch { copy.Content = "复制失败"; }
            };
            use.Click += delegate
            {
                string normalized;
                if (HashUtility.TryNormalize(localHash.Text, out normalized)) first.Text = normalized;
            };
            ready = true;
            ReadLocal();
        }

        private void ReadLocal()
        {
            try
            {
                string path = Path.Combine(Runtime.PluginFolder, (string)file.SelectedItem);
                localHash.Text = HashUtility.FileHash(path);
                localHash.Foreground = Ui.Brush("#BCD7FC");
            }
            catch (Exception error)
            {
                localHash.Text = "无法读取文件：" + error.Message;
                localHash.Foreground = Ui.Brush("#FFB0AE");
            }
        }

        // 不把“输入无效”误判成“不一致”；空输入也不会产生绿色通过提示。
        private void Compare()
        {
            string a, b;
            bool validA = HashUtility.TryNormalize(first.Text, out a);
            bool validB = HashUtility.TryNormalize(second.Text, out b);
            if (string.IsNullOrWhiteSpace(first.Text) && string.IsNullOrWhiteSpace(second.Text))
            {
                Show("等待输入 A 和 B", "#A5BAD7");
                return;
            }
            if (!validA || !validB)
            {
                string invalid = !validA && !validB ? "A、B" : (!validA ? "A" : "B");
                Show("输入无效（" + invalid + "）：需要 64 位十六进制字符", "#F5C987");
                return;
            }
            bool equal = string.Equals(a, b, StringComparison.Ordinal);
            Show(equal ? "✓  一致" : "✕  不一致", equal ? "#83DBB4" : "#FF9C9C");
        }

        private void Show(string text, string color)
        {
            result.Text = text;
            result.Foreground = Ui.Brush(color);
        }

        private static TextBox Input(string name, bool readOnly)
        {
            var input = new TextBox
            {
                IsReadOnly = readOnly, AcceptsReturn = false, FontFamily = new FontFamily("Consolas"),
                FontSize = 12, TextWrapping = TextWrapping.Wrap, MinHeight = 50,
                VerticalContentAlignment = VerticalAlignment.Center, Padding = new Thickness(10, 7, 10, 7),
                Background = Ui.Brush("#0B1320"), Foreground = Ui.Brush("#DDEBFA"), BorderBrush = Ui.Brush("#3A516E")
            };
            AutomationProperties.SetName(input, name);
            return input;
        }
    }
}
