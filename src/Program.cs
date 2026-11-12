using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Reflection;
using System.Threading;
using System.Windows;
using Forms = System.Windows.Forms;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;

namespace WinLaunch
{
    public static class Program
    {
        private const int HotkeyLaunchpad = 0xA100;
        private const int HotkeyDock = 0xA101;
        private static Mutex _mutex;
        private static EventWaitHandle _showEvent;
        private static TrayHost _tray;
        private static Application _app;

        /// <summary>程序版本（Major.Minor.Build），来自 AssemblyInfo 生成的程序集版本；写日志/标题用。</summary>
        public static string Version
        {
            get
            {
                try
                {
                    System.Version v = Assembly.GetEntryAssembly().GetName().Version;
                    return v.Major + "." + v.Minor + "." + v.Build;
                }
                catch { return "0.0.0"; }
            }
        }

        [STAThread]
        public static void Main(string[] args)
        {
            bool created;
            _mutex = new Mutex(true, "Local\\WinLaunch.SingleInstance", out created);
            if (!created)
            {
                try
                {
                    EventWaitHandle ev = EventWaitHandle.OpenExisting("Local\\WinLaunch.ShowLaunchpad");
                    ev.Set();
                }
                catch { }
                return;
            }

            // 先建好单实例唤醒事件，避免第二个实例在第一个实例还没开始监听时丢点击
            _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "Local\\WinLaunch.ShowLaunchpad");

            _app = new Application();
            _app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            _app.DispatcherUnhandledException += delegate(object s, DispatcherUnhandledExceptionEventArgs e)
            {
                Logger.Error("DispatcherUnhandled", e.Exception);
                e.Handled = true;
            };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            {
                Logger.Write("[FATAL] " + e.ExceptionObject);
            };

            Core.UI = Dispatcher.CurrentDispatcher;
            // 系统级 DPI（manifest 声明 system DPI aware）；图标按物理像素提取，避免高分屏发糊
            try
            {
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    Core.DpiScale = Math.Max(1.0, Math.Min(3.0, g.DpiX / 96.0));
            }
            catch { Core.DpiScale = 1.0; }
            IconService.Init(Core.UI);
            Core.Config = ConfigStore.Load();
            UsageStore.Load();
            if (!string.IsNullOrEmpty(ConfigStore.FallbackError))
                Logger.Write("[WARN] %APPDATA% 写入失败: " + ConfigStore.FallbackError);

            Logger.Write("[INFO] 数据目录: " + ConfigStore.AppDir
                + (ConfigStore.Portable ? "（便携模式：%APPDATA% 不可写，可能运行在受限环境）" : ""));

            // 自测入口：验证"切到已运行窗口"的能力
            if (args != null && args.Length >= 2 && args[0] == "--activate-test")
            {
                AppEntry probe = ProbeEntry(args[1]);
                bool okAct = LaunchService.TryActivate(probe);
                Logger.Write("[TEST] activate " + args[1] + " => " + okAct);
                Environment.Exit(0);
                return;
            }

            // 自测入口：验证“启动去重”（300ms 内连点两次，只应启动一次）
            if (args != null && args.Length >= 2 && args[0] == "--launch-test")
            {
                AppEntry probe = ProbeEntry(args[1]);
                bool first = LaunchService.LaunchOrActivate(probe);
                Thread.Sleep(300);
                bool second = LaunchService.LaunchOrActivate(probe);
                Logger.Write("[TEST] launch-twice " + args[1] + " => " + first + "/" + second);
                Thread.Sleep(300);
                Environment.Exit(0);
                return;
            }

            Logger.Write("[INFO] 启动 WinLaunch v" + Version + "，Dock 项 " + Core.Config.Dock.Items.Count);
            _app.Exit += delegate { Logger.Write("[INFO] Application.Exit"); };

            Core.Dock = new DockWindow();
            Core.Dock.Build();
            Core.Dock.Show();
            Core.Dock.Reposition();
            Core.Dock.BringAboveAll();

            RegisterHotkeys();

            bool openAtStart = args != null && Array.Exists(args, delegate(string a) { return a == "--launchpad"; });
            if (openAtStart)
            {
                Core.UI.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                {
                    if (Core.Launchpad == null) Core.Launchpad = new LaunchpadWindow();
                    Core.Launchpad.Open();
                }));
            }

            // 受限环境（写不了 %APPDATA%）：不再用模态 MessageBox 卡住整个 UI，
            // 只记日志 + 托盘气泡提示（每 3 天一次）。真正的解决办法是从普通目录运行。
            bool allowPortable = args != null && Array.Exists(args, delegate(string a) { return a == "--portable"; });
            bool warnPortable = ConfigStore.Portable && !allowPortable;

            _tray = new TrayHost();
            _tray.Attach();
            Core.Notify = _tray.ShowBalloon;
            if (warnPortable) WarnPortable();

            StartShowListener();
            StartCatalogScan();

            _app.Run();
        }

        /// <summary>供 --activate-test / --launch-test 构造一个测试条目。</summary>
        private static AppEntry ProbeEntry(string arg)
        {
            AppEntry probe = new AppEntry();
            probe.Name = "probe";
            if (arg.IndexOf('!') > 0 && !arg.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase)
                && !System.IO.File.Exists(arg))
            {
                probe.Kind = "uwp";
                probe.Target = arg;
            }
            else if (arg.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                ShortcutInfo si = ShortcutResolver.Resolve(arg);
                probe.Kind = "shortcut";
                probe.Target = arg;
                probe.ResolvedTarget = string.IsNullOrEmpty(si.Target) ? null : si.Target;
                probe.Args = si.Args;
                probe.WorkDir = si.WorkDir;
            }
            else
            {
                probe.Kind = "exe";
                probe.Target = arg;
                probe.ResolvedTarget = arg;
            }
            return probe;
        }

        /// <summary>便携模式提示：非阻塞，避免模态框挡住 Dock 的点击。</summary>
        private static void WarnPortable()
        {
            string stamp = System.IO.Path.Combine(ConfigStore.AppDir, "restricted.warned");
            try
            {
                if (System.IO.File.Exists(stamp) &&
                    (DateTime.Now - System.IO.File.GetLastWriteTime(stamp)).TotalDays <= 3)
                    return;
            }
            catch { }

            Logger.Write("[WARN] 受限环境：无法写入 %APPDATA%，已使用便携模式（不阻塞启动）");
            try { System.IO.File.WriteAllText(stamp, DateTime.Now.ToString("o")); } catch { }

            if (Core.Notify != null)
            {
                Core.Notify("WinLaunch · 运行环境受限",
                    "写不了 %APPDATA%，已使用便携模式。为保证启动的软件权限正常，请把程序放到普通目录（如 D:\\WinLaunch）再运行。");
            }
        }

        // ---------------- 应用扫描 ----------------

        private static void StartCatalogScan()
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    List<AppEntry> list = AppCatalog.Scan();
                    Core.UI.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        Core.Catalog = list;
                        if (Core.Config.Dock.Items.Count == 0) SeedDock(list);
                        if (Core.Dock != null) Core.Dock.OnCatalogChanged();
                        if (Core.Launchpad != null && Core.Launchpad.IsOpen) Core.Launchpad.RefreshApps();
                        else if (Core.FirstRun) Core.Dock.ToggleLaunchpad();
                    }));
                }
                catch (Exception ex)
                {
                    Logger.Error("StartCatalogScan", ex);
                }
            });
            t.IsBackground = true;
            t.Name = "catalog-scan";
            t.Start();
        }

        private static void SeedDock(List<AppEntry> list)
        {
            string[] prefer = new string[]
            {
                "Microsoft Edge", "Google Chrome", "Firefox", "浏览器",
                "微信", "QQ", "网易云音乐", "Visual Studio Code", "文件资源管理器", "设置"
            };
            List<AppEntry> picked = new List<AppEntry>();
            foreach (string p in prefer)
            {
                foreach (AppEntry e in list)
                {
                    if (e.Name != null && e.Name.IndexOf(p, StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        if (!picked.Contains(e) && picked.Count < 6) picked.Add(e);
                        break;
                    }
                }
                if (picked.Count >= 6) break;
            }
            foreach (AppEntry e in list)
            {
                if (picked.Count >= 5) break;
                if (!picked.Contains(e)) picked.Add(e);
            }

            Core.Config.Dock.Items.Clear();
            foreach (AppEntry e in picked) Core.Config.Dock.Items.Add(e);
            ConfigStore.Save();
            if (Core.Dock != null) Core.Dock.Rebuild();
        }

        // ---------------- 热键 ----------------

        private static void RegisterHotkeys()
        {
            IntPtr hwnd = new WindowInteropHelper(Core.Dock).Handle;
            Core.Dock.HotkeyPressed += OnHotkey;

            uint mods, vk;
            if (ParseHotkey(Core.Config.Hotkeys.ToggleLaunchpad, out mods, out vk))
            {
                bool okReg = Native.RegisterHotKey(hwnd, HotkeyLaunchpad, mods, vk);
                if (!okReg) Logger.Write("[WARN] 启动台热键注册失败：" + Core.Config.Hotkeys.ToggleLaunchpad);
            }
            else
            {
                Logger.Write("[WARN] 启动台热键解析失败：" + Core.Config.Hotkeys.ToggleLaunchpad);
            }
            if (ParseHotkey(Core.Config.Hotkeys.ToggleDock, out mods, out vk))
            {
                if (!Native.RegisterHotKey(hwnd, HotkeyDock, mods, vk))
                    Logger.Write("[WARN] Dock 热键注册失败（可能被占用）：" + Core.Config.Hotkeys.ToggleDock);
            }
        }

        private static void OnHotkey(int id)
        {
            if (id == HotkeyLaunchpad) Core.Dock.ToggleLaunchpad();
            else if (id == HotkeyDock) Core.Dock.ToggleVisible();
        }

        public static bool ParseHotkey(string text, out uint mods, out uint vk)
        {
            mods = 0;
            vk = 0;
            if (string.IsNullOrEmpty(text)) return false;
            string[] parts = text.Split('+');
            foreach (string raw in parts)
            {
                string p = raw.Trim();
                if (p.Length == 0) continue;
                if (p.Equals("Ctrl", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Control", StringComparison.OrdinalIgnoreCase)) { mods |= Native.MOD_CONTROL; continue; }
                if (p.Equals("Alt", StringComparison.OrdinalIgnoreCase)) { mods |= Native.MOD_ALT; continue; }
                if (p.Equals("Shift", StringComparison.OrdinalIgnoreCase)) { mods |= Native.MOD_SHIFT; continue; }
                if (p.Equals("Win", StringComparison.OrdinalIgnoreCase) ||
                    p.Equals("Windows", StringComparison.OrdinalIgnoreCase)) { mods |= Native.MOD_WIN; continue; }

                Key key;
                if (Enum.TryParse<Key>(p, true, out key))
                {
                    int v = KeyInterop.VirtualKeyFromKey(key);
                    if (v != 0) vk = (uint)v;
                }
            }
            return vk != 0 && mods != 0;
        }

        // ---------------- 单实例唤起 ----------------

        private static void StartShowListener()
        {
            Thread t = new Thread(delegate()
            {
                while (true)
                {
                    try
                    {
                        _showEvent.WaitOne();
                        Core.UI.BeginInvoke(new Action(delegate { Core.Dock.ToggleLaunchpad(); }));
                    }
                    catch (Exception ex)
                    {
                        Logger.Error("ShowListener", ex);
                        return;
                    }
                }
            });
            t.IsBackground = true;
            t.Name = "show-listener";
            t.Start();
        }

        // ---------------- 退出 ----------------

        public static void Shutdown()
        {
            try
            {
                if (_tray != null) _tray.Dispose();
                IntPtr hwnd = new WindowInteropHelper(Core.Dock).Handle;
                Native.UnregisterHotKey(hwnd, HotkeyLaunchpad);
                Native.UnregisterHotKey(hwnd, HotkeyDock);
                ConfigStore.Save();
            }
            catch (Exception ex)
            {
                Logger.Error("Shutdown", ex);
            }
            if (_app != null) _app.Shutdown();
            Environment.Exit(0);
        }
    }

    /// <summary>托盘图标与菜单。</summary>
    public class TrayHost : IDisposable
    {
        private Forms.NotifyIcon _icon;
        private Forms.ContextMenuStrip _menu;
        private Forms.ToolStripMenuItem _magnify;
        private Forms.ToolStripMenuItem _autoHide;
        private Forms.ToolStripMenuItem _startup;

        public void Attach()
        {
            _icon = new Forms.NotifyIcon();
            _icon.Text = "WinLaunch";
            _icon.Icon = LoadIcon();
            _icon.Visible = true;
            _icon.DoubleClick += delegate { Core.Dock.ToggleLaunchpad(); };

            _menu = new Forms.ContextMenuStrip();
            _menu.RenderMode = Forms.ToolStripRenderMode.System;

            Forms.ToolStripMenuItem open = new Forms.ToolStripMenuItem("打开启动台    Ctrl+Alt+L");
            open.Click += delegate { Core.Dock.ToggleLaunchpad(); };
            _menu.Items.Add(open);

            Forms.ToolStripMenuItem dock = new Forms.ToolStripMenuItem("显示 / 隐藏程序坞    Ctrl+Alt+D");
            dock.Click += delegate { Core.Dock.ToggleVisible(); };
            _menu.Items.Add(dock);

            _menu.Items.Add(new Forms.ToolStripSeparator());

            _magnify = new Forms.ToolStripMenuItem("悬停放大");
            _magnify.CheckOnClick = true;
            _magnify.Checked = Core.Config.Dock.Magnification;
            _magnify.CheckedChanged += delegate
            {
                Core.Config.Dock.Magnification = _magnify.Checked;
                ConfigStore.Save();
            };
            _menu.Items.Add(_magnify);

            _autoHide = new Forms.ToolStripMenuItem("自动隐藏程序坞");
            _autoHide.CheckOnClick = true;
            _autoHide.Checked = Core.Config.Dock.AutoHide;
            _autoHide.CheckedChanged += delegate
            {
                Core.Config.Dock.AutoHide = _autoHide.Checked;
                ConfigStore.Save();
            };
            _menu.Items.Add(_autoHide);

            _startup = new Forms.ToolStripMenuItem("开机自动启动");
            _startup.CheckOnClick = true;
            _startup.Checked = Startup.IsEnabled();
            _startup.CheckedChanged += delegate { Startup.SetEnabled(_startup.Checked); };
            _menu.Items.Add(_startup);

            _menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem rescan = new Forms.ToolStripMenuItem("重新扫描应用");
            rescan.Click += delegate { ProgramRescan(); };
            _menu.Items.Add(rescan);

            Forms.ToolStripMenuItem dir = new Forms.ToolStripMenuItem("打开配置目录");
            dir.Click += delegate
            {
                try { System.Diagnostics.Process.Start("explorer.exe", ConfigStore.AppDir); }
                catch { }
            };
            _menu.Items.Add(dir);

            _menu.Items.Add(new Forms.ToolStripSeparator());

            Forms.ToolStripMenuItem exit = new Forms.ToolStripMenuItem("退出 WinLaunch");
            exit.Click += delegate { Program.Shutdown(); };
            _menu.Items.Add(exit);

            _icon.ContextMenuStrip = _menu;
        }

        /// <summary>
        /// 重新扫描应用：重读开始菜单/UWP 清单，同时清空图标缓存重新提取。
        /// 两件事合成一个动作——应用换了图标、缓存里存过错图标，都能一次修好。
        /// </summary>
        private static void ProgramRescan()
        {
            Thread t = new Thread(delegate()
            {
                try
                {
                    IconService.ClearDiskCache();
                    List<AppEntry> list = AppCatalog.Scan();
                    Core.UI.BeginInvoke(DispatcherPriority.Background, new Action(delegate
                    {
                        Core.Catalog = list;
                        if (Core.Dock != null)
                        {
                            Core.Dock.OnCatalogChanged();
                            Core.Dock.Rebuild();      // 已固定图标也用新缓存重新取图
                        }
                        if (Core.Launchpad != null && Core.Launchpad.IsOpen) Core.Launchpad.RefreshApps();
                    }));
                }
                catch (Exception ex) { Logger.Error("ProgramRescan", ex); }
            });
            t.IsBackground = true;
            t.Name = "catalog-rescan";
            t.Start();
        }

        public void ShowBalloon(string title, string text)
        {
            try
            {
                if (_icon == null) return;
                _icon.BalloonTipTitle = title;
                _icon.BalloonTipText = text;
                _icon.BalloonTipIcon = Forms.ToolTipIcon.Warning;
                _icon.ShowBalloonTip(8000);
            }
            catch { }
        }

        private static Icon LoadIcon()
        {
            try
            {
                string exe = Assembly.GetEntryAssembly().Location;
                Icon ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
            catch { }
            return SystemIcons.Application;
        }

        public void Dispose()
        {
            if (_icon != null)
            {
                _icon.Visible = false;
                _icon.Dispose();
                _icon = null;
            }
            if (_menu != null)
            {
                _menu.Dispose();
                _menu = null;
            }
        }
    }
}
