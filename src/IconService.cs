using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WinLaunch
{
    /// <summary>快捷方式解析结果。</summary>
    public class ShortcutInfo
    {
        public string Target;
        public string Args;
        public string WorkDir;
        public string IconLocation;
    }

    /// <summary>IShellLink 解析：.lnk → 目标 exe / 参数 / 工作目录。</summary>
    public static class ShortcutResolver
    {
        public static ShortcutInfo Resolve(string lnkPath)
        {
            ShortcutInfo info = new ShortcutInfo();
            object comObj = null;
            try
            {
                comObj = new Native.ShellLink();
                Native.IShellLinkW link = (Native.IShellLinkW)comObj;
                Native.IPersistFile file = (Native.IPersistFile)comObj;
                file.Load(lnkPath, 0);

                StringBuilder sb = new StringBuilder(1024);
                link.GetPath(sb, sb.Capacity, IntPtr.Zero, 0);
                info.Target = sb.ToString();

                sb.Length = 0;
                link.GetArguments(sb, sb.Capacity);
                info.Args = sb.ToString();

                sb.Length = 0;
                link.GetWorkingDirectory(sb, sb.Capacity);
                info.WorkDir = sb.ToString();

                sb.Length = 0;
                int iconIndex = 0;
                link.GetIconLocation(sb, sb.Capacity, out iconIndex);
                info.IconLocation = sb.ToString();
            }
            catch (Exception ex)
            {
                Logger.Error("ShortcutResolver.Resolve(" + lnkPath + ")", ex);
            }
            finally
            {
                if (comObj != null && Marshal.IsComObject(comObj)) Marshal.ReleaseComObject(comObj);
            }
            return info;
        }
    }

    /// <summary>
    /// 图标管线：内存 LRU → 磁盘 PNG → IShellItemImageFactory(256) → IImageList(Jumbo)
    /// → SHGetFileInfo(32) → 字母色块占位。
    /// </summary>
    public static class IconService
    {
        private static readonly object Gate = new object();
        private static readonly Dictionary<string, BitmapSource> Mem = new Dictionary<string, BitmapSource>();
        private static readonly Dictionary<string, List<Action<BitmapSource>>> Waiting =
            new Dictionary<string, List<Action<BitmapSource>>>();
        private static readonly List<string> MemOrder = new List<string>();
        private const int MemLimit = 320;
        // 图标提取管线有改动时 +1：启动时自动清掉旧 PNG，避免旧版本留下的错图标一直复用
        private const int CacheVersion = 2;
        private static Dispatcher _ui;

        public static void Init(Dispatcher ui)
        {
            _ui = ui;
            RefreshCacheIfOutdated();
        }

        /// <summary>
        /// 图标缓存没有过期时间，一旦某次提取失败（拿到通用文件图标），会一直错下去。
        /// 用一个版本标记文件控制：版本不一致就清空 PNG 重新提取。
        /// </summary>
        private static void RefreshCacheIfOutdated()
        {
            try
            {
                string dir = ConfigStore.CacheDir;
                string marker = Path.Combine(dir, "icons.version");
                string want = CacheVersion.ToString(CultureInfo.InvariantCulture);
                string have = File.Exists(marker) ? File.ReadAllText(marker).Trim() : "";
                if (have == want) return;

                int n = DeletePngs(dir);
                File.WriteAllText(marker, want);
                Logger.Write("[INFO] 图标缓存版本 " + (have.Length == 0 ? "(无)" : have) + " → " + want + "，已清理 " + n + " 个旧图标");
            }
            catch (Exception ex) { Logger.Error("IconService.RefreshCacheIfOutdated", ex); }
        }

        public static void Clear()
        {
            lock (Gate) { Mem.Clear(); MemOrder.Clear(); }
        }

        /// <summary>清空磁盘 + 内存图标缓存（保留版本标记），托盘「重建图标缓存」用。</summary>
        public static void ClearDiskCache()
        {
            try
            {
                Clear();
                int n = DeletePngs(ConfigStore.CacheDir);
                Logger.Write("[INFO] 已清理图标缓存 " + n + " 个，重新提取");
            }
            catch (Exception ex) { Logger.Error("IconService.ClearDiskCache", ex); }
        }

        private static int DeletePngs(string dir)
        {
            int n = 0;
            try
            {
                foreach (string png in Directory.GetFiles(dir, "*.png"))
                {
                    try { File.Delete(png); n++; } catch { }
                }
            }
            catch (Exception ex) { Logger.Error("IconService.DeletePngs", ex); }
            return n;
        }

        /// <summary>同步取内存缓存；没有则返回 null。</summary>
        public static BitmapSource Peek(AppEntry e, int px)
        {
            string key = KeyOf(e, px);
            lock (Gate)
            {
                BitmapSource bs;
                if (Mem.TryGetValue(key, out bs)) return bs;
            }
            return null;
        }

        /// <summary>请求图标：命中即回调，否则后台加载后回调（回调保证在 UI 线程）。</summary>
        public static void Get(AppEntry e, int px, Action<BitmapSource> done)
        {
            string key = KeyOf(e, px);

            lock (Gate)
            {
                BitmapSource cached;
                if (Mem.TryGetValue(key, out cached))
                {
                    done(cached);
                    return;
                }
                List<Action<BitmapSource>> list;
                if (Waiting.TryGetValue(key, out list))
                {
                    list.Add(done);
                    return;
                }
                Waiting[key] = new List<Action<BitmapSource>> { done };
            }

            // Shell 图标 API 必须在 STA 线程调用（MTA 线程池线程会挂死在 Shell 命名空间里）
            EnqueueJob(delegate
            {
                BitmapSource result = null;
                try
                {
                    result = LoadFromDisk(e, px) ?? LoadFromShell(e, px);
                    if (result == null) result = LoadPlaceholder(e, px);
                    else if (!File.Exists(CachePath(e, px))) SaveToDisk(e, px, result);
                }
                catch (Exception ex)
                {
                    Logger.Error("IconService.Load(" + e.Name + ")", ex);
                }
                Complete(key, result, e, px);
            });
        }

        // ---------------- STA 工作线程池 ----------------

        private static readonly Queue<Action> Jobs = new Queue<Action>();
        private static readonly object JobGate = new object();
        private static int _workers;

        private static void EnqueueJob(Action job)
        {
            lock (JobGate)
            {
                Jobs.Enqueue(job);
                if (_workers < 3)
                {
                    _workers++;
                    Thread t = new Thread(WorkerLoop);
                    t.IsBackground = true;
                    t.Name = "icon-worker";
                    t.SetApartmentState(ApartmentState.STA);
                    t.Start();
                }
            }
        }

        private static void WorkerLoop()
        {
            while (true)
            {
                Action job = null;
                lock (JobGate)
                {
                    if (Jobs.Count > 0) job = Jobs.Dequeue();
                }
                if (job == null)
                {
                    Thread.Sleep(40);
                    continue;
                }
                try { job(); }
                catch (Exception ex) { Logger.Error("IconService.Worker", ex); }
            }
        }

        private static void Complete(string key, BitmapSource bs, AppEntry e, int px)
        {
            List<Action<BitmapSource>> callbacks;
            lock (Gate)
            {
                if (bs != null)
                {
                    Mem[key] = bs;
                    MemOrder.Add(key);
                    while (MemOrder.Count > MemLimit)
                    {
                        Mem.Remove(MemOrder[0]);
                        MemOrder.RemoveAt(0);
                    }
                }
                Waiting.TryGetValue(key, out callbacks);
                Waiting.Remove(key);
            }
            if (callbacks == null) return;
            Action dispatch = delegate
            {
                foreach (Action<BitmapSource> cb in callbacks)
                {
                    try { cb(bs); } catch (Exception ex) { Logger.Error("IconService.Callback", ex); }
                }
            };
            if (_ui != null) _ui.BeginInvoke(DispatcherPriority.Background, dispatch);
            else dispatch();
        }

        private static string KeyOf(AppEntry e, int px)
        {
            return (e.Id ?? Util.Hash(e.Target)) + "@" + px;
        }

        private static string CachePath(AppEntry e, int px)
        {
            return Path.Combine(ConfigStore.CacheDir, Util.SafeFileName(e.Id ?? "x") + "_" + px + ".png");
        }

        private static BitmapSource LoadFromDisk(AppEntry e, int px)
        {
            try
            {
                string p = CachePath(e, px);
                if (!File.Exists(p)) return null;
                BitmapImage bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.CreateOptions = BitmapCreateOptions.PreservePixelFormat;
                bmp.UriSource = new Uri(p);
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }
            catch (Exception ex)
            {
                Logger.Error("IconService.LoadFromDisk", ex);
                return null;
            }
        }

        private static void SaveToDisk(AppEntry e, int px, BitmapSource bs)
        {
            try
            {
                PngBitmapEncoder enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bs));
                using (FileStream fs = new FileStream(CachePath(e, px), FileMode.Create, FileAccess.Write))
                    enc.Save(fs);
            }
            catch (Exception ex)
            {
                Logger.Error("IconService.SaveToDisk", ex);
            }
        }

        // ---------------- shell 提取 ----------------

        private static string ParseNameOf(AppEntry e)
        {
            if (string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase))
                return "shell:AppsFolder\\" + e.Target;
            if (string.Equals(e.Kind, "shell", StringComparison.OrdinalIgnoreCase))
                return e.Target;
            return e.Target;
        }

        private static BitmapSource LoadFromShell(AppEntry e, int px)
        {
            string name = ParseNameOf(e);
            if (string.IsNullOrEmpty(name)) return null;

            BitmapSource bs = TryImageFactory(name, px);
            if (bs != null) return bs;

            bs = TryJumboIcon(name, px);
            if (bs != null) return bs;

            bs = TrySmallIcon(name);
            return bs;
        }

        private static BitmapSource TryImageFactory(string name, int px)
        {
            object item = null;
            try
            {
                Guid iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
                Native.SHCreateItemFromParsingName(name, IntPtr.Zero, ref iid, out item);
                if (item == null) return null;
                Native.IShellItemImageFactory factory = (Native.IShellItemImageFactory)item;
                IntPtr hbm;
                int hr = factory.GetImage(new Native.SIZE(px, px),
                    Native.SIIGBF_ICONONLY | Native.SIIGBF_BIGGERSIZEOK, out hbm);
                if (hr != 0 || hbm == IntPtr.Zero) return null;
                try
                {
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHBitmap(
                        hbm, IntPtr.Zero, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
                finally
                {
                    Native.DeleteObject(hbm);
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                if (item != null && Marshal.IsComObject(item)) Marshal.ReleaseComObject(item);
            }
        }

        private static BitmapSource TryJumboIcon(string name, int px)
        {
            try
            {
                Native.SHFILEINFO sfi = new Native.SHFILEINFO();
                IntPtr r = Native.SHGetFileInfo(name, 0, ref sfi,
                    (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)), Native.SHGFI_SYSICONINDEX);
                if (r == IntPtr.Zero || sfi.iIcon == 0) return null;

                Guid iid = new Guid("46EB5926-582E-4017-9FDF-E8998DAA0950");
                Native.IImageList list;
                int hr = Native.SHGetImageList(Native.SHIL_JUMBO, ref iid, out list);
                if (hr != 0 || list == null) return null;

                IntPtr hIcon;
                hr = list.GetIcon(sfi.iIcon, Native.ILD_TRANSPARENT, out hIcon);
                if (hr != 0 || hIcon == IntPtr.Zero) return null;
                try
                {
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(
                        hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
                finally
                {
                    DestroyIcon(hIcon);
                }
            }
            catch
            {
                return null;
            }
        }

        private static BitmapSource TrySmallIcon(string name)
        {
            try
            {
                Native.SHFILEINFO sfi = new Native.SHFILEINFO();
                IntPtr r = Native.SHGetFileInfo(name, 0, ref sfi,
                    (uint)Marshal.SizeOf(typeof(Native.SHFILEINFO)),
                    Native.SHGFI_ICON | Native.SHGFI_LARGEICON);
                if (r == IntPtr.Zero || sfi.hIcon == IntPtr.Zero) return null;
                try
                {
                    BitmapSource bs = Imaging.CreateBitmapSourceFromHIcon(
                        sfi.hIcon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    bs.Freeze();
                    return bs;
                }
                finally
                {
                    DestroyIcon(sfi.hIcon);
                }
            }
            catch
            {
                return null;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        // ---------------- 字母色块占位 ----------------

        private static BitmapSource LoadPlaceholder(AppEntry e, int px)
        {
            // RenderTargetBitmap 需要 STA/Dispatch 线程，放到 UI 线程同步生成
            if (_ui != null && !_ui.CheckAccess())
            {
                BitmapSource result = null;
                _ui.Invoke(new Action(delegate { result = BuildPlaceholder(e, px); }));
                return result;
            }
            return BuildPlaceholder(e, px);
        }

        private static BitmapSource BuildPlaceholder(AppEntry e, int px)
        {
            int size = Math.Max(48, px);
            DrawingVisual dv = new DrawingVisual();
            using (DrawingContext dc = dv.RenderOpen())
            {
                string hex = Brush2.ColorFor(e);
                Color c1 = Brush2.Hex(hex);
                Color c2 = Color.FromArgb(255,
                    (byte)(c1.R * 0.72), (byte)(c1.G * 0.72), (byte)(c1.B * 0.72));
                LinearGradientBrush bg = new LinearGradientBrush(c1, c2, 90);
                dc.DrawRoundedRectangle(bg, null, new Rect(0, 0, size, size), size * 0.22, size * 0.22);

                string ch = Brush2.Initial(e);
                FormattedText ft = new FormattedText(
                    ch, System.Globalization.CultureInfo.CurrentUICulture,
                    FlowDirection.LeftToRight,
                    new Typeface(new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.Bold, FontStretches.Normal),
                    size * 0.46, Brushes.White, 1.0);
                dc.DrawText(ft, new Point((size - ft.Width) / 2, (size - ft.Height) / 2));
            }
            RenderTargetBitmap rtb = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
            rtb.Render(dv);
            rtb.Freeze();
            return rtb;
        }
    }
}
