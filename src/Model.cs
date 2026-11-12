using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;

namespace WinLaunch
{
    // =====================================================================
    // 数据模型
    // =====================================================================

    [DataContract]
    public class AppEntry
    {
        [DataMember(Name = "id")] public string Id;
        [DataMember(Name = "name")] public string Name;
        [DataMember(Name = "kind")] public string Kind;          // shortcut | exe | uwp | url | folder | shell
        [DataMember(Name = "target")] public string Target;
        [DataMember(Name = "resolvedTarget")] public string ResolvedTarget;
        [DataMember(Name = "args")] public string Args;
        [DataMember(Name = "workDir")] public string WorkDir;
        [DataMember(Name = "iconOverride")] public string IconOverride;

        // 运行时字段（不序列化）
        public bool IsRunning;
        public string Section;   // 系统项分组标记

        public override string ToString()
        {
            return string.IsNullOrEmpty(Name) ? Target : Name;
        }
    }

    [DataContract]
    public class DockSettings
    {
        [DataMember] public int IconSize = 44;
        [DataMember] public bool Magnification = true;
        [DataMember] public double MagnificationMax = 1.55;
        [DataMember] public bool AutoHide;
        [DataMember] public bool ShowRunningIndicator = true;
        [DataMember] public bool FollowCursorMonitor = true;
        [DataMember] public string Alignment = "center";
        [DataMember] public List<AppEntry> Items = new List<AppEntry>();
    }

    [DataContract]
    public class LaunchpadSettings
    {
        [DataMember] public int Columns = 7;
        [DataMember] public int Rows = 4;
        [DataMember] public string SortMode = "name";
        [DataMember] public bool HideUninstallers = true;
    }

    [DataContract]
    public class HotkeySettings
    {
        [DataMember] public string ToggleLaunchpad = "Ctrl+Alt+L";
        [DataMember] public string ToggleDock = "Ctrl+Alt+D";
    }

    [DataContract]
    public class Config
    {
        [DataMember] public int SchemaVersion = 1;
        [DataMember] public DockSettings Dock = new DockSettings();
        [DataMember] public LaunchpadSettings Launchpad = new LaunchpadSettings();
        [DataMember] public HotkeySettings Hotkeys = new HotkeySettings();
        [DataMember] public string Theme = "dark";
        [DataMember] public bool StartWithWindows;
        [DataMember] public string Locale = "zh-CN";
    }

    // =====================================================================
    // 全局上下文
    // =====================================================================

    public static class Core
    {
        public static Config Config;
        public static List<AppEntry> Catalog = new List<AppEntry>();
        public static DockWindow Dock;
        public static LaunchpadWindow Launchpad;
        public static Dispatcher UI;
        public static double DpiScale = 1.0;
        public static bool FirstRun;

        /// <summary>非阻塞提示（托盘气泡），由 Program 在托盘创建后注入。</summary>
        public static Action<string, string> Notify;
    }

    // =====================================================================
    // 工具
    // =====================================================================

    public static class Util
    {
        public static string Hash(string s)
        {
            if (s == null) s = "";
            using (SHA1 sha = SHA1.Create())
            {
                byte[] b = sha.ComputeHash(Encoding.UTF8.GetBytes(s.ToLowerInvariant()));
                StringBuilder sb = new StringBuilder();
                for (int i = 0; i < 6; i++) sb.Append(b[i].ToString("x2"));
                return sb.ToString();
            }
        }

        public static string SafeFileName(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char c in s)
            {
                if (char.IsLetterOrDigit(c) || c == '-' || c == '_') sb.Append(c);
                else sb.Append('_');
            }
            return sb.ToString();
        }

        public static bool SamePath(string a, string b)
        {
            if (string.IsNullOrEmpty(a) || string.IsNullOrEmpty(b)) return false;
            return string.Equals(a.Trim('"'), b.Trim('"'), StringComparison.OrdinalIgnoreCase);
        }

        public static string ExeNameOf(string path)
        {
            if (string.IsNullOrEmpty(path)) return "";
            try { return Path.GetFileNameWithoutExtension(path); }
            catch { return ""; }
        }
    }

    public static class Logger
    {
        private static readonly object Gate = new object();

        public static void Write(string msg)
        {
            try
            {
                string dir = Path.Combine(ConfigStore.AppDir, "logs");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "winlaunch-" + DateTime.Now.ToString("yyyyMMdd") + ".log");
                string line = string.Format("{0:HH:mm:ss.fff}  {1}{2}", DateTime.Now, msg, Environment.NewLine);
                lock (Gate) File.AppendAllText(file, line, Encoding.UTF8);
            }
            catch
            {
                // 日志失败不影响主流程
            }
        }

        public static void Error(string where, Exception ex)
        {
            Write("[ERR] " + where + " : " + ex.GetType().Name + " : " + ex.Message);
        }
    }

    // =====================================================================
    // 配置存储
    // =====================================================================

    public static class ConfigStore
    {
        private static string _root;

        /// <summary>true 表示 %APPDATA% 不可写，已回退到 exe 同级 data 目录（便携模式）。</summary>
        public static bool Portable;

        /// <summary>回退到便携模式时的异常摘要（供启动后一次性记录）。</summary>
        public static string FallbackError;

        /// <summary>配置根目录：优先 %APPDATA%\WinLaunch，不可写则回退 exe\data。</summary>
        public static string AppDir
        {
            get
            {
                if (_root != null) return _root;
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "WinLaunch");
                    Directory.CreateDirectory(dir);
                    string probe = Path.Combine(dir, ".probe");
                    File.WriteAllText(probe, "1");
                    File.Delete(probe);
                    _root = dir;
                }
                catch (Exception ex)
                {
                    // 注意：这里绝不能调用 Logger——Logger 依赖 AppDir，会无限递归导致栈溢出
                    Portable = true;
                    FallbackError = ex.GetType().Name + " : " + ex.Message;
                    string exeDir = Path.GetDirectoryName(
                        System.Reflection.Assembly.GetEntryAssembly().Location);
                    string dir2 = Path.Combine(exeDir, "data");
                    Directory.CreateDirectory(dir2);
                    _root = dir2;
                }
                return _root;
            }
        }

        public static string ConfigPath { get { return Path.Combine(AppDir, "config.json"); } }
        public static string BackupPath { get { return Path.Combine(AppDir, "config.bak.json"); } }

        public static string CacheDir
        {
            get
            {
                if (Portable)
                {
                    string d0 = Path.Combine(AppDir, "cache", "icons");
                    Directory.CreateDirectory(d0);
                    return d0;
                }
                try
                {
                    string dir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        "WinLaunch", "cache", "icons");
                    Directory.CreateDirectory(dir);
                    return dir;
                }
                catch
                {
                    string dir = Path.Combine(AppDir, "cache", "icons");
                    Directory.CreateDirectory(dir);
                    return dir;
                }
            }
        }

        public static Config Load()
        {
            Config cfg = TryRead(ConfigPath);
            if (cfg == null)
            {
                cfg = TryRead(BackupPath);
                if (cfg != null) Logger.Write("[WARN] 主配置损坏，已回滚备份");
            }
            if (cfg == null)
            {
                cfg = new Config();
                Core.FirstRun = true;
                Logger.Write("[INFO] 首次运行，使用默认配置");
            }
            if (cfg.Dock == null) cfg.Dock = new DockSettings();
            if (cfg.Dock.Items == null) cfg.Dock.Items = new List<AppEntry>();
            if (cfg.Launchpad == null) cfg.Launchpad = new LaunchpadSettings();
            if (cfg.Hotkeys == null) cfg.Hotkeys = new HotkeySettings();
            return cfg;
        }

        private static Config TryRead(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] raw = File.ReadAllBytes(path);
                int offset = (raw.Length >= 3 && raw[0] == 0xEF && raw[1] == 0xBB && raw[2] == 0xBF) ? 3 : 0;
                using (MemoryStream ms = new MemoryStream(raw, offset, raw.Length - offset))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(Config));
                    Config c = (Config)ser.ReadObject(ms);
                    return c;
                }
            }
            catch (Exception ex)
            {
                Logger.Error("ConfigStore.TryRead(" + path + ")", ex);
                return null;
            }
        }

        public static void Save()
        {
            if (Core.Config == null) return;
            try
            {
                string tmp = ConfigPath + ".tmp";
                using (FileStream fs = new FileStream(tmp, FileMode.Create, FileAccess.Write))
                {
                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(Config));
                    ser.WriteObject(fs, Core.Config);
                }
                if (File.Exists(ConfigPath))
                    File.Replace(tmp, ConfigPath, BackupPath, true);
                else
                    File.Move(tmp, ConfigPath);
            }
            catch (Exception ex)
            {
                Logger.Error("ConfigStore.Save", ex);
            }
        }
    }

    // =====================================================================
    // 开机自启
    // =====================================================================

    public static class Startup
    {
        private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
        private const string ValueName = "WinLaunch";

        public static bool IsEnabled()
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey))
                {
                    if (k == null) return false;
                    return k.GetValue(ValueName) != null;
                }
            }
            catch { return false; }
        }

        public static void SetEnabled(bool on)
        {
            try
            {
                using (Microsoft.Win32.RegistryKey k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey, true))
                {
                    if (k == null) return;
                    if (on)
                    {
                        string exe = System.Reflection.Assembly.GetEntryAssembly().Location;
                        k.SetValue(ValueName, "\"" + exe + "\" --autostart");
                    }
                    else
                    {
                        k.DeleteValue(ValueName, false);
                    }
                }
                Core.Config.StartWithWindows = on;
                ConfigStore.Save();
            }
            catch (Exception ex)
            {
                Logger.Error("Startup.SetEnabled", ex);
            }
        }
    }

    // =====================================================================
    // 小工具：视觉
    // =====================================================================

    public static class Brush2
    {
        public static Color Hex(string hex)
        {
            hex = hex.TrimStart('#');
            byte a = 255;
            if (hex.Length == 8)
            {
                a = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
                hex = hex.Substring(2);
            }
            byte r = byte.Parse(hex.Substring(0, 2), NumberStyles.HexNumber);
            byte g = byte.Parse(hex.Substring(2, 2), NumberStyles.HexNumber);
            byte b = byte.Parse(hex.Substring(4, 2), NumberStyles.HexNumber);
            return Color.FromArgb(a, r, g, b);
        }

        public static SolidColorBrush Solid(string hex)
        {
            SolidColorBrush br = new SolidColorBrush(Hex(hex));
            br.Freeze();
            return br;
        }

        public static LinearGradientBrush Vertical(string topHex, string bottomHex)
        {
            LinearGradientBrush br = new LinearGradientBrush(Hex(topHex), Hex(bottomHex), 90);
            br.Freeze();
            return br;
        }

        public static string Initial(AppEntry e)
        {
            string n = e.Name;
            if (string.IsNullOrEmpty(n)) return "?";
            n = n.Trim();
            if (n.Length == 0) return "?";
            char c = n[0];
            if (char.IsLetterOrDigit(c)) return c.ToString().ToUpperInvariant();
            return "?";
        }

        public static string[] Palette = new string[]
        {
            "#6B7BFF", "#34C759", "#FF9F0A", "#FF6B6B", "#22D3EE", "#A855F7",
            "#F472B6", "#4C8DFF", "#10B981", "#F59E0B", "#EF4444", "#8B5CF6",
            "#06B6D4", "#EC4899", "#3B82F6", "#22C55E", "#F97316", "#E11D48",
            "#7C3AED", "#0EA5E9", "#D946EF"
        };

        public static string ColorFor(AppEntry e)
        {
            int h = 0;
            string s = e.Name ?? e.Target ?? "";
            for (int i = 0; i < s.Length; i++) h = (h * 31 + s[i]) & 0x7fffffff;
            return Palette[h % Palette.Length];
        }
    }
}
