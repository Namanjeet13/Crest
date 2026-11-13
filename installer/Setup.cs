// Crest installer. Built with the C# compiler that ships with Windows (.NET Framework 4.8),
// so it runs on any Windows 10/11 PC with nothing else installed. The app itself rides along
// as an embedded zip (payload.zip) and carries its own .NET runtime.
//
// Installs for the current user only (no admin prompt) into %LOCALAPPDATA%\Programs\Crest,
// adds Start menu (and optionally desktop) shortcuts, registers with Settings > Apps so it can
// be uninstalled, and can start Crest with Windows.

using System;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.Win32;

[assembly: AssemblyTitle("Crest Setup")]
[assembly: AssemblyProduct("Crest")]
[assembly: AssemblyDescription("Installs Crest, a dynamic island for Windows.")]
[assembly: AssemblyVersion(CrestSetup.Info.Version + ".0")]
[assembly: AssemblyFileVersion(CrestSetup.Info.Version + ".0")]

namespace CrestSetup
{
    internal static class Info
    {
        public const string Version = "1.0.1";
        public const string Name = "Crest";
        public const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\Crest";
        public const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";

        public static string InstallDir
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Crest"); }
        }
        public static string StartMenuLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), "Crest.lnk"); }
        }
        public static string DesktopLink
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), "Crest.lnk"); }
        }
    }

    internal static class Program
    {
        [STAThread]
        private static void Main(string[] args)
        {
            // "/update" comes from Crest's own updater: keep the existing choices, install without asking,
            // and open Crest again when done. If anything fails, the normal window shows the error.
            bool update = Array.Exists(args, a => a.Equals("/update", StringComparison.OrdinalIgnoreCase));
            var app = new Application();
            app.ShutdownMode = ShutdownMode.OnMainWindowClose;
            app.Run(new SetupWindow(update));
        }
    }

    internal sealed class SetupWindow : Window
    {
        private static readonly Brush Bg = Hex("#1C1C1E"), Card = Hex("#2C2C2E"), Dim = Hex("#98989F"), Blue = Hex("#0A84FF");

        private readonly TextBlock _title = new TextBlock();
        private readonly TextBlock _text = new TextBlock();
        private readonly StackPanel _options = new StackPanel();
        private readonly CheckBox _desktop = new CheckBox();
        private readonly CheckBox _startup = new CheckBox();
        private readonly ProgressBar _bar = new ProgressBar();
        private readonly StackPanel _buttons = new StackPanel();
        private bool _busy;
        private readonly bool _update;

        public SetupWindow(bool update)
        {
            _update = update;
            Title = "Install Crest";
            Width = 520; SizeToContent = SizeToContent.Height;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
            Background = Bg;
            Foreground = Brushes.White;
            FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
            FontSize = 13;
            UseLayoutRounding = true;
            TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
            var icon = LoadIcon(256);
            if (icon != null) Icon = icon;

            var root = new StackPanel { Margin = new Thickness(28, 26, 28, 22) };

            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 18) };
            var img = new Image { Width = 64, Height = 64, Source = icon, Margin = new Thickness(0, 0, 16, 0) };
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            head.Children.Add(img);
            var names = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            _title.Text = "Install Crest";
            _title.FontSize = 22; _title.FontWeight = FontWeights.SemiBold;
            names.Children.Add(_title);
            names.Children.Add(new TextBlock { Text = "A dynamic island for Windows  ·  version " + Info.Version, Foreground = Dim, Margin = new Thickness(0, 2, 0, 0) });
            head.Children.Add(names);
            root.Children.Add(head);

            _text.TextWrapping = TextWrapping.Wrap;
            _text.Foreground = Dim;
            _text.Margin = new Thickness(0, 0, 0, 16);
            _text.Text = "Crest sits at the top of your screen and shows your music, clipboard, controls and system stats. "
                       + "It installs just for you, no administrator rights needed.";
            root.Children.Add(_text);

            _desktop.Content = "Add a shortcut to the desktop";
            _desktop.IsChecked = true;
            _startup.Content = "Start Crest when I sign in to Windows";
            _startup.IsChecked = true;
            foreach (var c in new[] { _desktop, _startup })
            {
                c.Foreground = Brushes.White;
                c.Margin = new Thickness(14, 10, 14, 10);
                c.VerticalContentAlignment = VerticalAlignment.Center;
                _options.Children.Add(c);
            }
            _options.Children.Insert(1, new Border { Height = 1, Background = Hex("#3A3A3C"), Margin = new Thickness(14, 0, 0, 0) });
            root.Children.Add(new Border { Background = Card, CornerRadius = new CornerRadius(10), Child = _options, Margin = new Thickness(0, 0, 0, 8) });

            root.Children.Add(new TextBlock
            {
                Text = "Location: " + Info.InstallDir,
                Foreground = Hex("#636366"), FontSize = 11.5, TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(2, 0, 0, 18),
            });

            _bar.Height = 6; _bar.Minimum = 0; _bar.Maximum = 1;
            _bar.Foreground = Blue; _bar.Background = Card; _bar.BorderThickness = new Thickness(0);
            _bar.Margin = new Thickness(0, 0, 0, 18);
            _bar.Visibility = Visibility.Collapsed;
            root.Children.Add(_bar);

            _buttons.Orientation = Orientation.Horizontal;
            _buttons.HorizontalAlignment = HorizontalAlignment.Right;
            root.Children.Add(_buttons);
            ShowButtons(Button("Cancel", false, Close), Button("Install", true, Install));

            if (update)
            {
                // Same shortcuts and sign-in choice as the copy being replaced.
                _desktop.IsChecked = File.Exists(Info.DesktopLink);
                using (var run = Registry.CurrentUser.OpenSubKey(Info.RunKey))
                    _startup.IsChecked = run != null && run.GetValue("Crest") != null;
                Title = "Updating Crest";
                ContentRendered += delegate { Install(); };
            }

            Content = root;
            SourceInitialized += delegate
            {
                int dark = 1;
                DwmSetWindowAttribute(new WindowInteropHelper(this).Handle, 20, ref dark, sizeof(int));
            };
            Closing += (s, e) => { if (_busy) e.Cancel = true; };
        }

        private void ShowButtons(params UIElement[] buttons)
        {
            _buttons.Children.Clear();
            foreach (var b in buttons) _buttons.Children.Add(b);
        }

        private static UIElement Button(string text, bool primary, Action click)
        {
            var idle = primary ? Blue : Hex("#3A3A3C");
            var hover = primary ? Hex("#409CFF") : Hex("#48484A");
            var b = new Border
            {
                Background = idle, CornerRadius = new CornerRadius(8), Padding = new Thickness(20, 7, 20, 8),
                Margin = new Thickness(10, 0, 0, 0), Cursor = Cursors.Hand, MinWidth = 96,
                Child = new TextBlock { Text = text, FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal, HorizontalAlignment = HorizontalAlignment.Center },
            };
            b.MouseEnter += delegate { b.Background = hover; };
            b.MouseLeave += delegate { b.Background = idle; };
            b.MouseLeftButtonUp += delegate { click(); };
            return b;
        }

        private void Install()
        {
            if (_busy) return;
            _busy = true;
            bool desktop = _desktop.IsChecked == true, startup = _startup.IsChecked == true;
            _options.IsEnabled = false;
            _options.Opacity = 0.5;
            _bar.Visibility = Visibility.Visible;
            _title.Text = _update ? "Updating Crest…" : "Installing…";
            ShowButtons();

            var worker = new Thread(() =>
            {
                string error = null;
                try { DoInstall(desktop, startup); }
                catch (Exception ex) { error = ex.Message; }
                Dispatcher.Invoke(new Action(() => Finished(error)));
            });
            worker.IsBackground = true;
            worker.Start();
        }

        private void Report(double p)
        {
            Dispatcher.BeginInvoke(new Action(() => _bar.Value = p));
        }

        private void DoInstall(bool desktop, bool startup)
        {
            string dir = Info.InstallDir;
            CloseRunningCrest(dir);

            // A clean copy every time: an update never mixes old and new files.
            if (Directory.Exists(dir))
            {
                for (int tries = 0; ; tries++)
                {
                    try { Directory.Delete(dir, true); break; }
                    catch (IOException) { if (tries > 20) throw; Thread.Sleep(250); }
                    catch (UnauthorizedAccessException) { if (tries > 20) throw; Thread.Sleep(250); }
                }
            }
            Directory.CreateDirectory(dir);

            long total = 0;
            using (var payload = Assembly.GetExecutingAssembly().GetManifestResourceStream("payload.zip"))
            using (var zip = new ZipArchive(payload, ZipArchiveMode.Read))
            {
                foreach (var e in zip.Entries) total += e.Length;
                long done = 0;
                string root = Path.GetFullPath(dir) + Path.DirectorySeparatorChar;
                foreach (var e in zip.Entries)
                {
                    string target = Path.GetFullPath(Path.Combine(dir, e.FullName));
                    if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                    if (e.FullName.EndsWith("/") || e.FullName.EndsWith("\\")) { Directory.CreateDirectory(target); continue; }
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    e.ExtractToFile(target, true);
                    done += e.Length;
                    Report(total == 0 ? 1 : 0.95 * done / total);
                }
            }

            string exe = Path.Combine(dir, "Crest.exe");
            string uninstaller = Path.Combine(dir, "Uninstall.exe");

            Shortcut(Info.StartMenuLink, exe, dir);
            if (desktop) Shortcut(Info.DesktopLink, exe, dir);
            else if (File.Exists(Info.DesktopLink)) File.Delete(Info.DesktopLink);

            using (var run = Registry.CurrentUser.CreateSubKey(Info.RunKey))
            {
                if (startup) run.SetValue("Crest", "\"" + exe + "\"");
                else run.DeleteValue("Crest", false);
            }

            long kb = total / 1024;
            using (var key = Registry.CurrentUser.CreateSubKey(Info.UninstallKey))
            {
                key.SetValue("DisplayName", Info.Name);
                key.SetValue("DisplayVersion", Info.Version);
                key.SetValue("Publisher", "Crest");
                key.SetValue("DisplayIcon", exe + ",0");
                key.SetValue("InstallLocation", dir);
                key.SetValue("UninstallString", "\"" + uninstaller + "\"");
                key.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /quiet");
                key.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                key.SetValue("EstimatedSize", (int)Math.Min(int.MaxValue, kb), RegistryValueKind.DWord);
                key.SetValue("NoModify", 1, RegistryValueKind.DWord);
                key.SetValue("NoRepair", 1, RegistryValueKind.DWord);
            }
            Report(1);
        }

        private void Finished(string error)
        {
            _busy = false;
            _bar.Visibility = Visibility.Collapsed;
            if (error != null)
            {
                _title.Text = "Crest could not be installed";
                _text.Text = error;
                _options.IsEnabled = true;
                _options.Opacity = 1;
                ShowButtons(Button("Close", false, Close), Button("Try again", true, Install));
                return;
            }
            if (_update)
            {
                OpenCrest();
                Close();
                return;
            }
            _title.Text = "Crest is installed";
            _text.Text = "Crest lives at the top of your screen. Move the pointer up to the island to open it. "
                       + "Right-click the Crest icon in the taskbar tray for settings. "
                       + "To remove it later, use Settings > Apps > Installed apps.";
            _options.Visibility = Visibility.Collapsed;
            ShowButtons(Button("Close", false, Close), Button("Open Crest", true, () =>
            {
                OpenCrest();
                Close();
            }));
        }

        private static void OpenCrest()
        {
            try { Process.Start(new ProcessStartInfo(Path.Combine(Info.InstallDir, "Crest.exe")) { UseShellExecute = true, WorkingDirectory = Info.InstallDir }); }
            catch { }
        }

        /// <summary>An update cannot replace files that are in use: close any running Crest first.</summary>
        private static void CloseRunningCrest(string dir)
        {
            foreach (var p in Process.GetProcessesByName("Crest"))
            {
                try { p.Kill(); p.WaitForExit(5000); }
                catch { }
                finally { p.Dispose(); }
            }
            foreach (var p in Process.GetProcessesByName("Uninstall"))
                p.Dispose();
        }

        private static void Shortcut(string link, string target, string workDir)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            object shell = Activator.CreateInstance(t);
            try
            {
                object lnk = t.InvokeMember("CreateShortcut", BindingFlags.InvokeMethod, null, shell, new object[] { link });
                Type lt = lnk.GetType();
                lt.InvokeMember("TargetPath", BindingFlags.SetProperty, null, lnk, new object[] { target });
                lt.InvokeMember("WorkingDirectory", BindingFlags.SetProperty, null, lnk, new object[] { workDir });
                lt.InvokeMember("IconLocation", BindingFlags.SetProperty, null, lnk, new object[] { target + ",0" });
                lt.InvokeMember("Description", BindingFlags.SetProperty, null, lnk, new object[] { "A dynamic island for Windows" });
                lt.InvokeMember("Save", BindingFlags.InvokeMethod, null, lnk, null);
                Marshal.FinalReleaseComObject(lnk);
            }
            finally { Marshal.FinalReleaseComObject(shell); }
        }

        private static BitmapSource LoadIcon(int size)
        {
            try
            {
                var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("Crest.ico");
                if (s == null) return null;
                var dec = new IconBitmapDecoder(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                BitmapFrame best = null;
                foreach (var f in dec.Frames)
                    if (best == null || Math.Abs(f.PixelWidth - size) < Math.Abs(best.PixelWidth - size)) best = f;
                return best;
            }
            catch { return null; }
        }

        private static SolidColorBrush Hex(string hex)
        {
            var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
            b.Freeze();
            return b;
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
    }
}
