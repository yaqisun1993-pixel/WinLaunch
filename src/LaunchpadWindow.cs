using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Shapes = System.Windows.Shapes;

namespace WinLaunch
{
    /// <summary>启动台：全屏覆盖层、背景虚化、自适应分页网格、搜索、键盘导航、拖拽到 Dock。</summary>
    public class LaunchpadWindow : Window
    {
        private const double CellW = 168;
        private const double CellH = 150;
        private const double IconS = 96;
        // “常用”卡片相对“卡片+网格”整体块向上提的距离（DIP）
        private const double FreqLift = 16;
        private const int MaxCols = 9;
        private const int MaxRows = 6;

        private Grid _root;
        private Image _backdrop;
        private Border _dim;
        private Grid _content;
        private Grid _pageHost;
        private Canvas _strip;
        private Canvas _layer;
        private Border _searchBorder;
        private TextBox _search;
        private TextBlock _placeholder;
        private StackPanel _dots;
        private ScaleTransform _contentScale;

        private List<AppEntry> _all = new List<AppEntry>();
        private List<AppEntry> _filtered = new List<AppEntry>();
        private readonly List<Border> _rings = new List<Border>();
        private int _cols = 7;
        private int _rows = 4;
        private int _page;
        private int _pageCount = 1;
        private int _sel = -1;
        private bool _built;
        private bool _isOpen;
        private bool _wasActive;
        private DateTime _openTime;

        // 拖拽
        private AppEntry _dragEntry;
        private System.Windows.Point _pressPoint;
        private bool _dragging;
        private Image _ghost;
        private bool _overDock;
        private readonly System.Collections.Generic.Dictionary<AppEntry, BitmapSource> _icons = new System.Collections.Generic.Dictionary<AppEntry, BitmapSource>();

        private class TileRef
        {
            public Image Img;
            public AppEntry Entry;
            public int Px;
        }

        private readonly List<TileRef> _tiles = new List<TileRef>();
        private readonly List<TileRef> _freqTiles = new List<TileRef>();
        private Border _freqCard;
        private bool _hasFrequent;
        private StackPanel _freqRow;
        private DispatcherTimer _iconTimer;

        public bool IsOpen { get { return _isOpen; } }

        public LaunchpadWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            Topmost = true;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            Title = "WinLaunch 启动台";
            UseLayoutRounding = true;
        }

        // =================================================================
        // 构建
        // =================================================================

        private void BuildShell()
        {
            _root = new Grid();
            _root.Background = Brush2.Solid("#101218");
            Content = _root;

            // 背景：截屏 + 高斯模糊（渲染时放大 4 倍，等效半径 ≈ 40px）
            _backdrop = new Image();
            _backdrop.Stretch = Stretch.Fill;
            _backdrop.Width = 640;
            _backdrop.Height = 360;
            _backdrop.HorizontalAlignment = HorizontalAlignment.Center;
            _backdrop.VerticalAlignment = VerticalAlignment.Center;
            _backdrop.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            _backdrop.RenderTransform = new ScaleTransform(4, 4);
            BlurEffect blur = new BlurEffect();
            blur.Radius = 10;
            blur.KernelType = KernelType.Gaussian;
            blur.RenderingBias = RenderingBias.Performance;
            _backdrop.Effect = blur;
            _root.Children.Add(_backdrop);

            _dim = new Border();
            _dim.Background = new SolidColorBrush(Color.FromArgb(132, 6, 7, 11));
            _root.Children.Add(_dim);

            _content = new Grid();
            _contentScale = new ScaleTransform(1, 1);
            _content.RenderTransform = _contentScale;
            _content.RenderTransformOrigin = new System.Windows.Point(0.5, 0.5);
            _root.Children.Add(_content);

            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(232) });
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(78) });

            // 搜索框
            _searchBorder = new Border();
            _searchBorder.Width = 420;
            _searchBorder.Height = 44;
            _searchBorder.CornerRadius = new CornerRadius(22);
            _searchBorder.Background = new SolidColorBrush(Color.FromArgb(120, 10, 12, 18));
            _searchBorder.BorderBrush = new SolidColorBrush(Color.FromArgb(100, 255, 255, 255));
            _searchBorder.BorderThickness = new Thickness(1);
            _searchBorder.VerticalAlignment = VerticalAlignment.Top;
            _searchBorder.HorizontalAlignment = HorizontalAlignment.Center;
            _searchBorder.Margin = new Thickness(0, 56, 0, 0);

            Grid searchInner = new Grid();
            _searchBorder.Child = searchInner;

            TextBlock icon = new TextBlock();
            icon.Text = "\uE721";
            icon.FontFamily = new FontFamily("Segoe MDL2 Assets");
            icon.FontSize = 14;
            icon.Foreground = Brush2.Solid("#8892A6");
            icon.VerticalAlignment = VerticalAlignment.Center;
            icon.Margin = new Thickness(18, 0, 0, 0);
            icon.HorizontalAlignment = HorizontalAlignment.Left;
            searchInner.Children.Add(icon);

            _search = new TextBox();
            _search.Background = Brushes.Transparent;
            _search.BorderThickness = new Thickness(0);
            _search.Foreground = Brush2.Solid("#EDEFF4");
            _search.CaretBrush = Brush2.Solid("#EDEFF4");
            _search.FontSize = 14;
            _search.VerticalContentAlignment = VerticalAlignment.Center;
            _search.Margin = new Thickness(46, 0, 16, 0);
            _search.TextChanged += delegate { ApplyFilter(); };
            searchInner.Children.Add(_search);

            _placeholder = new TextBlock();
            _placeholder.Text = "搜索应用、拼音首字母（如 wx → 微信）";
            _placeholder.FontSize = 14;
            _placeholder.Foreground = Brush2.Solid("#C3CAD8");
            _placeholder.VerticalAlignment = VerticalAlignment.Center;
            _placeholder.Margin = new Thickness(46, 0, 0, 0);
            _placeholder.IsHitTestVisible = false;
            searchInner.Children.Add(_placeholder);

            Grid.SetRow(_searchBorder, 0);
            _content.Children.Add(_searchBorder);

            // 常用：与 Dock 同一套视觉语言的卡片（圆角半透明 + 左侧标签 + 分隔线）
            _freqCard = new Border();
            _freqCard.CornerRadius = new CornerRadius(22);
            _freqCard.Background = new SolidColorBrush(Color.FromArgb(58, 10, 12, 18));
            _freqCard.BorderBrush = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255));
            _freqCard.BorderThickness = new Thickness(1);
            _freqCard.Padding = new Thickness(14, 8, 14, 8);
            _freqCard.MinWidth = 420;
            _freqCard.HorizontalAlignment = HorizontalAlignment.Center;
            _freqCard.VerticalAlignment = VerticalAlignment.Top;
            _freqCard.Margin = new Thickness(0, 114, 0, 0);
            Grid.SetRow(_freqCard, 0);
            _content.Children.Add(_freqCard);

            StackPanel inner = new StackPanel();
            inner.Orientation = Orientation.Horizontal;
            inner.VerticalAlignment = VerticalAlignment.Center;
            inner.HorizontalAlignment = HorizontalAlignment.Center;
            _freqCard.Child = inner;

            TextBlock cap = new TextBlock();
            cap.Text = "常用";
            cap.FontSize = 12;
            cap.Foreground = Brush2.Solid("#9AA5B8");
            cap.VerticalAlignment = VerticalAlignment.Center;
            cap.Margin = new Thickness(8, 0, 14, 0);
            inner.Children.Add(cap);

            Shapes.Rectangle sep = new Shapes.Rectangle();
            sep.Width = 1;
            sep.Height = 30;
            sep.Fill = Brush2.Solid("#26FFFFFF");
            sep.VerticalAlignment = VerticalAlignment.Center;
            sep.Margin = new Thickness(0, 0, 12, 0);
            inner.Children.Add(sep);

            _freqRow = new StackPanel();
            _freqRow.Orientation = Orientation.Horizontal;
            _freqRow.VerticalAlignment = VerticalAlignment.Center;
            inner.Children.Add(_freqRow);

            // 分页区
            _pageHost = new Grid();
            _pageHost.ClipToBounds = true;
            Grid.SetRow(_pageHost, 1);
            _content.Children.Add(_pageHost);

            _strip = new Canvas();
            _strip.Width = 100;
            _strip.Height = 100;
            _strip.RenderTransform = new TranslateTransform(0, 0);
            _pageHost.Children.Add(_strip);

            // 拖拽层
            _layer = new Canvas();
            _layer.IsHitTestVisible = false;
            Grid.SetRowSpan(_layer, 3);
            Panel.SetZIndex(_layer, 500);
            _content.Children.Add(_layer);

            // 页点
            _dots = new StackPanel();
            _dots.Orientation = Orientation.Horizontal;
            _dots.HorizontalAlignment = HorizontalAlignment.Center;
            _dots.VerticalAlignment = VerticalAlignment.Center;
            Grid.SetRow(_dots, 2);
            _content.Children.Add(_dots);

            MouseLeftButtonDown += Pad_MouseDown;
            MouseMove += Pad_MouseMove;
            MouseLeftButtonUp += Pad_MouseUp;
            PreviewKeyDown += Pad_PreviewKeyDown;
            MouseWheel += Pad_MouseWheel;

            // 切到别的程序（Alt+Tab / Win+D）时自动收起，避免全屏覆盖层一直压在最上面。
            // _wasActive 保证“从没拿到过焦点”的环境下不会被 Deactivated 立刻关掉。
            Activated += delegate { _wasActive = true; };
            Deactivated += delegate
            {
                if (!_isOpen || !_wasActive) return;
                if ((DateTime.UtcNow - _openTime).TotalMilliseconds < 400) return;
                ClosePanel();
            };
        }

        // =================================================================
        // 打开 / 关闭
        // =================================================================

        public void Open()
        {
            if (_isOpen)
            {
                ClosePanel();
                return;
            }
            Logger.Write("[INFO] Launchpad.Open");
            if (!_built)
            {
                BuildShell();
                _built = true;
            }

            _all = Core.Catalog;
            Width = SystemParameters.PrimaryScreenWidth;
            Height = SystemParameters.PrimaryScreenHeight;
            Left = 0;
            Top = 0;

            CheckLayout();
            CaptureBackdrop();
            ApplyFilter();
            _search.Text = "";
            _page = 0;
            _sel = -1;
            UpdateDots();

            // 窗口本身保持不透明（避免 WPF 因 Opacity=0 不提交窗口），淡入作用在内容层
            Opacity = 1;
            _root.Opacity = 0;
            _contentScale.ScaleX = 0.97;
            _contentScale.ScaleY = 0.97;
            Topmost = true;
            _wasActive = false;
            _openTime = DateTime.UtcNow;
            Show();
            Activate();
            _isOpen = true;

            DoubleAnimation fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(240));
            fade.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _root.BeginAnimation(UIElement.OpacityProperty, fade);

            DoubleAnimation scale = new DoubleAnimation(0.97, 1.0, TimeSpan.FromMilliseconds(240));
            scale.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut };
            _contentScale.BeginAnimation(ScaleTransform.ScaleXProperty, scale);
            _contentScale.BeginAnimation(ScaleTransform.ScaleYProperty, scale);

            Dispatcher.BeginInvoke(new Action(delegate { _search.Focus(); }),
                System.Windows.Threading.DispatcherPriority.Input);
        }

        /// <summary>目录扫描完成后刷新（保持打开状态）。</summary>
        public void RefreshApps()
        {
            _all = Core.Catalog;
            ApplyFilter();
        }

        public void ClosePanel()
        {
            if (!_isOpen) return;
            Logger.Write("[INFO] Launchpad.Close");
            _isOpen = false;
            DoubleAnimation fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            fade.Completed += delegate
            {
                if (!_isOpen) Hide();
            };
            _root.BeginAnimation(UIElement.OpacityProperty, fade);
            if (Core.Dock != null) Core.Dock.ExternalDropLeave();
        }

        // =================================================================
        // 布局
        // =================================================================

        private void CheckLayout()
        {
            int c = (int)(Width * 0.80 / CellW);
            int r = (int)(Height * 0.62 / CellH);
            _cols = Math.Max(4, Math.Min(MaxCols, c));
            _rows = Math.Max(3, Math.Min(MaxRows, r));
        }

        private void ApplyFilter()
        {
            string q = _search == null ? "" : _search.Text.Trim();
            _placeholder.Visibility = string.IsNullOrEmpty(q) ? Visibility.Visible : Visibility.Collapsed;

            _filtered = new List<AppEntry>();
            foreach (AppEntry e in _all)
            {
                if (string.IsNullOrEmpty(q) || Matches(e, q)) _filtered.Add(e);
            }
            _page = 0;
            _sel = -1;
            RebuildFrequent(string.IsNullOrEmpty(q));
        }

        private static bool Matches(AppEntry e, string q)
        {
            if (string.IsNullOrEmpty(q)) return true;
            if (e.Name != null && e.Name.IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            if (e.ResolvedTarget != null &&
                Path.GetFileNameWithoutExtension(e.ResolvedTarget)
                    .IndexOf(q, StringComparison.OrdinalIgnoreCase) >= 0) return true;
            // 拼音/英文首字母：wx → 微信，vsc → Visual Studio Code
            return Pinyin.Matches(e.Name, q);
        }

        private void RebuildPages()
        {
            int perPage = _cols * _rows;
            _pageCount = Math.Max(1, (int)Math.Ceiling(_filtered.Count / (double)perPage));
            if (_page > _pageCount - 1) _page = _pageCount - 1;
            if (_page < 0) _page = 0;

            _strip.Children.Clear();
            _rings.Clear();
            _tiles.Clear();
            StartIconTimer();

            double gridW = _cols * CellW;
            double gridH = _rows * CellH;
            double originX = (Width - gridW) / 2;

            // 顶部区域 = 搜索框；"常用"卡片与网格作为一个整体在搜索框下方垂直居中
            bool hasCard = _hasFrequent;
            double cardH = hasCard ? 104 : 0;
            double gap = hasCard ? 26 : 0;
            double areaTop = 100;
            double areaBottom = Height - 78;
            double blockH = cardH + gap + gridH;
            double blockTop = areaTop + Math.Max(0, (areaBottom - areaTop - blockH) / 2);
            double gridTop = blockTop + cardH + gap;

            _content.RowDefinitions[0].Height = new GridLength(Math.Max(96, gridTop));
            // 只把“常用”卡片上提一点，网格保持不动
            if (_freqCard != null) _freqCard.Margin = new Thickness(0, Math.Max(0, blockTop - FreqLift), 0, 0);

            double originY = 0;

            for (int p = 0; p < _pageCount; p++)
            {
                Canvas page = new Canvas();
                page.Width = Width;
                page.Height = Height;
                Canvas.SetLeft(page, p * Width);
                _strip.Children.Add(page);

                int start = p * perPage;
                for (int i = 0; i < perPage; i++)
                {
                    int idx = start + i;
                    if (idx >= _filtered.Count) break;
                    int row = i / _cols;
                    int col = i % _cols;
                    FrameworkElement tile = BuildTile(_filtered[idx], originX + col * CellW, originY + row * CellH);
                    page.Children.Add(tile);
                }
            }

            _strip.Width = Width * _pageCount;
            _strip.Height = Height;
            SetPageTransform(_page, false);
            UpdateDots();
        }

        private FrameworkElement BuildTile(AppEntry e, double x, double y)
        {
            Grid tile = new Grid();
            tile.Width = CellW;
            tile.Height = CellH;

            Border ring = new Border();
            ring.CornerRadius = new CornerRadius(18);
            ring.BorderThickness = new Thickness(2);
            ring.BorderBrush = Brush2.Solid("#4C8DFF");
            ring.Margin = new Thickness((CellW - IconS) / 2 - 6, -6, (CellW - IconS) / 2 - 6, 0);
            ring.VerticalAlignment = VerticalAlignment.Top;
            ring.Height = IconS + 12;
            ring.Opacity = 0;
            tile.Children.Add(ring);
            _rings.Add(ring);

            Image img = new Image();
            img.Width = IconS;
            img.Height = IconS;
            img.Stretch = Stretch.Uniform;
            img.VerticalAlignment = VerticalAlignment.Top;
            img.HorizontalAlignment = HorizontalAlignment.Center;
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            tile.Children.Add(img);

            TextBlock label = new TextBlock();
            label.Text = e.Name;
            label.FontSize = 13;
            label.Foreground = Brush2.Solid("#F0F2F6");
            label.TextAlignment = TextAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Width = CellW - 12;
            label.Margin = new Thickness(6, IconS + 8, 6, 0);
            label.VerticalAlignment = VerticalAlignment.Top;
            tile.Children.Add(label);

            Canvas.SetLeft(tile, x);
            Canvas.SetTop(tile, y);

            AppEntry local = e;
            tile.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs ev)
            {
                _dragEntry = local;
                _pressPoint = ev.GetPosition(this);
                _dragging = false;
                CaptureMouse();
                ev.Handled = true;
            };

            int px = (int)Math.Round(160 * Core.DpiScale);
            _tiles.Add(new TileRef { Img = img, Entry = local, Px = px });
            BitmapSource ready = IconService.Peek(local, px);
            if (ready != null)
            {
                img.Source = ready;
                _icons[local] = ready;
            }
            IconService.Get(local, px, delegate(BitmapSource bs) { });

            return tile;
        }

        private void PullTileIcons(List<TileRef> list)
        {
            for (int i = 0; i < list.Count; i++)
            {
                TileRef t = list[i];
                if (t.Img == null || t.Img.Source != null) continue;
                BitmapSource ready = IconService.Peek(t.Entry, t.Px);
                if (ready != null)
                {
                    t.Img.Source = ready;
                    _icons[t.Entry] = ready;
                }
            }
        }

        /// <summary>重建"常用"一行：按启动频次 × 时间衰减取前 8 个。</summary>
        private void RebuildFrequent(bool visible)
        {
            if (_freqRow == null) return;
            _freqRow.Children.Clear();
            _freqTiles.Clear();

            if (!visible)
            {
                // 搜索时只隐藏卡片，网格位置保持不变，避免输入过程中版面跳动
                _freqCard.Visibility = Visibility.Collapsed;
                RebuildPages();
                return;
            }

            List<AppEntry> top = UsageStore.Top(8);
            _hasFrequent = top.Count > 0;
            if (!_hasFrequent)
            {
                _freqCard.Visibility = Visibility.Collapsed;
                RebuildPages();
                return;
            }

            _freqCard.Visibility = Visibility.Visible;
            foreach (AppEntry e in top) _freqRow.Children.Add(BuildFrequentTile(e));
            RebuildPages();
        }


        private FrameworkElement BuildFrequentTile(AppEntry e)
        {
            // 圆角高亮底 + 图标 + 名称，悬停时浮出浅色底
            Border pill = new Border();
            pill.Width = 88;
            pill.Height = 88;
            pill.CornerRadius = new CornerRadius(16);
            pill.Background = Brushes.Transparent;
            pill.Cursor = Cursors.Hand;
            pill.Margin = new Thickness(2, 0, 2, 0);

            Grid tile = new Grid();
            pill.Child = tile;

            Image img = new Image();
            img.Width = 58;
            img.Height = 58;
            img.Stretch = Stretch.Uniform;
            img.VerticalAlignment = VerticalAlignment.Top;
            img.HorizontalAlignment = HorizontalAlignment.Center;
            img.Margin = new Thickness(0, 4, 0, 0);
            RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.HighQuality);
            tile.Children.Add(img);

            TextBlock label = new TextBlock();
            label.Text = e.Name;
            label.FontSize = 11.5;
            label.Foreground = Brush2.Solid("#D6DBE4");
            label.TextAlignment = TextAlignment.Center;
            label.TextTrimming = TextTrimming.CharacterEllipsis;
            label.Width = 84;
            label.Margin = new Thickness(0, 66, 0, 0);
            label.VerticalAlignment = VerticalAlignment.Top;
            tile.Children.Add(label);

            SolidColorBrush hover = new SolidColorBrush(Color.FromArgb(30, 255, 255, 255));
            pill.MouseEnter += delegate { pill.Background = hover; label.Foreground = Brush2.Solid("#FFFFFF"); };
            pill.MouseLeave += delegate { pill.Background = Brushes.Transparent; label.Foreground = Brush2.Solid("#D6DBE4"); };

            AppEntry local = e;
            // 和普通网格图标走同一套“按下 → 松开启动 / 拖到 Dock”逻辑。
            // 之前只在 MouseUp 启动，而窗口在 MouseDown 就关了面板，慢点击会丢事件。
            pill.MouseLeftButtonDown += delegate(object s, MouseButtonEventArgs ev)
            {
                _dragEntry = local;
                _pressPoint = ev.GetPosition(this);
                _dragging = false;
                CaptureMouse();
                ev.Handled = true;
            };

            int px = (int)Math.Round(160 * Core.DpiScale);
            BitmapSource ready = IconService.Peek(local, px);
            if (ready != null)
            {
                img.Source = ready;
                _icons[local] = ready;
            }
            else
            {
                IconService.Get(local, px, delegate(BitmapSource bs)
                {
                    if (bs != null)
                    {
                        img.Source = bs;
                        _icons[local] = bs;
                    }
                });
            }
            _freqTiles.Add(new TileRef { Img = img, Entry = local, Px = px });

            return pill;
        }

        private void StartIconTimer()
        {
            if (_iconTimer != null) return;
            _iconTimer = new DispatcherTimer(DispatcherPriority.Background);
            _iconTimer.Interval = TimeSpan.FromMilliseconds(120);
            _iconTimer.Tick += delegate
            {
                PullTileIcons(_tiles);
                PullTileIcons(_freqTiles);
            };
            _iconTimer.Start();
        }

        private void SetPageTransform(int page, bool animate)
        {
            TranslateTransform tt = _strip.RenderTransform as TranslateTransform;
            if (tt == null) return;
            double target = -page * Width;
            if (animate)
            {
                DoubleAnimation anim = new DoubleAnimation(tt.X, target, TimeSpan.FromMilliseconds(320));
                anim.EasingFunction = new CubicEase { EasingMode = EasingMode.EaseInOut };
                tt.BeginAnimation(TranslateTransform.XProperty, anim);
            }
            else
            {
                tt.BeginAnimation(TranslateTransform.XProperty, null);
                tt.X = target;
            }
        }

        private void UpdateDots()
        {
            _dots.Children.Clear();
            for (int i = 0; i < _pageCount; i++)
            {
                Shapes.Ellipse dot = new Shapes.Ellipse();
                dot.Width = 8;
                dot.Height = 8;
                dot.Margin = new Thickness(5, 0, 5, 0);
                dot.Fill = Brush2.Solid(i == _page ? "#EDEFF4" : "#70FFFFFF");
                _dots.Children.Add(dot);
            }
        }

        private void GoPage(int page, bool animate)
        {
            if (_pageCount <= 1) return;
            _page = Math.Max(0, Math.Min(_pageCount - 1, page));
            SetPageTransform(_page, animate);
            UpdateDots();
            UpdateSelection();
        }

        // =================================================================
        // 背景虚化
        // =================================================================

        private void CaptureBackdrop()
        {
            try
            {
                double wDip = SystemParameters.PrimaryScreenWidth;
                double hDip = SystemParameters.PrimaryScreenHeight;
                if (wDip <= 0 || hDip <= 0) return;

                // CopyFromScreen 用物理像素，WPF 窗口/元素用 DIP，按系统 DPI 换算
                int w = Math.Max(1, (int)Math.Round(wDip * Core.DpiScale));
                int h = Math.Max(1, (int)Math.Round(hDip * Core.DpiScale));
                int sw = Math.Max(1, (int)Math.Round(wDip / 4));
                int sh = Math.Max(1, (int)Math.Round(hDip / 4));

                using (System.Drawing.Bitmap full = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                {
                    using (System.Drawing.Graphics g = System.Drawing.Graphics.FromImage(full))
                    {
                        g.CopyFromScreen(0, 0, 0, 0, new System.Drawing.Size(w, h), System.Drawing.CopyPixelOperation.SourceCopy);
                    }

                    // 不再移动 Dock（移动会让 Dock 闪一下）。直接把截图里 Dock 的区域
                    // 用上方一条背景覆盖，再交给后面的模糊处理。
                    if (Core.Dock != null && Core.Dock.IsVisible)
                    {
                        try
                        {
                            int dx = (int)Math.Round(Core.Dock.Left * Core.DpiScale);
                            int dy = (int)Math.Round(Core.Dock.Top * Core.DpiScale);
                            int dw = (int)Math.Round(Core.Dock.Width * Core.DpiScale);
                            int dh = (int)Math.Round(Core.Dock.Height * Core.DpiScale);
                            dx = Math.Max(0, Math.Min(w - 1, dx));
                            dy = Math.Max(0, Math.Min(h - 1, dy));
                            dw = Math.Min(dw, w - dx);
                            dh = Math.Min(dh, h - dy);

                            if (dw > 0 && dh > 0)
                            {
                                int srcY = Math.Max(0, dy - dh);
                                int srcH = Math.Min(dh, dy - srcY);
                                if (srcH > 0)
                                {
                                    System.Drawing.Rectangle src = new System.Drawing.Rectangle(dx, srcY, dw, srcH);
                                    System.Drawing.Rectangle dst = new System.Drawing.Rectangle(dx, dy, dw, dh);
                                    using (System.Drawing.Bitmap patch = full.Clone(src, full.PixelFormat))
                                    using (System.Drawing.Graphics g3 = System.Drawing.Graphics.FromImage(full))
                                    {
                                        g3.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                                        g3.DrawImage(patch, dst);
                                    }
                                }
                            }
                        }
                        catch { }
                    }

                    using (System.Drawing.Bitmap small = new System.Drawing.Bitmap(sw, sh, System.Drawing.Imaging.PixelFormat.Format24bppRgb))
                    {
                        using (System.Drawing.Graphics g2 = System.Drawing.Graphics.FromImage(small))
                        {
                            g2.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                            g2.DrawImage(full, 0, 0, sw, sh);
                        }
                        using (MemoryStream ms = new MemoryStream())
                        {
                            small.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
                            ms.Position = 0;
                            BitmapImage bi = new BitmapImage();
                            bi.BeginInit();
                            bi.CacheOption = BitmapCacheOption.OnLoad;
                            bi.StreamSource = ms;
                            bi.EndInit();
                            bi.Freeze();
                            _backdrop.Width = sw;
                            _backdrop.Height = sh;
                            _backdrop.Source = bi;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error("Launchpad.CaptureBackdrop", ex);
            }
        }

        // =================================================================
        // 交互
        // =================================================================

        private void Pad_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (_searchBorder != null && _searchBorder.IsMouseOver) return;
            if (_dragEntry == null) ClosePanel();
        }

        private void Pad_MouseMove(object sender, MouseEventArgs e)
        {
            if (_dragEntry == null) return;
            System.Windows.Point p = e.GetPosition(this);

            if (!_dragging)
            {
                if (Math.Abs(p.X - _pressPoint.X) + Math.Abs(p.Y - _pressPoint.Y) < 6) return;
                BeginDrag(p);
            }

            MoveGhost(p);

            System.Windows.Point sp = PointToScreen(p);
            bool over = Core.Dock != null && Core.Dock.HitTestScreen(sp);
            if (over != _overDock)
            {
                _overDock = over;
                if (over) Core.Dock.ExternalDropEnter();
                else Core.Dock.ExternalDropLeave();
            }
        }

        private void Pad_MouseUp(object sender, MouseButtonEventArgs e)
        {
            ReleaseMouseCapture();
            if (_dragEntry == null) return;

            AppEntry entry = _dragEntry;
            _dragEntry = null;

            if (_dragging)
            {
                EndDrag(entry);
            }
            else
            {
                // 单击启动
                LaunchFromLaunchpad(entry);
            }
        }

        private void LaunchFromLaunchpad(AppEntry e)
        {
            ClosePanel();
            LaunchService.LaunchOrActivate(e);
        }

        private void BeginDrag(System.Windows.Point p)
        {
            _dragging = true;
            _root.Opacity = 0.45;
            if (_ghost == null)
            {
                _ghost = new Image();
                _ghost.Width = IconS * 0.9;
                _ghost.Height = IconS * 0.9;
                _ghost.Stretch = Stretch.Uniform;
                _ghost.Opacity = 0.95;
                DropShadowEffect sh = new DropShadowEffect();
                sh.BlurRadius = 24;
                sh.ShadowDepth = 6;
                sh.Direction = 270;
                sh.Opacity = 0.7;
                _ghost.Effect = sh;
                _layer.Children.Add(_ghost);
            }
            BitmapSource src = null;
            _icons.TryGetValue(_dragEntry, out src);
            _ghost.Source = src;
            _ghost.Visibility = Visibility.Visible;
            if (Core.Dock != null) Core.Dock.BringAboveAll();
            MoveGhost(p);
        }

        private void MoveGhost(System.Windows.Point p)
        {
            if (_ghost == null) return;
            Canvas.SetLeft(_ghost, p.X - _ghost.Width / 2);
            Canvas.SetTop(_ghost, p.Y - _ghost.Height / 2);
        }

        private void EndDrag(AppEntry entry)
        {
            if (_ghost != null) _ghost.Visibility = Visibility.Collapsed;
            _root.Opacity = 1.0;

            System.Windows.Point sp = PointToScreen(Mouse.GetPosition(this));
            bool over = Core.Dock != null && Core.Dock.HitTestScreen(sp);

            if (over)
            {
                Core.Dock.ExternalDropCommit(entry, sp);
                ClosePanel();
            }
            else if (Core.Dock != null)
            {
                Core.Dock.ExternalDropLeave();
            }
            _dragging = false;
            _overDock = false;
        }

        private void Pad_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Escape)
            {
                if (!string.IsNullOrEmpty(_search.Text))
                {
                    _search.Text = "";
                    e.Handled = true;
                }
                else
                {
                    ClosePanel();
                    e.Handled = true;
                }
                return;
            }

            if (e.Key == Key.Enter)
            {
                AppEntry target = null;
                if (_sel >= 0 && _sel < _filtered.Count) target = _filtered[_sel];
                else if (_filtered.Count > 0) target = _filtered[0];
                if (target != null) LaunchFromLaunchpad(target);
                e.Handled = true;
                return;
            }

            // 搜索框里左右键应该移动光标，不能被窗口的网格导航吃掉
            if (_search != null && _search.IsKeyboardFocusWithin &&
                (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Home || e.Key == Key.End))
                return;

            int perPage = _cols * _rows;
            if (e.Key == Key.Left || e.Key == Key.Right || e.Key == Key.Up || e.Key == Key.Down)
            {
                if (_filtered.Count == 0) return;
                int delta = 0;
                if (e.Key == Key.Left) delta = -1;
                if (e.Key == Key.Right) delta = 1;
                if (e.Key == Key.Up) delta = -_cols;
                if (e.Key == Key.Down) delta = _cols;

                // 第一次按键选中本页第一个，而不是跳过一个
                if (_sel < 0) _sel = _page * perPage;
                else _sel += delta;

                if (_sel < 0) _sel = 0;
                if (_sel >= _filtered.Count) _sel = _filtered.Count - 1;

                int page = _sel / perPage;
                if (page != _page) GoPage(page, true);
                UpdateSelection();
                e.Handled = true;
                return;
            }

            if (e.Key == Key.PageDown) { _sel = -1; GoPage(_page + 1, true); e.Handled = true; return; }
            if (e.Key == Key.PageUp) { _sel = -1; GoPage(_page - 1, true); e.Handled = true; return; }
        }

        private void UpdateSelection()
        {
            for (int i = 0; i < _rings.Count; i++)
                _rings[i].Opacity = (i == _sel) ? 1 : 0;
        }

        private void Pad_MouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (e.Delta < 0) GoPage(_page + 1, true);
            else GoPage(_page - 1, true);
        }

    }
}
