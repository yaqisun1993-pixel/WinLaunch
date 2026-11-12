using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;
using System.Threading;

namespace WinLaunch
{
    /// <summary>应用清单：扫描开始菜单 + 系统项，去重、排序、提供索引。</summary>
    public static class AppCatalog
    {
        private static readonly string[] ExcludeWords = new string[]
        {
            "卸载", "uninstall", "readme", "帮助", "help", "官网", "website", "网站",
            "更新", "update", "修复", "repair", "license", "许可", "文档", "documentation",
            "命令行", "command prompt"
        };

        public static List<AppEntry> Scan()
        {
            Stopwatch sw = Stopwatch.StartNew();
            List<AppEntry> result = new List<AppEntry>();
            List<string> lnks = new List<string>();

            string[] roots = new string[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonStartMenu), "Programs"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.StartMenu), "Programs")
            };

            foreach (string root in roots)
            {
                try
                {
                    if (!Directory.Exists(root)) continue;
                    lnks.AddRange(Directory.GetFiles(root, "*.lnk", SearchOption.AllDirectories));
                }
                catch (Exception ex)
                {
                    Logger.Error("AppCatalog.Enumerate(" + root + ")", ex);
                }
            }

            Dictionary<string, AppEntry> seen = new Dictionary<string, AppEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (string lnk in lnks)
            {
                try
                {
                    string name = Path.GetFileNameWithoutExtension(lnk);
                    if (IsExcluded(name)) continue;

                    ShortcutInfo info = ShortcutResolver.Resolve(lnk);
                    string resolved = info.Target;

                    // 指向卸载程序 / 帮助的快捷方式再次过滤
                    if (!string.IsNullOrEmpty(resolved) && IsExcluded(Path.GetFileNameWithoutExtension(resolved)))
                        continue;

                    string key = string.IsNullOrEmpty(resolved) ? lnk : resolved;
                    if (seen.ContainsKey(key))
                    {
                        // 同名同目标：优先保留路径更短（更"官方"）的一条
                        continue;
                    }

                    AppEntry e = new AppEntry();
                    e.Id = Util.Hash(key);
                    e.Name = CleanName(name);
                    e.Kind = "shortcut";
                    e.Target = lnk;
                    e.ResolvedTarget = string.IsNullOrEmpty(resolved) ? null : resolved;
                    e.Args = info.Args;
                    e.WorkDir = info.WorkDir;
                    seen[key] = e;
                }
                catch (Exception ex)
                {
                    Logger.Error("AppCatalog.Entry(" + lnk + ")", ex);
                }
            }

            result.AddRange(seen.Values);

            // UWP / 商店应用（Get-StartApps，带 AUMID 才能启动）
            foreach (AppEntry uwp in ScanUwp(result))
            {
                bool dup = false;
                foreach (AppEntry a in result)
                    if (string.Equals(a.Name, uwp.Name, StringComparison.OrdinalIgnoreCase)) { dup = true; break; }
                if (!dup) result.Add(uwp);
            }

            // 系统项（去重后追加）
            AddSystem(result, seen, "设置", "ms-settings:", "shell");
            AddSystem(result, seen, "任务管理器", Path.Combine(Environment.SystemDirectory, "Taskmgr.exe"), "exe");
            AddSystem(result, seen, "命令提示符", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "exe");
            AddSystem(result, seen, "控制面板", "control.exe", "shell");


            result.Sort(new PinyinComparer());

            sw.Stop();
            Logger.Write(string.Format("[INFO] 扫描完成：{0} 个应用，耗时 {1} ms", result.Count, sw.ElapsedMilliseconds));
            return result;
        }

        [DataContract]
        private class UwpApp
        {
            [DataMember(Name = "Name")] public string Name = null;
            [DataMember(Name = "AppID")] public string AppId = null;
        }

        /// <summary>枚举 UWP 应用（AppID 形如 xxx!App）。失败时静默返回空集合。</summary>
        private static List<AppEntry> ScanUwp(List<AppEntry> existing)
        {
            List<AppEntry> list = new List<AppEntry>();
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.FileName = "powershell.exe";
                psi.Arguments = "-NoProfile -NonInteractive -Command \"[Console]::OutputEncoding=[System.Text.Encoding]::UTF8; Get-StartApps | ConvertTo-Json -Compress\"";
                psi.UseShellExecute = false;
                psi.RedirectStandardOutput = true;
                psi.CreateNoWindow = true;
                psi.StandardOutputEncoding = Encoding.UTF8;

                using (Process p = Process.Start(psi))
                {
                    // 先异步读输出再等超时：ReadToEnd 会一直阻塞，WaitForExit(8000) 形同虚设
                    StringBuilder output = new StringBuilder();
                    p.OutputDataReceived += delegate(object s, DataReceivedEventArgs e)
                    {
                        if (e.Data != null) output.AppendLine(e.Data);
                    };
                    p.BeginOutputReadLine();
                    if (!p.WaitForExit(8000))
                    {
                        try { p.Kill(); } catch { }
                        Logger.Write("[WARN] Get-StartApps 超时，已跳过 UWP 扫描");
                        return list;
                    }
                    p.WaitForExit();   // 等异步输出回调清空
                    string json = output.ToString();
                    if (string.IsNullOrEmpty(json)) return list;

                    DataContractJsonSerializer ser = new DataContractJsonSerializer(typeof(List<UwpApp>));
                    using (MemoryStream ms = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    {
                        List<UwpApp> items = ser.ReadObject(ms) as List<UwpApp>;
                        if (items == null) return list;
                        foreach (UwpApp it in items)
                        {
                            if (string.IsNullOrEmpty(it.AppId)) continue;
                            if (it.AppId.IndexOf('!') < 0) continue;      // 只要 UWP
                            if (string.IsNullOrEmpty(it.Name)) continue;

                            AppEntry e = new AppEntry();
                            e.Id = Util.Hash("uwp:" + it.AppId);
                            e.Name = it.Name;
                            e.Kind = "uwp";
                            e.Target = it.AppId;
                            e.Section = "UWP";
                            list.Add(e);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("AppCatalog.ScanUwp", ex);
            }
            return list;
        }
        private static void AddSystem(List<AppEntry> list, Dictionary<string, AppEntry> seen,
                                      string name, string target, string kind)
        {
            string key = target.ToLowerInvariant();
            if (seen.ContainsKey(key)) return;
            foreach (AppEntry a in list)
                if (string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)) return;

            AppEntry e = new AppEntry();
            e.Id = Util.Hash(key);
            e.Name = name;
            e.Kind = kind;
            e.Target = target;
            e.ResolvedTarget = kind == "exe" ? target : null;
            e.Section = "系统";
            seen[key] = e;
            list.Add(e);
        }

        private static bool IsExcluded(string name)
        {
            if (string.IsNullOrEmpty(name)) return true;
            string low = name.ToLowerInvariant();
            foreach (string w in ExcludeWords)
                if (low.Contains(w)) return true;
            return false;
        }

        private static string CleanName(string name)
        {
            name = name.Trim();
            return name.Replace("_", " ");
        }

        /// <summary>
        /// 判定"Windows 自带应用"：系统目录里的程序、WindowsApps 里的 UWP、
        /// 以及 Microsoft.* 的商店应用与系统级 shell 项（设置、控制面板等）。
        /// </summary>
        public static bool IsBuiltIn(AppEntry e)
        {
            if (e == null) return false;
            string kind = e.Kind ?? "";
            string target = e.Target ?? "";

            if (string.Equals(kind, "uwp", StringComparison.OrdinalIgnoreCase))
            {
                // 微软商店应用用发布者哈希 _8wekyb3d8bbwe，系统应用用 _cw5n1h2txyewy；
                // 少数还以 Microsoft. / Windows. / MicrosoftWindows. 开头，一并视为自带
                string t = target.ToLowerInvariant();
                return t.Contains("_8wekyb3d8bbwe")
                    || t.Contains("_cw5n1h2txyewy")
                    || t.StartsWith("microsoft.")
                    || t.StartsWith("windows.");
            }

            if (string.Equals(kind, "shell", StringComparison.OrdinalIgnoreCase)) return true;

            // Windows 自带的经典工具都挂在开始菜单的这几个目录下，
            // 用户装的软件一般在厂商目录（腾讯软件、NVIDIA 等）或 Programs 根下
            if (string.Equals(kind, "shortcut", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrEmpty(target))
            {
                string t = target.ToLowerInvariant();
                if (t.Contains(@"\programs\accessories\")
                    || t.Contains(@"\programs\system tools\")
                    || t.Contains(@"\programs\windows accessories\")
                    || t.Contains(@"\programs\windows system\")
                    || t.Contains(@"\programs\windows 管理工具\")
                    || t.Contains(@"\programs\administrative tools\")
                    || t.Contains(@"\programs\accessibility\"))
                    return true;
            }

            string path = e.ResolvedTarget;
            if (string.IsNullOrEmpty(path)) path = target;

            if (path.StartsWith(@"C:\Windows\", StringComparison.OrdinalIgnoreCase)) return true;
            if (path.StartsWith(@"C:\Program Files\WindowsApps\", StringComparison.OrdinalIgnoreCase)) return true;

            return false;
        }

        /// <summary>排序：第三方应用在前，Windows 自带整组在后；组内按中文拼音序。</summary>
        private class PinyinComparer : IComparer<AppEntry>
        {
            private static readonly CompareInfo Ci =
                CultureInfo.GetCultureInfo("zh-CN").CompareInfo;

            public int Compare(AppEntry x, AppEntry y)
            {
                bool bx = IsBuiltIn(x);
                bool by = IsBuiltIn(y);
                if (bx != by) return bx ? 1 : -1;

                int c = Ci.Compare(x.Name, y.Name, CompareOptions.IgnoreCase);
                if (c != 0) return c;
                return string.CompareOrdinal(x.Id, y.Id);
            }
        }
    }

    /// <summary>启动与实例激活。</summary>
    public static class LaunchService
    {
        public static bool Launch(AppEntry e)
        {
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo();
                psi.UseShellExecute = true;

                if (string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = "explorer.exe";
                    psi.Arguments = "shell:AppsFolder\\" + e.Target;
                }
                else if (string.Equals(e.Kind, "shell", StringComparison.OrdinalIgnoreCase))
                {
                    psi.FileName = "explorer.exe";
                    psi.Arguments = e.Target;
                }
                else if (string.Equals(e.Kind, "shortcut", StringComparison.OrdinalIgnoreCase))
                {
                    // .lnk 自带参数与工作目录，交给 Shell 解析即可；
                    // 再传一遍参数会导致某些程序（Steam 等）收到重复参数
                    psi.FileName = e.Target;
                }
                else
                {
                    psi.FileName = e.Target;
                    if (!string.IsNullOrEmpty(e.Args)) psi.Arguments = e.Args;
                    if (!string.IsNullOrEmpty(e.WorkDir) && Directory.Exists(e.WorkDir))
                        psi.WorkingDirectory = e.WorkDir;
                }

                Process.Start(psi);
                UsageStore.Touch(e);
                Logger.Write("[LAUNCH] " + e.Name + "  ←  " + psi.FileName + " " + psi.Arguments);
                return true;
            }
            catch (Exception ex)
            {
                Logger.Error("LaunchService.Launch(" + e.Name + ")", ex);
                if (Core.Notify != null)
                    Core.Notify("无法启动 " + (string.IsNullOrEmpty(e.Name) ? "应用" : e.Name), ex.Message);
                return false;
            }
        }

        /// <summary>
        /// 激活已在运行的实例：按"窗口所属进程的镜像路径"匹配，
        /// 而不是 Process.MainWindowHandle（Chromium/Steam 这类多进程应用该值恒为 0）。
        /// </summary>
        public static bool TryActivate(AppEntry e)
        {
            if (e == null) return false;

            bool isUwp = string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase);
            string target = e.ResolvedTarget;
            // UWP 没有 exe 路径，靠 AUMID 识别；普通应用必须有目标路径
            if (!isUwp && string.IsNullOrEmpty(target)) return false;

            string targetName = string.IsNullOrEmpty(target) ? null : Util.ExeNameOf(target);

            // 先按进程名拿到候选 pid 集合：不依赖 OpenProcess，受限环境下也能用
            HashSet<int> pids = new HashSet<int>();
            if (!string.IsNullOrEmpty(targetName))
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(targetName))
                    {
                        try { pids.Add(p.Id); } catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch (Exception ex) { Logger.Error("LaunchService.TryActivate/pid", ex); }
            }

            string targetAumid = TargetAumid(e);
            List<IntPtr> byAumid = new List<IntPtr>();
            List<IntPtr> byPath = new List<IntPtr>();
            List<IntPtr> byName = new List<IntPtr>();
            // 桌面歌词 / 迷你播放器 / 托盘图标窗这类 WS_EX_TOOLWINDOW 只作兜底
            List<IntPtr> byAumidAny = new List<IntPtr>();
            List<IntPtr> byPathAny = new List<IntPtr>();
            List<IntPtr> byNameAny = new List<IntPtr>();

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (!Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;   // GW_OWNER，跳过子窗口
                if (IsTinyWindow(h)) return true;

                StringBuilder title = new StringBuilder(256);
                Native.GetWindowText(h, title, 256);
                if (title.Length == 0) return true;

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                bool tool = IsToolWindow(h) || IsNoActivate(h);

                if (pids.Contains((int)pid))
                {
                    if (tool) byNameAny.Add(h); else byName.Add(h);
                }

                // AUMID 优先级最高：任务栏就是靠它把窗口归到图标下的
                if (!string.IsNullOrEmpty(targetAumid))
                {
                    IntPtr hp = Native.OpenProcess(0x1000, false, (int)pid);
                    if (hp != IntPtr.Zero)
                    {
                        try
                        {
                            string wa = Native.ProcessAumid(hp);
                            if (!string.IsNullOrEmpty(wa) &&
                                string.Equals(wa, targetAumid, StringComparison.OrdinalIgnoreCase))
                            {
                                if (tool) byAumidAny.Add(h); else byAumid.Add(h);
                            }
                        }
                        finally { Native.CloseHandle(hp); }
                    }
                }

                // 路径匹配（可覆盖窗口挂在子进程上的情况）
                string imagePath = string.IsNullOrEmpty(target) ? null : ProcessImagePath((int)pid);
                if (!string.IsNullOrEmpty(imagePath) && Util.SamePath(imagePath, target))
                {
                    if (tool) byPathAny.Add(h); else byPath.Add(h);
                    return true;
                }
                if (!string.IsNullOrEmpty(imagePath) && !string.IsNullOrEmpty(targetName) &&
                    string.Equals(Util.ExeNameOf(imagePath), targetName, StringComparison.OrdinalIgnoreCase))
                {
                    if (tool)
                    {
                        if (!byNameAny.Contains(h)) byNameAny.Add(h);
                    }
                    else if (!byName.Contains(h)) byName.Add(h);
                }
                return true;
            }, IntPtr.Zero);

            // 可见窗口按“像主窗口”的程度排序，而不是按 EnumWindows 的 Z 序取第一个
            SortByScore(byAumid);
            SortByScore(byPath);
            SortByScore(byName);
            SortByScore(byAumidAny);
            SortByScore(byPathAny);
            SortByScore(byNameAny);

            List<ActCandidate> candidates = new List<ActCandidate>();
            HashSet<IntPtr> seen = new HashSet<IntPtr>();
            AddCandidates(candidates, seen, byAumid, "aumid");
            AddCandidates(candidates, seen, byPath, "path");
            AddCandidates(candidates, seen, byName, "name");

            // 可见窗口里可能只有“桌面歌词/迷你播放器”这种工具窗，真正的主窗口被应用隐藏了。
            // 优先找隐藏主窗口，而不是反复激活工具窗。
            if (pids.Count > 0) CollectHiddenCandidates(pids, candidates, seen);

            // 再兜底：只有工具窗的应用（旧行为）
            AddCandidates(candidates, seen, byAumidAny, "aumid-tool");
            AddCandidates(candidates, seen, byPathAny, "path-tool");
            AddCandidates(candidates, seen, byNameAny, "name-tool");

            if (candidates.Count == 0)
            {
                Logger.Write("[ACTIVATE-MISS] " + e.Name + " target=" + Path.GetFileName(target)
                    + " aumid='" + (targetAumid ?? "") + "' pids=" + pids.Count
                    + " aumidHits=" + byAumid.Count + " pathHits=" + byPath.Count + " nameHits=" + byName.Count);
                return false;
            }

            // “找到窗口”不等于“激活成功”：逐个尝试，校验 IsWindowVisible/IsIconic，
            // 失败换下一个候选，全部失败才返回 false（由调用方决定重新启动还是提示）。
            ActCandidate chosen = null;
            foreach (ActCandidate c in candidates)
            {
                if (ActivateWindow(c.Hwnd)) { chosen = c; break; }
                Logger.Write("[ACTIVATE-RETRY] " + e.Name + " hwnd=" + c.Hwnd + " via=" + c.Via);
            }

            if (chosen == null)
            {
                Logger.Write("[ACTIVATE-MISS] " + e.Name + " 找到 " + candidates.Count + " 个候选窗口，但都没能还原/置前");
                return false;
            }

            UsageStore.Touch(e);
            bool fg = Native.GetForegroundWindow() == chosen.Hwnd;
            Logger.Write("[ACTIVATE] " + e.Name + " hwnd=" + chosen.Hwnd + " via=" + chosen.Via
                + " visible=" + Native.IsWindowVisible(chosen.Hwnd)
                + " fg=" + fg + " candidates=" + candidates.Count);
            if (!fg)
                Logger.Write("[ACTIVATE-WARN] " + e.Name + " 已还原但未置前 hwnd=" + chosen.Hwnd);
            return true;
        }

        private class ActCandidate
        {
            public IntPtr Hwnd;
            public string Via;
            public ActCandidate(IntPtr h, string via) { Hwnd = h; Via = via; }
        }

        private class ScoredWindow
        {
            public IntPtr Hwnd;
            public long Score;
        }

        private static void AddCandidates(List<ActCandidate> list, HashSet<IntPtr> seen, List<IntPtr> src, string via)
        {
            if (src == null) return;
            foreach (IntPtr h in src)
                if (seen.Add(h)) list.Add(new ActCandidate(h, via));
        }

        private static void SortByScore(List<IntPtr> list)
        {
            if (list == null || list.Count < 2) return;
            list.Sort(delegate(IntPtr a, IntPtr b) { return WindowScore(b).CompareTo(WindowScore(a)); });
        }

        private static bool IsToolWindow(IntPtr h)
        {
            try { return (Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_TOOLWINDOW) != 0; }
            catch { return false; }
        }

        private static bool IsNoActivate(IntPtr h)
        {
            try { return (Native.GetWindowLong(h, Native.GWL_EXSTYLE) & Native.WS_EX_NOACTIVATE) != 0; }
            catch { return false; }
        }

        private static bool IsTinyWindow(IntPtr h)
        {
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return false;
            return (r.Right - r.Left) < 8 || (r.Bottom - r.Top) < 8;
        }

        private static long WindowScore(IntPtr h)
        {
            long score = 0;
            try
            {
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_APPWINDOW) != 0) score += 1000000000L;   // 任务栏窗口优先
                Native.RECT r;
                if (Native.GetWindowRect(h, out r))
                {
                    long w = Math.Max(0, r.Right - r.Left);
                    long ht = Math.Max(0, r.Bottom - r.Top);
                    score += Math.Min(w * ht, 999999999L);
                }
            }
            catch { }
            return score;
        }

        /// <summary>
        /// 收集被隐藏的主窗口候选（如网易云音乐最小化后 OrpheusBrowserHost 变成 hidden）。
        /// 跳过桌面歌词、托盘图标、IME 等辅助窗，按 APPWINDOW + 面积排序；先主窗口，后工具窗。
        /// </summary>
        private static void CollectHiddenCandidates(HashSet<int> pids, List<ActCandidate> candidates, HashSet<IntPtr> seen)
        {
            List<ScoredWindow> preferred = new List<ScoredWindow>();
            List<ScoredWindow> fallback = new List<ScoredWindow>();

            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                if (Native.IsWindowVisible(h)) return true;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                if (IsTinyWindow(h)) return true;

                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;

                StringBuilder title = new StringBuilder(256);
                Native.GetWindowText(h, title, 256);
                if (title.Length == 0) return true;

                StringBuilder cls = new StringBuilder(256);
                Native.GetClassName(h, cls, 256);
                string cn = cls.ToString();
                if (cn.StartsWith("IME", StringComparison.OrdinalIgnoreCase)
                    || cn.StartsWith("MSCTF", StringComparison.OrdinalIgnoreCase)
                    || cn.StartsWith("Cicero", StringComparison.OrdinalIgnoreCase)
                    || cn.StartsWith("GDI+", StringComparison.OrdinalIgnoreCase)
                    || cn.Equals("icon", StringComparison.OrdinalIgnoreCase)
                    || cn.Equals("Base_PowerMessageWindow", StringComparison.OrdinalIgnoreCase)
                    || cn.Equals("Chrome_SystemMessageWindow", StringComparison.OrdinalIgnoreCase))
                    return true;

                ScoredWindow sw = new ScoredWindow();
                sw.Hwnd = h;
                sw.Score = WindowScore(h);
                if (IsToolWindow(h) || IsNoActivate(h)) fallback.Add(sw);
                else preferred.Add(sw);
                return true;
            }, IntPtr.Zero);

            preferred.Sort(delegate(ScoredWindow a, ScoredWindow b) { return b.Score.CompareTo(a.Score); });
            fallback.Sort(delegate(ScoredWindow a, ScoredWindow b) { return b.Score.CompareTo(a.Score); });

            foreach (ScoredWindow sw in preferred)
                if (seen.Add(sw.Hwnd)) candidates.Add(new ActCandidate(sw.Hwnd, "hidden"));
            foreach (ScoredWindow sw in fallback)
                if (seen.Add(sw.Hwnd)) candidates.Add(new ActCandidate(sw.Hwnd, "hidden-tool"));
        }

        private static readonly Dictionary<string, string> AumidCache = new Dictionary<string, string>();

        // ---------- 启动去重：目标窗口出现前的重复点击只允许一次 Launch ----------
        private static readonly Dictionary<string, DateTime> PendingLaunches = new Dictionary<string, DateTime>();
        private static readonly object PendingGate = new object();
        private const double LaunchGraceSeconds = 15.0;

        private static string LaunchKey(AppEntry e)
        {
            // 优先用解析后的 exe 路径：同一个应用的不同快捷方式/入口能归到同一个 key
            if (!string.IsNullOrEmpty(e.ResolvedTarget)) return e.ResolvedTarget.ToLowerInvariant();
            if (!string.IsNullOrEmpty(e.Target)) return e.Target.ToLowerInvariant();
            if (!string.IsNullOrEmpty(e.Id)) return e.Id;
            return (e.Name ?? "").ToLowerInvariant();
        }

        private static void ClearPending(string key)
        {
            if (string.IsNullOrEmpty(key)) return;
            lock (PendingGate) PendingLaunches.Remove(key);
        }

        /// <summary>条目对应的应用标识：UWP 直接用 AppID，快捷方式读其 AUMID 属性。</summary>
        private static string TargetAumid(AppEntry e)
        {
            if (e == null) return null;
            if (string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase)) return e.Target;
            if (!string.Equals(e.Kind, "shortcut", StringComparison.OrdinalIgnoreCase)) return null;
            if (string.IsNullOrEmpty(e.Target)) return null;

            string cached;
            if (AumidCache.TryGetValue(e.Target, out cached)) return cached;

            string aumid = Native.ShortcutAumid(e.Target);
            AumidCache[e.Target] = aumid;
            return aumid;
        }

        private static bool ActivateWindow(IntPtr hwnd)
        {
            try
            {
                if (Native.IsIconic(hwnd)) Native.ShowWindow(hwnd, Native.SW_RESTORE);
                else Native.ShowWindow(hwnd, Native.SW_SHOW);

                Native.SetWindowPos(hwnd, Native.HWND_TOP, 0, 0, 0, 0,
                    Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_SHOWWINDOW);

                IntPtr fg = Native.GetForegroundWindow();
                uint fgThread = Native.GetWindowThreadProcessId(fg, IntPtr.Zero);
                uint targetPid;
                uint targetThread = Native.GetWindowThreadProcessId(hwnd, out targetPid);
                uint myThread = Native.GetCurrentThreadId();
                bool attachFg = false, attachTarget = false;
                try
                {
                    if (fgThread != myThread) attachFg = Native.AttachThreadInput(myThread, fgThread, true);
                    if (targetThread != myThread && targetThread != fgThread)
                        attachTarget = Native.AttachThreadInput(myThread, targetThread, true);
                    Native.BringWindowToTop(hwnd);
                    Native.SetForegroundWindow(hwnd);
                }
                finally
                {
                    if (attachTarget) Native.AttachThreadInput(myThread, targetThread, false);
                    if (attachFg) Native.AttachThreadInput(myThread, fgThread, false);
                }

                // SetForegroundWindow 被 Windows 前台锁拒绝时，用任务栏同款方式兜底
                if (Native.GetForegroundWindow() != hwnd)
                    Native.SwitchToThisWindow(hwnd, true);

                // 后置校验：只有在“确实可见且不再最小化”时才算成功
                return Native.IsWindowVisible(hwnd) && !Native.IsIconic(hwnd);
            }
            catch (Exception ex)
            {
                Logger.Error("LaunchService.ActivateWindow", ex);
                return false;
            }
        }

        internal static string ProcessImagePath(int pid)
        {
            IntPtr h = Native.OpenProcess(0x1000, false, pid);   // PROCESS_QUERY_LIMITED_INFORMATION
            if (h == IntPtr.Zero) return null;
            try
            {
                StringBuilder sb = new StringBuilder(1024);
                int size = sb.Capacity;
                if (Native.QueryFullProcessImageName(h, 0, sb, ref size)) return sb.ToString();
                return null;
            }
            finally
            {
                Native.CloseHandle(h);
            }
        }

        public static bool IsRunning(AppEntry e)
        {
            if (string.IsNullOrEmpty(e.ResolvedTarget)) return false;
            string exeName = Util.ExeNameOf(e.ResolvedTarget);
            if (string.IsNullOrEmpty(exeName)) return false;
            try
            {
                Process[] procs = Process.GetProcessesByName(exeName);
                bool any = procs.Length > 0;
                foreach (Process p in procs) p.Dispose();
                return any;
            }
            catch
            {
                return false;
            }
        }

        public static bool LaunchOrActivate(AppEntry e)
        {
            if (e == null) return false;
            string key = LaunchKey(e);

            if (TryActivate(e))
            {
                ClearPending(key);
                return true;
            }

            lock (PendingGate)
            {
                DateTime at;
                if (PendingLaunches.TryGetValue(key, out at))
                {
                    double age = (DateTime.UtcNow - at).TotalSeconds;
                    if (age < LaunchGraceSeconds)
                    {
                        Logger.Write("[LAUNCH-SKIP] " + e.Name + " 正在启动中（"
                            + Math.Round(age, 1) + "s），不重复打开");
                        return true;
                    }
                    PendingLaunches.Remove(key);
                }
                PendingLaunches[key] = DateTime.UtcNow;
            }

            bool ok = Launch(e);
            if (!ok) ClearPending(key);
            return ok;
        }

        /// <summary>
        /// 退出应用：优先给主窗口发 WM_CLOSE（应用可以弹保存确认）；
        /// 普通应用 1.2s 后如果已经没有可见窗口、进程却还在（托盘/隐藏），再强制结束。
        /// UWP 只发 WM_CLOSE，绝不结束共享的 ApplicationFrameHost。
        /// </summary>
        public static void Quit(AppEntry e)
        {
            if (e == null) return;
            bool uwp = string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase);
            string target = e.ResolvedTarget;
            string targetName = string.IsNullOrEmpty(target) ? null : Util.ExeNameOf(target);
            bool isShell = string.Equals(targetName, "explorer", StringComparison.OrdinalIgnoreCase);
            string targetAumid = TargetAumid(e);

            HashSet<int> pids = new HashSet<int>();
            if (!uwp && !string.IsNullOrEmpty(targetName))
            {
                try
                {
                    foreach (Process p in Process.GetProcessesByName(targetName))
                    {
                        try { pids.Add(p.Id); } catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch (Exception ex) { Logger.Error("LaunchService.Quit/pid", ex); }
            }

            List<IntPtr> windows = new List<IntPtr>();
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    uint pid;
                    Native.GetWindowThreadProcessId(h, out pid);
                    if (uwp)
                    {
                        if (string.IsNullOrEmpty(targetAumid)) return true;
                        IntPtr hp = Native.OpenProcess(0x1000, false, (int)pid);
                        if (hp == IntPtr.Zero) return true;
                        try
                        {
                            string a = Native.ProcessAumid(hp);
                            if (!string.Equals(a, targetAumid, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                        finally { Native.CloseHandle(hp); }
                    }
                    else if (!pids.Contains((int)pid)) return true;

                    if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                    if (IsToolWindow(h) || IsNoActivate(h)) return true;
                    if (IsTinyWindow(h)) return true;

                    StringBuilder title = new StringBuilder(256);
                    Native.GetWindowText(h, title, 256);
                    if (title.Length == 0) return true;

                    windows.Add(h);
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { Logger.Error("LaunchService.Quit/enum", ex); }

            foreach (IntPtr h in windows)
                Native.PostMessage(h, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);

            Logger.Write("[QUIT] " + e.Name + " windows=" + windows.Count + " pids=" + pids.Count
                + (uwp ? " uwp" : ""));

            if (uwp || pids.Count == 0 || isShell) return;

            // 给应用 1.2s 正常退出；如果已经没有可见窗口但进程还在（最小化到托盘），强制结束
            // 注意：explorer.exe 是 Windows 外壳，绝不 Kill，只关闭它的文件夹窗口
            HashSet<int> captured = new HashSet<int>(pids);
            string name = targetName;
            string appName = e.Name;
            ThreadPool.QueueUserWorkItem(delegate
            {
                try
                {
                    Thread.Sleep(1200);
                    if (HasVisibleAppWindow(captured)) return;
                    foreach (int pid in captured)
                    {
                        try
                        {
                            using (Process p = Process.GetProcessById(pid))
                            {
                                if (!string.Equals(p.ProcessName, name, StringComparison.OrdinalIgnoreCase)) continue;
                                p.Kill();
                                Logger.Write("[QUIT-KILL] " + appName + " pid=" + pid);
                            }
                        }
                        catch { }
                    }
                }
                catch (Exception ex) { Logger.Error("LaunchService.Quit/fallback", ex); }
            });
        }

        /// <summary>强制结束应用（不弹保存确认）。UWP / explorer 仍走温和退出，避免结束共享宿主或 Windows 外壳。</summary>
        public static void ForceQuit(AppEntry e)
        {
            if (e == null) return;

            bool uwp = string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase);
            string target = e.ResolvedTarget;
            string targetName = string.IsNullOrEmpty(target) ? null : Util.ExeNameOf(target);
            if (uwp || string.IsNullOrEmpty(targetName) ||
                string.Equals(targetName, "explorer", StringComparison.OrdinalIgnoreCase))
            {
                Quit(e);
                return;
            }

            int n = 0;
            try
            {
                foreach (Process p in Process.GetProcessesByName(targetName))
                {
                    try
                    {
                        p.Kill();
                        n++;
                    }
                    catch (Exception ex) { Logger.Error("LaunchService.ForceQuit/kill", ex); }
                    finally { p.Dispose(); }
                }
            }
            catch (Exception ex) { Logger.Error("LaunchService.ForceQuit", ex); }

            Logger.Write("[FORCE-QUIT] " + e.Name + " pids=" + n);
        }

        private static bool HasVisibleAppWindow(HashSet<int> pids)        {
            bool visible = false;
            Native.EnumWindows(delegate(IntPtr h, IntPtr l)
            {
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (!pids.Contains((int)pid)) return true;
                if (!Native.IsWindowVisible(h)) return true;
                if (IsToolWindow(h) || IsNoActivate(h)) return true;

                StringBuilder title = new StringBuilder(256);
                Native.GetWindowText(h, title, 256);
                if (title.Length == 0) return true;

                visible = true;
                return false;
            }, IntPtr.Zero);
            return visible;
        }
    }
}
