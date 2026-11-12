using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Markup;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using Shapes = System.Windows.Shapes;
using System.Windows.Threading;

namespace WinLaunch
{
    /// <summary>程序坞窗口：图标渲染、悬停放大、运行指示、拖拽排序、钉住/移除。</summary>
    public class DockWindow : Window
    {
        // ---------- 视觉令牌 ----------
        private const double IconBase = 44;
        private const double Gap = 8;
        private const double PadX = 10;
        private const double PadBottom = 6;
        private const double DotH = 10;
        private const double BarH = 68;
        private const double ShadowTop = 40;
        private const double ShadowBottom = 8;
        private const double GroupGap = 18;   // 固定区 / 运行中临时区之间的额外间距（竖线居中）
        private const double MaxScale = 1.55;
        private const double RadiusPx = 3 * (IconBase + Gap);
        private const double FixedLeft = PadX + IconBase + Gap + 1 + Gap;
        private const double BottomGap = 8;

        // ---------- UI ----------
        private Grid _root;
        private Border _bar;
        private Canvas _canvas;
        private Canvas _layer;


        private TranslateTransform _slide;
        private Border _launcher;
        private ContextMenu _menu;
        private MenuItem _menuOpen;
        private MenuItem _menuLocate;
        private MenuItem _menuQuit;
        private MenuItem _menuForce;
        private MenuItem _menuPin;
        private MenuItem _menuRemove;
        private Separator _menuSeparator;

        private readonly List<ItemUI> _items = new List<ItemUI>();
        private int _pinnedCount;
        private Shapes.Rectangle _groupSep;
        private readonly int _myPid = Process.GetCurrentProcess().Id;
        private IntPtr _lastExternalForeground;
        private IntPtr _hwndSelf;
        private double _baseBarW;
        private double _cursorX = double.NaN;


        private DispatcherTimer _timer;
        private int _tick;
        private bool _suppressed;
        private HashSet<string> _runningNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private HashSet<string> _runningAumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        private readonly List<AppEntry> _runningExtras = new List<AppEntry>();
        private string _extrasKey = "";
        private int _snapshotBusy;
        private int _lastState;
        private bool _slidAway;

        // ---------- 拖拽 ----------
        private ItemUI _dragSrc;
        private Point _pressPoint;
        private bool _dragging;
        private bool _dragPinned;
        private Image _ghost;
        private int _insertIndex = -1;

        public event Action<int> HotkeyPressed;

        private class ItemUI
        {
            public AppEntry Entry;
            public Grid Root;
            public Image Img;
            public Shapes.Ellipse Dot;
            public ScaleTransform Ts;
            public double Scale = 1.0;
            public double Target = 1.0;
            public double BaseCenter;
            public double ScreenCenter;
            public double Width;
        }

        /// <summary>运行中应用快照（后台线程采集，UI 线程映射成 AppEntry）。</summary>
        private class RunningApp
        {
            public int Pid;
            public string ImagePath;
            public string Aumid;
            public string Title;
        }

        public DockWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SnapsToDevicePixels = true;
            UseLayoutRounding = true;
            ShowActivated = false;
            Title = "WinLaunch Dock";
        }

        // =================================================================
        // 生命周期
        // =================================================================

        protected override void OnSourceInitialized(EventArgs e)
        {
            base.OnSourceInitialized(e);
            HwndSource src = (HwndSource)PresentationSource.FromVisual(this);
            if (src != null) src.AddHook(WndProc);
            _hwndSelf = src != null ? src.Handle : IntPtr.Zero;
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            // 任务栏同款：左键点击 Dock 不要抢焦点。否则第一次点击会先激活 Dock，
            // 当前应用被切到后台，下一次点击又只走“激活”，看起来像第一下没反应。
            // 右键要放行：右键菜单的弹出窗口是 WS_EX_NOACTIVATE，只有 Dock 处于
            // 激活状态时，菜单才能拿到键盘焦点（Esc 关闭）和鼠标捕获（点外面关闭）。
            if (msg == Native.WM_MOUSEACTIVATE)
            {
                int mouseMsg = (int)((lParam.ToInt64() >> 16) & 0xFFFF);
                if (mouseMsg != Native.WM_RBUTTONDOWN && mouseMsg != Native.WM_RBUTTONUP)
                {
                    handled = true;
                    return new IntPtr(Native.MA_NOACTIVATE);
                }
            }

            if (msg == Native.WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (HotkeyPressed != null) HotkeyPressed(id);
                handled = true;
            }
            return IntPtr.Zero;
        }

        protected override void OnClosed(EventArgs e)
        {
            Logger.Write("[INFO] Dock window closed");
            base.OnClosed(e);
        }

        public void Build()
        {
            BuildShell();
            Rebuild();
            Reposition();
            StartTimer();
        }

        private void BuildShell()
        {
            _root = new Grid();
            _root.RenderTransform = _slide = new TranslateTransform(0, 0);
            Content = _root;

            _layer = new Canvas();
            _layer.IsHitTestVisible = false;
            Panel.SetZIndex(_layer, 100);
            _root.Children.Add(_layer);

            _bar = new Border();
            _bar.Width = 200;
            _bar.Height = BarH;
            _bar.CornerRadius = new CornerRadius(22);
            _bar.BorderThickness = new Thickness(1);
            _bar.BorderBrush = Brush2.Solid("#38FFFFFF");
            _bar.Background = Brush2.Vertical("#F22A2E3A", "#F5181B24");
            _bar.HorizontalAlignment = HorizontalAlignment.Center;
            _bar.VerticalAlignment = VerticalAlignment.Top;
            _bar.Margin = new Thickness(0, ShadowTop, 0, 0);

            DropShadowEffect shadow = new DropShadowEffect();
            shadow.BlurRadius = 28;
            shadow.ShadowDepth = 6;
            shadow.Direction = 270;
            shadow.Opacity = 0.55;
            shadow.Color = Colors.Black;
            _bar.Effect = shadow;

            _canvas = new Canvas();
            _canvas.Background = Brushes.Transparent;
            _canvas.Height = BarH;
            _bar.Child = _canvas;
            _root.Children.Add(_bar);



            MouseMove += Dock_MouseMove;
            MouseLeave += Dock_MouseLeave;
            MouseLeftButtonUp += Dock_MouseUp;
            AllowDrop = true;
            DragOver += Dock_DragOver;
            DragLeave += Dock_DragLeave;
            Drop += Dock_Drop;
        }

        private void StartTimer()
        {
            _timer = new DispatcherTimer(DispatcherPriority.Render);
            _timer.Interval = TimeSpan.FromMilliseconds(16);
            _timer.Tick += Timer_Tick;
            _timer.Start();
        }

        // =================================================================
        // 构建 / 布局
        // =================================================================

        public void Rebuild()
        {
            if (_menu != null && _menu.IsOpen) _menu.IsOpen = false;

            _canvas.Children.Clear();
            _items.Clear();
            _pinnedCount = Core.Config.Dock.Items.Count;

            BuildLauncher();

            foreach (AppEntry e in Core.Config.Dock.Items)
                AddVisual(e);

            // macOS 行为：运行中但未固定的应用也显示在 Dock 末尾
            foreach (AppEntry e in _runningExtras)
                AddVisual(e);

            ComputeBaseWidth();
            Reposition();
            Layout();
            RequestIcons();
            ApplyRunningDots();
            RequestRunningSnapshot();
        }

        private void BuildLauncher()
        {
            _launcher = new Border();
            _launcher.Width = IconBase;
            _launcher.Height = IconBase;
            _launcher.CornerRadius = new CornerRadius(10);
            _launcher.Background = Brush2.Solid("#1FFFFFFF");
            _launcher.HorizontalAlignment = HorizontalAlignment.Left;
            _launcher.VerticalAlignment = VerticalAlignment.Top;
            _launcher.Cursor = Cursors.Hand;
            _launcher.ToolTip = "启动台";

            Grid dots = new Grid();
            for (int r = 0; r < 3; r++)
                for (int c = 0; c < 3; c++)
                {
                    Shapes.Ellipse el = new Shapes.Ellipse();
                    el.Width = 5;
                    el.Height = 5;
                    el.Fill = Brush2.Solid("#E2E6EE");
                    el.HorizontalAlignment = HorizontalAlignment.Left;
                    el.VerticalAlignment = VerticalAlignment.Top;
                    el.Margin = new Thickness(11 + c * 8, 11 + r * 8, 0, 0);
                    dots.Children.Add(el);
                }
            _launcher.Child = dots;
            Canvas.SetLeft(_launcher, PadX);
            Canvas.SetTop(_launcher, (BarH - IconBase) / 2 - 1);
            _launcher.MouseLeftButtonUp += delegate { ToggleLaunchpad(); };
            _launcher.MouseEnter += delegate { _launcher.Background = Brush2.Solid("#33FFFFFF"); };
            _launcher.MouseLeave += delegate { _launcher.Background = Brush2.Solid("#1FFFFFFF"); };
            _canvas.Children.Add(_launcher);

            Shapes.Rectangle sep = new Shapes.Rectangle();
            sep.Width = 1;
            sep.Height = 26;
            sep.Fill = Brush2.Solid("#38FFFFFF");
            Canvas.SetLeft(sep, PadX + IconBase + Gap);
            Canvas.SetTop(sep, (BarH - 26) / 2);
            _canvas.Children.Add(sep);

            // 固定应用 / 运行中临时应用之间的竖线（位置在 Layout 里按实际图标边界更新）
            _groupSep = new Shapes.Rectangle();
            _groupSep.Width = 1;
            _groupSep.Height = 28;
            _groupSep.Fill = Brush2.Solid("#4DFFFFFF");
            _groupSep.IsHitTestVisible = false;
            _groupSep.Visibility = Visibility.Collapsed;
            Canvas.SetTop(_groupSep, (BarH - 28) / 2);
            _canvas.Children.Add(_groupSep);
        }

        private bool HasGroup
        {
            get { return _pinnedCount > 0 && _pinnedCount < _items.Count; }
        }

        private void AddVisual(AppEntry e)
        {
            ItemUI it = new ItemUI();
            it.Entry = e;

            Grid g = new Grid();
            g.Width = IconBase;
            g.Height = IconBase + DotH;
            g.Background = Brushes.Transparent;
            g.Cursor = Cursors.Hand;

            it.Img = new Image();
            it.Img.Width = IconBase;
            it.Img.Height = IconBase;
            it.Img.VerticalAlignment = VerticalAlignment.Top;
            it.Img.Stretch = Stretch.Uniform;
            RenderOptions.SetBitmapScalingMode(it.Img, BitmapScalingMode.HighQuality);
            g.Children.Add(it.Img);

            it.Dot = new Shapes.Ellipse();
            it.Dot.Width = 4;
            it.Dot.Height = 4;
            it.Dot.Fill = Brush2.Solid("#E8ECF3");
            it.Dot.HorizontalAlignment = HorizontalAlignment.Center;
            it.Dot.VerticalAlignment = VerticalAlignment.Bottom;
            it.Dot.Margin = new Thickness(0, 0, 0, 2);
            it.Dot.Visibility = Visibility.Collapsed;
            g.Children.Add(it.Dot);

            it.Ts = new ScaleTransform(1, 1);
            g.RenderTransform = it.Ts;
            g.RenderTransformOrigin = new Point(0.5, 1);

            Canvas.SetTop(g, BarH - PadBottom - (IconBase + DotH));

            ItemUI captured = it;
            g.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs ev)
            {
                BeginPress(captured, ev);
            };
            g.MouseRightButtonUp += delegate(object s, MouseButtonEventArgs ev)
            {
                ShowMenu(captured);
                ev.Handled = true;
            };


            it.Root = g;
            _items.Add(it);
            _canvas.Children.Add(g);
        }

        private void ComputeBaseWidth()
        {
            int n = _items.Count;
            _baseBarW = FixedLeft + (n * IconBase) + (n > 0 ? Gap * (n - 1) : 0)
                + (HasGroup ? GroupGap : 0) + PadX;
            _bar.Width = _baseBarW;
            _canvas.Width = _baseBarW;
        }

        /// <summary>重新计算窗口位置与尺寸（工作区底边居中）。</summary>
        public void Reposition()
        {
            Rect wa = SystemParameters.WorkArea;
            double extra = 160; // 放大时向外溢出的余量
            Width = Math.Min(wa.Width, Math.Max(320, _baseBarW + extra));
            Height = ShadowTop + BarH + ShadowBottom;
            Left = wa.Left + (wa.Width - Width) / 2;
            Top = wa.Bottom - Height;

            if (IsVisible)
            {
                // 保证窗口位置更新（无边框窗口直接赋值即可）
                this.Left = Left;
                this.Top = Top;
            }
        }

        private void Layout()
        {
            int n = _items.Count;
            if (n == 0)
            {
                _bar.Width = FixedLeft + PadX;
                _canvas.Width = _bar.Width;
                if (_groupSep != null) _groupSep.Visibility = Visibility.Collapsed;
                return;
            }

            double[] w = new double[n];
            double contentW = 0;
            for (int i = 0; i < n; i++)
            {
                w[i] = IconBase * _items[i].Scale;
                contentW += w[i];
            }
            contentW += Gap * (n - 1);
            if (HasGroup) contentW += GroupGap;

            double barW = FixedLeft + contentW + PadX;
            double corr = (barW - _baseBarW) / 2;

            // 基准中心（不随放大变化），用于计算放大曲线
            double[] baseCenter = new double[n];
            for (int i = 0; i < n; i++)
                baseCenter[i] = FixedLeft + i * (IconBase + Gap)
                    + (HasGroup && i >= _pinnedCount ? GroupGap : 0) + IconBase / 2;

            // 以光标下的图标为锚点：它保持不动，两侧图标向外扩散，
            // 避免"放大后图标从光标下溜走"导致的提示乱跳
            double[] lefts = new double[n];
            int anchor = -1;
            if (!double.IsNaN(_cursorX))
            {
                double best = double.MaxValue;
                for (int i = 0; i < n; i++)
                {
                    double d = Math.Abs(baseCenter[i] - _cursorX);
                    if (d < best) { best = d; anchor = i; }
                }
            }

            if (anchor < 0)
            {
                double x = FixedLeft + corr;
                for (int i = 0; i < n; i++)
                {
                    if (HasGroup && i == _pinnedCount) x += GroupGap;
                    lefts[i] = x + (w[i] - IconBase) / 2;
                    x += w[i] + Gap;
                }
            }
            else
            {
                double anchorCenter = baseCenter[anchor] + corr;
                lefts[anchor] = anchorCenter - w[anchor] / 2;

                for (int i = anchor + 1; i < n; i++)
                    lefts[i] = lefts[i - 1] + w[i - 1] + Gap
                        + (HasGroup && i == _pinnedCount ? GroupGap : 0);

                for (int i = anchor - 1; i >= 0; i--)
                    lefts[i] = lefts[i + 1] - Gap - w[i]
                        - (HasGroup && i + 1 == _pinnedCount ? GroupGap : 0);

                // 不要压到左侧启动台按钮
                if (lefts[0] < FixedLeft)
                {
                    double shift = FixedLeft - lefts[0];
                    for (int i = 0; i < n; i++) lefts[i] += shift;
                }
            }

            for (int i = 0; i < n; i++)
            {
                ItemUI it = _items[i];
                Canvas.SetLeft(it.Root, lefts[i]);
                it.Ts.ScaleX = it.Scale;
                it.Ts.ScaleY = it.Scale;
                it.BaseCenter = baseCenter[i];
            }

            double rightEdge = lefts[n - 1] + w[n - 1] + PadX;
            if (rightEdge > barW) barW = rightEdge;

            _bar.Width = barW;
            _canvas.Width = barW;

            // 固定区 / 临时区之间的竖线
            if (_groupSep != null)
            {
                if (HasGroup)
                {
                    double lastRight = lefts[_pinnedCount - 1] + w[_pinnedCount - 1];
                    Canvas.SetLeft(_groupSep, lastRight + (Gap + GroupGap) / 2 - 0.5);
                    _groupSep.Visibility = Visibility.Visible;
                }
                else
                {
                    _groupSep.Visibility = Visibility.Collapsed;
                }
            }

            // 记录每个图标在窗口坐标里的真实中心，供悬停判定与提示定位使用
            double barLeft = (Width - barW) / 2;
            for (int i = 0; i < n; i++)
            {
                _items[i].ScreenCenter = barLeft + lefts[i] + IconBase / 2;
                _items[i].Width = w[i];
            }
        }

        private void RequestIcons()
        {
            int px = (int)Math.Round(IconBase * MaxScale * Core.DpiScale);
            foreach (ItemUI it in _items)
            {
                ItemUI local = it;
                BitmapSource ready = IconService.Peek(local.Entry, px);
                if (ready != null) local.Img.Source = ready;
                IconService.Get(local.Entry, px, delegate(BitmapSource bs) { });
            }
        }

        // =================================================================
        // 悬停放大
        // =================================================================

        private void Dock_MouseMove(object sender, MouseEventArgs e)
        {
            Point p = e.GetPosition(this);
            double barLeft = (Width - _baseBarW) / 2;
            _cursorX = p.X - barLeft;
            UpdateLastExternalForeground();

            if (_dragSrc != null && _dragPinned)
                DragMove(p);
        }

        private void Dock_MouseLeave(object sender, MouseEventArgs e)
        {
            _cursorX = double.NaN;
        }

        private void Timer_Tick(object sender, EventArgs e)
        {
            _tick++;
            UpdateLastExternalForeground();

            bool magnify = Core.Config.Dock.Magnification;
            for (int i = 0; i < _items.Count; i++)
            {
                ItemUI it = _items[i];
                double target = 1.0;
                if (magnify && !double.IsNaN(_cursorX) && !_suppressed)
                {
                    double d = Math.Abs(it.BaseCenter - _cursorX) / RadiusPx;
                    double t = Math.Max(0.0, 1.0 - d);
                    target = 1.0 + (MaxScale - 1.0) * (3 * t * t - 2 * t * t * t);
                }
                it.Target = target;
                it.Scale += (it.Target - it.Scale) * 0.35;
                if (Math.Abs(it.Target - it.Scale) < 0.002) it.Scale = it.Target;
            }
            Layout();

            if (_tick % 30 == 0)
            {
                RequestRunningSnapshot();
                CheckSuppressed();
            }
            if (_tick % 120 == 0) EnsureTopmost();

            if (_tick % 8 == 0) PullIcons();
            if (Core.Config.Dock.AutoHide) HandleAutoHide();
        }

        // =================================================================
        // 运行状态
        // =================================================================

        /// <summary>从图标缓存拉取就绪的图标（不依赖后台回调）。</summary>
        private void PullIcons()
        {
            int px = (int)Math.Round(IconBase * MaxScale * Core.DpiScale);
            foreach (ItemUI it in _items)
            {
                if (it.Img.Source != null) continue;
                BitmapSource ready = IconService.Peek(it.Entry, px);
                if (ready != null) it.Img.Source = ready;
            }
        }

        /// <summary>
        /// 枚举进程可能在僵死进程上阻塞数秒，绝不能放在 UI 线程上（会拖住点击/热键），
        /// 改为后台线程取快照，UI 只负责套用结果。
        /// </summary>
        private void RequestRunningSnapshot()
        {
            if (Interlocked.CompareExchange(ref _snapshotBusy, 1, 0) != 0) return;

            ThreadPool.QueueUserWorkItem(delegate
            {
                HashSet<string> names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
                {
                    foreach (Process p in Process.GetProcesses())
                    {
                        try { names.Add(p.ProcessName); } catch { }
                        finally { p.Dispose(); }
                    }
                }
                catch (Exception ex) { Logger.Error("Dock.RequestRunningSnapshot", ex); }

                // UWP 应用没有 exe，只能靠窗口的 AUMID 判断是否在运行；
                // 同时收集“有可见主窗口、未固定”的应用，供 Dock 动态显示（macOS 行为）。
                HashSet<string> aumids = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<RunningApp> apps = new List<RunningApp>();
                HashSet<string> appKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                int myPid = Process.GetCurrentProcess().Id;
                try
                {
                    Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                    {
                        uint pid;
                        Native.GetWindowThreadProcessId(h, out pid);

                        IntPtr hp = Native.OpenProcess(0x1000, false, (int)pid);
                        string a = null;
                        if (hp != IntPtr.Zero)
                        {
                            try { a = Native.ProcessAumid(hp); }
                            finally { Native.CloseHandle(hp); }
                        }
                        if (!string.IsNullOrEmpty(a)) aumids.Add(a);

                        // ---- 运行中应用：只取有可见主窗口的 ----
                        if (!Native.IsWindowVisible(h)) return true;
                        if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                        if ((int)pid == myPid) return true;

                        int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                        if ((ex & Native.WS_EX_TOOLWINDOW) != 0) return true;
                        if ((ex & Native.WS_EX_NOACTIVATE) != 0) return true;

                        StringBuilder title = new StringBuilder(256);
                        Native.GetWindowText(h, title, 256);
                        if (title.Length == 0) return true;

                        StringBuilder cls = new StringBuilder(256);
                        Native.GetClassName(h, cls, 256);
                        string cn = cls.ToString();
                        if (IsSystemWindowClass(cn)) return true;

                        Native.RECT wr;
                        if (!Native.GetWindowRect(h, out wr)) return true;
                        if (wr.Right - wr.Left < 8 || wr.Bottom - wr.Top < 8) return true;

                        string path = LaunchService.ProcessImagePath((int)pid);
                        if (string.IsNullOrEmpty(path)) return true;

                        bool isUwp = string.Equals(cn, "ApplicationFrameWindow", StringComparison.OrdinalIgnoreCase)
                            || path.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase);
                        if (isUwp && string.IsNullOrEmpty(a)) return true;

                        string key = isUwp ? ("uwp:" + a.ToLowerInvariant()) : path.ToLowerInvariant();
                        if (!appKeys.Add(key)) return true;

                        RunningApp app = new RunningApp();
                        app.Pid = (int)pid;
                        app.ImagePath = isUwp ? null : path;
                        app.Aumid = isUwp ? a : null;
                        app.Title = title.ToString();
                        apps.Add(app);
                        return true;
                    }, IntPtr.Zero);
                }
                catch (Exception ex) { Logger.Error("Dock.RequestRunningSnapshot/aumid", ex); }

                if (Core.UI == null) { _snapshotBusy = 0; return; }
                Core.UI.BeginInvoke(new Action(delegate
                {
                    _runningNames = names;
                    _runningAumids = aumids;
                    _snapshotBusy = 0;
                    UpdateRunningExtras(apps);
                    ApplyRunningDots();
                }));
            });
        }

        private void ApplyRunningDots()
        {
            if (!Core.Config.Dock.ShowRunningIndicator) return;
            foreach (ItemUI it in _items)
            {
                bool run;
                if (string.Equals(it.Entry.Kind, "uwp", StringComparison.OrdinalIgnoreCase))
                {
                    run = !string.IsNullOrEmpty(it.Entry.Target) && _runningAumids.Contains(it.Entry.Target);
                }
                else
                {
                    string exe = Util.ExeNameOf(it.Entry.ResolvedTarget);
                    run = !string.IsNullOrEmpty(exe) && _runningNames.Contains(exe);
                }
                it.Entry.IsRunning = run;
                it.Dot.Visibility = run ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        /// <summary>把运行中应用映射成 Dock 条目；集合变化时才重建，避免每 0.5s 闪动。</summary>
        private void UpdateRunningExtras(List<RunningApp> apps)
        {
            List<AppEntry> desired = new List<AppEntry>();
            HashSet<string> used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (AppEntry pinned in Core.Config.Dock.Items)
                used.Add(EntryKey(pinned));

            foreach (RunningApp app in apps)
            {
                AppEntry entry = MapRunningApp(app);
                if (entry == null) continue;
                string key = EntryKey(entry);
                if (string.IsNullOrEmpty(key) || !used.Add(key)) continue;
                entry.IsRunning = true;
                desired.Add(entry);
            }

            desired.Sort(delegate(AppEntry a, AppEntry b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });

            StringBuilder kb = new StringBuilder();
            foreach (AppEntry d in desired) kb.Append(EntryKey(d)).Append('\n');
            string keyAll = kb.ToString();
            if (keyAll == _extrasKey) return;

            StringBuilder names = new StringBuilder();
            foreach (AppEntry d in desired) names.Append(d.Name).Append("  ");
            Logger.Write("[DOCK-EXTRA] " + desired.Count + " 个运行中应用: " + names.ToString().Trim());

            _runningExtras.Clear();
            _runningExtras.AddRange(desired);

            // 拖拽中不重建，等松手后的下一次快照
            if (_dragging || _dragSrc != null) return;

            _extrasKey = keyAll;
            Rebuild();
        }

        private AppEntry MapRunningApp(RunningApp app)
        {
            // 优先复用应用清单里的条目（正式名称/图标）
            if (!string.IsNullOrEmpty(app.Aumid))
            {
                foreach (AppEntry c in Core.Catalog)
                {
                    if (string.Equals(c.Kind, "uwp", StringComparison.OrdinalIgnoreCase) &&
                        string.Equals(c.Target, app.Aumid, StringComparison.OrdinalIgnoreCase))
                        return c;
                }

                AppEntry u = new AppEntry();
                u.Id = Util.Hash("uwp:" + app.Aumid);
                u.Name = string.IsNullOrEmpty(app.Title) ? "UWP 应用" : app.Title;
                u.Kind = "uwp";
                u.Target = app.Aumid;
                return u;
            }

            foreach (AppEntry c in Core.Catalog)
            {
                if (!string.IsNullOrEmpty(c.ResolvedTarget) &&
                    Util.SamePath(c.ResolvedTarget, app.ImagePath))
                    return c;
            }

            AppEntry e = new AppEntry();
            e.Id = Util.Hash(app.ImagePath);
            string exe = Path.GetFileNameWithoutExtension(app.ImagePath);
            e.Name = string.Equals(exe, "explorer", StringComparison.OrdinalIgnoreCase) ? "文件资源管理器" : exe;
            e.Kind = "exe";
            e.Target = app.ImagePath;
            e.ResolvedTarget = app.ImagePath;
            return e;
        }

        private static string EntryKey(AppEntry e)
        {
            if (e == null) return "";
            if (string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase))
                return "uwp:" + ((e.Target ?? "").ToLowerInvariant());
            if (!string.IsNullOrEmpty(e.ResolvedTarget)) return e.ResolvedTarget.ToLowerInvariant();
            if (!string.IsNullOrEmpty(e.Target)) return e.Target.ToLowerInvariant();
            return (e.Id ?? e.Name ?? "").ToLowerInvariant();
        }

        private static bool IsPinned(AppEntry e)
        {
            return e != null && Core.Config.Dock.Items.IndexOf(e) >= 0;
        }

        private void PinEntry(AppEntry e)
        {
            if (e == null || IsPinned(e)) return;
            Core.Config.Dock.Items.Add(e);
            _runningExtras.RemoveAll(delegate(AppEntry x) { return EntryKey(x) == EntryKey(e); });
            ConfigStore.Save();
            Rebuild();
        }

        /// <summary>桌面/任务栏/开始菜单/Search 等系统窗口不当作“打开的应用”。</summary>
        private static bool IsSystemWindowClass(string cn)
        {
            if (string.IsNullOrEmpty(cn)) return true;
            if (cn == "Shell_TrayWnd" || cn == "Shell_SecondaryTrayWnd"
                || cn == "Progman" || cn == "WorkerW"
                || cn == "Windows.UI.Core.CoreWindow"
                || cn == "ApplicationManager_ImmersiveShellWindow"
                || cn == "MultitaskingViewFrame"
                || cn == "ForegroundStaging"
                || cn == "TaskListThumbnailWnd"
                || cn == "Windows.Internal.Shell.TabProxyWindow")
                return true;
            if (cn.StartsWith("XamlExplorerHostIslandWindow", StringComparison.OrdinalIgnoreCase)
                || cn.StartsWith("TopLevelWindowForOverflowXamlIsland", StringComparison.OrdinalIgnoreCase))
                return true;
            return false;
        }

        // =================================================================
        // 点击 / 右键菜单
        // =================================================================

        private void UpdateLastExternalForeground()
        {
            try
            {
                IntPtr fg = Native.GetForegroundWindow();
                if (fg == IntPtr.Zero) return;
                uint pid;
                Native.GetWindowThreadProcessId(fg, out pid);
                if (pid != (uint)_myPid) _lastExternalForeground = fg;
            }
            catch { }
        }

        /// <summary>菜单关闭 / 最小化后，如果焦点还落在 Dock 上，交还给原来的应用。</summary>
        private void ReturnFocusIfSelf()
        {
            try
            {
                IntPtr self = _hwndSelf;
                if (self == IntPtr.Zero || Native.GetForegroundWindow() != self) return;

                IntPtr last = _lastExternalForeground;
                if (last != IntPtr.Zero && last != self && Native.IsWindow(last)
                    && Native.IsWindowVisible(last) && !Native.IsIconic(last)
                    && Native.GetWindow(last, Native.GW_OWNER) == IntPtr.Zero)
                {
                    Native.SetForegroundWindow(last);
                    if (Native.GetForegroundWindow() == last) return;
                }

                // 原来那个应用已经不在了（例如刚从菜单里退出），按 Z 序找下一个可切换窗口
                IntPtr h = Native.GetWindow(self, Native.GW_HWNDNEXT);
                for (int guard = 0; h != IntPtr.Zero && guard < 300; guard++)
                {
                    if (IsSwitchableWindow(h))
                    {
                        Native.SetForegroundWindow(h);
                        return;
                    }
                    h = Native.GetWindow(h, Native.GW_HWNDNEXT);
                }
            }
            catch (Exception ex) { Logger.Error("Dock.ReturnFocusIfSelf", ex); }
        }

        private bool IsSwitchableWindow(IntPtr h)
        {
            try
            {
                if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h)) return false;
                if (Native.IsCloaked(h)) return false;
                uint pid;
                Native.GetWindowThreadProcessId(h, out pid);
                if (pid == (uint)_myPid) return false;
                if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return false;
                int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & Native.WS_EX_NOACTIVATE) != 0) return false;
                StringBuilder title = new StringBuilder(256);
                Native.GetWindowText(h, title, 256);
                if (title.Length == 0) return false;
                StringBuilder cls = new StringBuilder(256);
                Native.GetClassName(h, cls, 256);
                if (IsSystemWindowClass(cls.ToString())) return false;
                Native.RECT r;
                if (!Native.GetWindowRect(h, out r)) return false;
                return r.Right - r.Left >= 8 && r.Bottom - r.Top >= 8;
            }
            catch { return false; }
        }

        private void BeginPress(ItemUI it, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;
            UpdateLastExternalForeground();
            _dragSrc = it;
            _dragPinned = IsPinned(it.Entry);
            _pressPoint = e.GetPosition(this);
            _dragging = false;
            CaptureMouse();
            e.Handled = false;
        }

        private void Dock_MouseUp(object sender, MouseButtonEventArgs e)
        {
            if (_dragSrc == null) return;
            ReleaseMouseCapture();

            if (_dragging)
            {
                FinishDrag(e.GetPosition(this));
            }
            else
            {
                ItemUI it = _dragSrc;
                _dragSrc = null;
                _dragPinned = false;
                HideLaunchpadIfOpen();

                // Windows 任务栏行为：图标对应的窗口已经在最上层 → 最小化；
                // 否则启动 / 切到前台（被别的应用盖住时先弹到最上层）。
                if (ShouldMinimizeOnClick(it.Entry))
                {
                    MinimizeEntry(it.Entry);
                    // 最小化后 Windows 可能把前台交给最近激活过的 Dock，
                    // 导致键盘输入落空；把焦点还回下一个应用。
                    ReturnFocusIfSelf();
                }
                else
                    LaunchService.LaunchOrActivate(it.Entry);

                if (Core.Config.Dock.AutoHide) HideDockTemporarily();
            }
        }

        /// <summary>
        /// 点击 Dock 图标时是否应该最小化：该应用已经在所有普通窗口的最上层
        /// （任务栏行为）。桌面/任务栏被点过不算“应用在前台”，此时应用仍然
        /// 在视觉最上层，所以第一下就应该最小化。
        /// 有确认/保存对话框时不移最小化，避免打断用户。
        /// </summary>
        private bool ShouldMinimizeOnClick(AppEntry e)
        {
            if (e == null) return false;
            IntPtr top = TopAppWindow();
            if (top == IntPtr.Zero) return false;
            if (Native.GetWindow(top, Native.GW_OWNER) != IntPtr.Zero) return false;   // 保存/确认对话框
            return WindowBelongsTo(e, top);
        }

        /// <summary>Z 序最上面的普通应用窗口（跳过 Dock 自己、桌面/任务栏等系统窗口）。</summary>
        private IntPtr TopAppWindow()
        {
            IntPtr top = IntPtr.Zero;
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!IsSwitchableWindow(h)) return true;
                    top = h;
                    return false;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { Logger.Error("Dock.TopAppWindow", ex); }
            return top;
        }

        /// <summary>窗口是否属于该条目（按 exe 路径 / 进程名 / UWP AUMID 匹配）。</summary>
        private bool WindowBelongsTo(AppEntry e, IntPtr h)
        {
            if (e == null || h == IntPtr.Zero) return false;

            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == 0 || pid == (uint)_myPid) return false;

            if (string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase))
            {
                if (string.IsNullOrEmpty(e.Target)) return false;
                IntPtr hp = Native.OpenProcess(0x1000, false, (int)pid);
                if (hp == IntPtr.Zero) return false;
                try
                {
                    string a = Native.ProcessAumid(hp);
                    return !string.IsNullOrEmpty(a) &&
                        string.Equals(a, e.Target, StringComparison.OrdinalIgnoreCase);
                }
                finally { Native.CloseHandle(hp); }
            }

            string target = e.ResolvedTarget;
            if (string.IsNullOrEmpty(target)) return false;
            string path = LaunchService.ProcessImagePath((int)pid);
            if (!string.IsNullOrEmpty(path) && Util.SamePath(path, target)) return true;

            string exeName = Util.ExeNameOf(target);
            try
            {
                using (Process p = Process.GetProcessById((int)pid))
                    return string.Equals(p.ProcessName, exeName, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        /// <summary>最小化该应用的所有主窗口（多窗口应用整组最小化）。</summary>
        private void MinimizeEntry(AppEntry e)
        {
            if (e == null) return;

            bool uwp = string.Equals(e.Kind, "uwp", StringComparison.OrdinalIgnoreCase);
            string target = e.ResolvedTarget;
            string targetName = string.IsNullOrEmpty(target) ? null : Util.ExeNameOf(target);
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
                catch { }
            }

            int count = 0;
            try
            {
                Native.EnumWindows(delegate(IntPtr h, IntPtr l)
                {
                    if (!Native.IsWindowVisible(h)) return true;
                    uint pid;
                    Native.GetWindowThreadProcessId(h, out pid);

                    if (uwp)
                    {
                        if (string.IsNullOrEmpty(e.Target)) return true;
                        IntPtr hp = Native.OpenProcess(0x1000, false, (int)pid);
                        if (hp == IntPtr.Zero) return true;
                        try
                        {
                            string a = Native.ProcessAumid(hp);
                            if (!string.Equals(a, e.Target, StringComparison.OrdinalIgnoreCase)) return true;
                        }
                        finally { Native.CloseHandle(hp); }
                    }
                    else if (!pids.Contains((int)pid)) return true;

                    if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero) return true;
                    int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
                    if ((ex & Native.WS_EX_TOOLWINDOW) != 0 || (ex & Native.WS_EX_NOACTIVATE) != 0) return true;

                    Native.RECT r;
                    if (!Native.GetWindowRect(h, out r)) return true;
                    if (r.Right - r.Left < 8 || r.Bottom - r.Top < 8) return true;

                    StringBuilder title = new StringBuilder(256);
                    Native.GetWindowText(h, title, 256);
                    if (title.Length == 0) return true;

                    Native.ShowWindow(h, Native.SW_MINIMIZE);
                    count++;
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex) { Logger.Error("Dock.MinimizeEntry", ex); }

            Logger.Write("[MINIMIZE] " + e.Name + " windows=" + count);
            if (count == 0) LaunchService.LaunchOrActivate(e);
        }

        private AppEntry _menuEntry;

        private void ShowMenu(ItemUI it)
        {
            _menuEntry = it.Entry;

            // 复用同一个 ContextMenu：每次右键新建会积攒 Popup/HwndSource 句柄
            if (_menu == null) BuildMenu();
            if (_menu.IsOpen) _menu.IsOpen = false;

            bool pinned = IsPinned(it.Entry);
            bool running = it.Entry.IsRunning;
            string p = it.Entry.ResolvedTarget ?? it.Entry.Target;
            bool hasPath = !string.IsNullOrEmpty(p) && (File.Exists(p) || Directory.Exists(p));

            _menuOpen.Visibility = Visibility.Visible;
            _menuLocate.Visibility = hasPath ? Visibility.Visible : Visibility.Collapsed;
            _menuQuit.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _menuForce.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            _menuPin.Visibility = (!pinned && running) ? Visibility.Visible : Visibility.Collapsed;
            _menuRemove.Visibility = pinned ? Visibility.Visible : Visibility.Collapsed;
            _menuSeparator.Visibility =
                (_menuPin.Visibility == Visibility.Visible || _menuRemove.Visibility == Visibility.Visible)
                ? Visibility.Visible : Visibility.Collapsed;

            // 贴在图标上方弹出，而不是跟着鼠标点压在 Dock 上
            _menu.PlacementTarget = it.Root;
            _menu.Placement = PlacementMode.Top;
            _menu.VerticalOffset = -2;
            _menu.IsOpen = true;
        }

        private void BuildMenu()
        {
            ContextMenu menu = new ContextMenu();

            MenuItem open = new MenuItem();
            open.Header = "打开";
            open.Tag = "\uE8E5";              // Segoe MDL2: OpenFile
            open.Click += delegate
            {
                if (_menuEntry != null) LaunchService.LaunchOrActivate(_menuEntry);
            };
            menu.Items.Add(open);
            _menuOpen = open;

            MenuItem locate = new MenuItem();
            locate.Header = "在资源管理器中显示";
            locate.Tag = "\uE838";            // FolderOpen
            locate.Click += delegate
            {
                if (_menuEntry != null) ShowInExplorer(_menuEntry);
            };
            menu.Items.Add(locate);
            _menuLocate = locate;

            MenuItem quit = new MenuItem();
            quit.Header = "退出应用";
            quit.Tag = "\uE7E8";              // PowerButton
            quit.Click += delegate
            {
                if (_menuEntry != null)
                {
                    Logger.Write("[MENU] quit " + _menuEntry.Name);
                    LaunchService.Quit(_menuEntry);
                }
            };
            menu.Items.Add(quit);
            _menuQuit = quit;

            MenuItem force = new MenuItem();
            force.Header = "强制退出";
            force.Tag = "\uE711";             // Cancel
            force.Foreground = Brush2.Solid("#FF8A8A");
            force.Click += delegate
            {
                if (_menuEntry != null)
                {
                    Logger.Write("[MENU] force-quit " + _menuEntry.Name);
                    LaunchService.ForceQuit(_menuEntry);
                }
            };
            menu.Items.Add(force);
            _menuForce = force;

            Separator sep = new Separator();
            menu.Items.Add(sep);
            _menuSeparator = sep;

            MenuItem pin = new MenuItem();
            pin.Header = "固定在 Dock";
            pin.Tag = "\uE718";               // Pin
            pin.Click += delegate
            {
                if (_menuEntry != null) PinEntry(_menuEntry);
            };
            menu.Items.Add(pin);
            _menuPin = pin;

            MenuItem remove = new MenuItem();
            remove.Header = "从 Dock 移除";
            remove.Tag = "\uE74D";            // Delete
            remove.Foreground = Brush2.Solid("#FF8A8A");
            remove.Click += delegate
            {
                if (_menuEntry != null) RemoveEntry(_menuEntry);
            };
            menu.Items.Add(remove);
            _menuRemove = remove;

            try
            {
                menu.Style = MenuStyle;
                open.Style = MenuItemStyle;
                locate.Style = MenuItemStyle;
                quit.Style = MenuItemStyle;
                force.Style = MenuItemStyle;
                pin.Style = MenuItemStyle;
                remove.Style = MenuItemStyle;
                sep.Style = MenuSeparatorStyle;
            }
            catch (Exception ex)
            {
                Logger.Error("Dock.MenuStyle", ex);
                StyleMenuFallback(menu);
            }

            // 菜单关闭后（Esc、点外面、选中某一项）把焦点还给原来的应用，
            // 否则键盘输入会落在看不见的 Dock 窗口上。
            menu.Closed += delegate { ReturnFocusIfSelf(); };

            _menu = menu;
        }

        // ---------- 右键菜单视觉：深色圆角 + 图标 + 悬停胶囊 + 投影 ----------

        private static Style _menuStyle;
        private static Style _menuItemStyle;
        private static Style _menuSeparatorStyle;

        private static Style MenuStyle
        {
            get
            {
                if (_menuStyle == null)
                    _menuStyle = (Style)XamlReader.Parse(MenuStyleXaml);
                return _menuStyle;
            }
        }

        private static Style MenuItemStyle
        {
            get
            {
                if (_menuItemStyle == null)
                    _menuItemStyle = (Style)XamlReader.Parse(MenuItemStyleXaml);
                return _menuItemStyle;
            }
        }

        private static Style MenuSeparatorStyle
        {
            get
            {
                if (_menuSeparatorStyle == null)
                    _menuSeparatorStyle = (Style)XamlReader.Parse(MenuSeparatorStyleXaml);
                return _menuSeparatorStyle;
            }
        }

        private const string MenuStyleXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type ContextMenu}'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='SnapsToDevicePixels' Value='True'/>
  <Setter Property='HasDropShadow' Value='True'/>
  <Setter Property='MinWidth' Value='204'/>
  <Setter Property='FontFamily' Value='Microsoft YaHei UI'/>
  <Setter Property='FontSize' Value='13'/>
  <Setter Property='Foreground' Value='#EDEFF4'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type ContextMenu}'>
        <Border x:Name='MenuBorder' Background='#F5181B22' BorderBrush='#2EFFFFFF'
                BorderThickness='1' CornerRadius='12' Padding='5' Margin='8'>
          <Border.Effect>
            <DropShadowEffect BlurRadius='22' ShadowDepth='6' Direction='270'
                              Opacity='0.55' Color='#000000'/>
          </Border.Effect>
          <StackPanel IsItemsHost='True' KeyboardNavigation.DirectionalNavigation='Cycle'/>
        </Border>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        private const string MenuItemStyleXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type MenuItem}'>
  <Setter Property='OverridesDefaultStyle' Value='True'/>
  <Setter Property='Foreground' Value='#EDEFF4'/>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type MenuItem}'>
        <Border x:Name='Bd' CornerRadius='8' Background='Transparent'
                Padding='10,7' Margin='2,1'>
          <Grid>
            <Grid.ColumnDefinitions>
              <ColumnDefinition Width='22'/>
              <ColumnDefinition Width='*'/>
            </Grid.ColumnDefinitions>
            <TextBlock Grid.Column='0' Text='{TemplateBinding Tag}'
                       FontFamily='Segoe MDL2 Assets' FontSize='13'
                       Foreground='{TemplateBinding Foreground}' Opacity='0.72'
                       VerticalAlignment='Center'/>
            <ContentPresenter Grid.Column='1' ContentSource='Header'
                              RecognizesAccessKey='True' VerticalAlignment='Center'/>
          </Grid>
        </Border>
        <ControlTemplate.Triggers>
          <Trigger Property='IsHighlighted' Value='True'>
            <Setter TargetName='Bd' Property='Background' Value='#24FFFFFF'/>
          </Trigger>
          <Trigger Property='IsEnabled' Value='False'>
            <Setter Property='Opacity' Value='0.4'/>
          </Trigger>
        </ControlTemplate.Triggers>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        private const string MenuSeparatorStyleXaml = @"
<Style xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation'
       xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'
       TargetType='{x:Type Separator}'>
  <Setter Property='Template'>
    <Setter.Value>
      <ControlTemplate TargetType='{x:Type Separator}'>
        <Border Height='1' Margin='10,5' Background='#22FFFFFF'/>
      </ControlTemplate>
    </Setter.Value>
  </Setter>
</Style>";

        /// <summary>模板解析失败时的兜底（至少保持深色）。</summary>
        private static void StyleMenuFallback(ContextMenu menu)
        {
            menu.Background = Brush2.Solid("#F51A1D24");
            menu.Foreground = Brush2.Solid("#EDEFF4");
            menu.BorderBrush = Brush2.Solid("#3CFFFFFF");
            menu.BorderThickness = new Thickness(1);
            menu.HasDropShadow = true;
        }

        private static void ShowInExplorer(AppEntry e)
        {
            try
            {
                string path = e.ResolvedTarget ?? e.Target;
                if (File.Exists(path))
                {
                    Process.Start("explorer.exe", "/select,\"" + path + "\"");
                }
                else if (Directory.Exists(path))
                {
                    Process.Start("explorer.exe", "\"" + path + "\"");
                }
            }
            catch (Exception ex) { Logger.Error("Dock.ShowInExplorer", ex); }
        }

        // =================================================================
        // 拖拽：排序 / 拖出移除 / 外部拖入
        // =================================================================

        private void BeginDrag(Point p)
        {
            if (_dragSrc == null) return;
            _dragging = true;
            _dragSrc.Root.Opacity = 0.35;

            if (_ghost == null)
            {
                _ghost = new Image();
                _ghost.Width = IconBase * 1.12;
                _ghost.Height = IconBase * 1.12;
                _ghost.Stretch = Stretch.Uniform;
                _ghost.Opacity = 0.92;
                DropShadowEffect sh = new DropShadowEffect();
                sh.BlurRadius = 18;
                sh.ShadowDepth = 4;
                sh.Direction = 270;
                sh.Opacity = 0.6;
                _ghost.Effect = sh;
                _layer.Children.Add(_ghost);
            }
            _ghost.Source = _dragSrc.Img.Source;
            _ghost.Visibility = Visibility.Visible;
            MoveGhost(p);

        }

        private void MoveGhost(Point p)
        {
            if (_ghost == null) return;
            Canvas.SetLeft(_ghost, p.X - _ghost.Width / 2);
            Canvas.SetTop(_ghost, p.Y - _ghost.Height / 2);
        }

        private void DragMove(Point p)
        {
            if (!_dragging)
            {
                if (Math.Abs(p.X - _pressPoint.X) + Math.Abs(p.Y - _pressPoint.Y) < 6) return;
                BeginDrag(p);
            }
            MoveGhost(p);

            Point inCanvas = new Point(p.X - (Width - _baseBarW) / 2, p.Y);
            int idx = IndexForX(inCanvas.X);
            if (idx != _insertIndex)
            {
                _insertIndex = idx;
            }
        }

        private int IndexForX(double x)
        {
            int n = _items.Count;
            if (n == 0) return 0;
            double baseLeft = (Width - _baseBarW) / 2 + FixedLeft;
            double rel = x - baseLeft;
            int idx = (int)Math.Floor(rel / (IconBase + Gap));
            if (idx < 0) idx = 0;
            // 运行中的临时图标不参与拖拽排序/插入；最多插到固定区末尾
            int maxIdx = _pinnedCount > 0 ? _pinnedCount - 1 : 0;
            if (idx > maxIdx) idx = maxIdx;
            return idx;
        }

        private void FinishDrag(Point p)
        {
            _dragging = false;
            _dragPinned = false;
            _ghost.Visibility = Visibility.Collapsed;
            if (_dragSrc == null) return;

            ItemUI src = _dragSrc;
            src.Root.Opacity = 1.0;
            _dragSrc = null;

            // 运行中的临时图标（不在配置里）不参与排序/移除
            int from = Core.Config.Dock.Items.IndexOf(src.Entry);
            if (from < 0)
            {
                _insertIndex = -1;
                return;
            }

            // 向上拖出 → 移除
            if (p.Y < ShadowTop - 80)
            {
                RemoveEntry(src.Entry);
                return;
            }

            int to = _insertIndex;
            if (to >= 0 && to != from)
            {
                if (to > from) to--;
                if (to < 0) to = 0;
                Core.Config.Dock.Items.RemoveAt(from);
                Core.Config.Dock.Items.Insert(Math.Min(to, Core.Config.Dock.Items.Count), src.Entry);
                ConfigStore.Save();
                Rebuild();
            }
            _insertIndex = -1;
        }

        private void Dock_DragOver(object sender, DragEventArgs e)
        {
            if (!e.Data.GetDataPresent(DataFormats.FileDrop)) { e.Effects = DragDropEffects.None; return; }
            Point p = e.GetPosition(this);
            SetDropHighlight(true);
            e.Effects = DragDropEffects.Copy;
            e.Handled = true;
        }

        private void Dock_DragLeave(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
        }

        private void Dock_Drop(object sender, DragEventArgs e)
        {
            SetDropHighlight(false);
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null) return;
            Point p = e.GetPosition(this);
            int idx = IndexForX(p.X - (Width - _baseBarW) / 2) + 1;
            foreach (string f in files)
            {
                AppEntry entry = EntryFromFile(f);
                if (entry != null) InsertEntry(entry, idx++);
            }
        }

        public void SetDropHighlight(bool on)
        {
            _bar.BorderBrush = on ? Brush2.Solid("#8AB4C7FF") : Brush2.Solid("#38FFFFFF");
            _bar.BorderThickness = new Thickness(on ? 2 : 1);
        }

        // =================================================================
        // 增删改
        // =================================================================

        public static AppEntry EntryFromFile(string file)
        {
            try
            {
                if (!File.Exists(file) && !Directory.Exists(file)) return null;
                AppEntry e = new AppEntry();
                e.Name = Path.GetFileNameWithoutExtension(file);
                if (Directory.Exists(file)) e.Name = Path.GetFileName(file);

                string ext = Path.GetExtension(file).ToLowerInvariant();
                if (ext == ".lnk")
                {
                    ShortcutInfo info = ShortcutResolver.Resolve(file);
                    e.Kind = "shortcut";
                    e.Target = file;
                    e.ResolvedTarget = string.IsNullOrEmpty(info.Target) ? null : info.Target;
                    e.Args = info.Args;
                    e.WorkDir = info.WorkDir;
                    // 与 AppCatalog 保持一致：快捷方式按解析后的目标算 Id，
                    // 这样从资源管理器拖进来的应用也能正确计入“常用”
                    e.Id = Util.Hash(string.IsNullOrEmpty(info.Target) ? file : info.Target);
                }
                else
                {
                    e.Id = Util.Hash(file);
                    if (ext == ".exe")
                    {
                        e.Kind = "exe";
                        e.Target = file;
                        e.ResolvedTarget = file;
                    }
                    else if (ext == ".url")
                    {
                        e.Kind = "url";
                        e.Target = file;
                    }
                    else
                    {
                        e.Kind = "folder";
                        e.Target = file;
                    }
                }
                return e;
            }
            catch (Exception ex)
            {
                Logger.Error("Dock.EntryFromFile", ex);
                return null;
            }
        }

        public void InsertEntry(AppEntry e, int index)
        {
            foreach (AppEntry exist in Core.Config.Dock.Items)
                if (exist.Id == e.Id || Util.SamePath(exist.Target, e.Target)) return;

            if (index < 0 || index > Core.Config.Dock.Items.Count) index = Core.Config.Dock.Items.Count;
            Core.Config.Dock.Items.Insert(index, e);
            ConfigStore.Save();
            Rebuild();
        }

        private void RemoveEntry(AppEntry e)
        {
            Core.Config.Dock.Items.RemoveAll(delegate(AppEntry x) { return x.Id == e.Id; });
            ConfigStore.Save();
            Rebuild();
        }

        // =================================================================
        // 显隐 / 自动隐藏 / 全屏抑制
        // =================================================================

        public void ToggleLaunchpad()
        {
            if (Core.Launchpad == null) Core.Launchpad = new LaunchpadWindow();
            if (Core.Launchpad.IsOpen) Core.Launchpad.ClosePanel();
            else Core.Launchpad.Open();
        }

        public void ToggleVisible()
        {
            if (_slidAway) SlideTo(false);
            else SlideTo(true);
        }

        private void HideLaunchpadIfOpen()
        {
            if (Core.Launchpad != null && Core.Launchpad.IsOpen) Core.Launchpad.ClosePanel();
        }

        public void HideDockTemporarily()
        {
            SlideTo(true);
        }

        private void SlideTo(bool away)
        {
            if (_slidAway == away) return;
            _slidAway = away;
            DoubleAnimation anim = new DoubleAnimation(
                away ? BarH + ShadowBottom + 10 : 0, TimeSpan.FromMilliseconds(220));
            anim.EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseOut };
            _slide.BeginAnimation(TranslateTransform.YProperty, anim);
            IsHitTestVisible = !away;
        }

        private void HandleAutoHide()
        {
            if (_suppressed) return;
            Native.POINT pt;
            Native.GetCursorPos(out pt);
            Point sp = new Point(pt.X, pt.Y);
            Point p = PointFromScreen(sp);

            bool overDock = p.X >= 0 && p.X <= Width && p.Y >= ShadowTop - 6 && p.Y <= Height;
            if (_slidAway && pt.Y >= (int)SystemParameters.WorkArea.Bottom - 4)
            {
                SlideTo(false);
                return;
            }
            if (!overDock && !_slidAway)
            {
                SlideTo(true);
            }
            else if (overDock && _slidAway)
            {
                SlideTo(false);
            }
        }

        private void CheckSuppressed()
        {
            bool busy = false;
            try
            {
                int state;
                if (Native.SHQueryUserNotificationState(out state) == 0)
                {
                    // D3D 全屏 / 演示模式：无条件让位
                    busy = state == Native.QUNS_RUNNING_D3D_FULL_SCREEN
                        || state == Native.QUNS_PRESENTATION_MODE;

                    // QUNS_BUSY 判定过宽（浏览器全屏视频、通知勿扰都会命中），
                    // 只有前台窗口真的铺满整个显示器时才让位。
                    if (!busy && state == Native.QUNS_BUSY) busy = ForegroundIsFullscreen();
                    _lastState = state;
                }
            }
            catch { }

            if (busy != _suppressed)
            {
                _suppressed = busy;
                Logger.Write("[INFO] 全屏抑制 = " + busy + "（QUNS=" + _lastState + "）");
                if (busy) SlideTo(true);
                else SlideTo(false);
            }
        }

        private static bool ForegroundIsFullscreen()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == IntPtr.Zero) return false;

            // 自己的窗口（启动台）不算"全屏应用"，否则一开启动台 Dock 就自己滑走
            uint fgPid;
            Native.GetWindowThreadProcessId(fg, out fgPid);
            if (fgPid == (uint)Process.GetCurrentProcess().Id) return false;
            Native.RECT r;
            if (!Native.GetWindowRect(fg, out r)) return false;
            IntPtr mon = Native.MonitorFromWindow(fg, 2); // MONITOR_DEFAULTTONEAREST
            Native.MONITORINFO mi = new Native.MONITORINFO();
            mi.cbSize = System.Runtime.InteropServices.Marshal.SizeOf(typeof(Native.MONITORINFO));
            if (!Native.GetMonitorInfo(mon, ref mi)) return false;
            return r.Left <= mi.rcMonitor.Left && r.Top <= mi.rcMonitor.Top
                && r.Right >= mi.rcMonitor.Right && r.Bottom >= mi.rcMonitor.Bottom;
        }

        /// <summary>把 Dock 提到最上层（用于启动台拖拽时可见）。</summary>
        public void BringAboveAll()
        {
            try
            {
                Topmost = false;
                Topmost = true;
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd != IntPtr.Zero)
                    Native.SetWindowPos(hwnd, Native.HWND_TOPMOST, 0, 0, 0, 0,
                        Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
            }
            catch (Exception ex) { Logger.Error("Dock.BringAboveAll", ex); }
        }

        /// <summary>每 ~2s 校验一次置顶状态（某些全屏/焦点切换会重置 Z 序）。</summary>
        private void EnsureTopmost()
        {
            try
            {
                IntPtr hwnd = new WindowInteropHelper(this).Handle;
                if (hwnd == IntPtr.Zero) return;
                int ex = Native.GetWindowLong(hwnd, Native.GWL_EXSTYLE);
                if ((ex & Native.WS_EX_TOPMOST) == 0) BringAboveAll();
            }
            catch { }
        }

        /// <summary>屏幕坐标是否落在 Dock 上（用于启动台拖拽投放判定）。</summary>
        public bool HitTestScreen(Point screenPoint)
        {
            try
            {
                Point p = PointFromScreen(screenPoint);
                return p.X >= 0 && p.X <= Width && p.Y >= ShadowTop - 8 && p.Y <= Height;
            }
            catch { return false; }
        }

        /// <summary>外部拖拽进入 Dock（启动台发起）。</summary>
        public void ExternalDropEnter()
        {
            SlideTo(false);
            BringAboveAll();
            SetDropHighlight(true);
        }

        public void ExternalDropLeave()
        {
            SetDropHighlight(false);
        }

        public void ExternalDropCommit(AppEntry e, Point screenPoint)
        {
            SetDropHighlight(false);
            int idx = Core.Config.Dock.Items.Count;
            try
            {
                Point p = PointFromScreen(screenPoint);
                idx = IndexForX(p.X - (Width - _baseBarW) / 2) + 1;
            }
            catch { }
            InsertEntry(e, idx);
        }

        // =================================================================
        // 对外：刷新
        // =================================================================

        public void OnCatalogChanged()
        {
            // 清单到达后，把临时的 exe 名称/图标替换成清单里的正式条目
            _extrasKey = "";
            RequestRunningSnapshot();
        }
    }
}
