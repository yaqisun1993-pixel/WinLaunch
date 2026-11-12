Add-Type -AssemblyName System.Drawing

$W = 1800; $H = 1250
$bmp = New-Object System.Drawing.Bitmap($W, $H)
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
$g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::ClearTypeGridFit
$g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic

function C([string]$hex, [int]$a = 255) {
    $h = $hex.TrimStart('#')
    [System.Drawing.Color]::FromArgb($a, [Convert]::ToInt32($h.Substring(0,2),16), [Convert]::ToInt32($h.Substring(2,2),16), [Convert]::ToInt32($h.Substring(4,2),16))
}
function RoundPath([System.Drawing.RectangleF]$rect, [single]$r) {
    $p = New-Object System.Drawing.Drawing2D.GraphicsPath
    $d = [single]($r * 2)
    $p.AddArc($rect.X, $rect.Y, $d, $d, 180, 90)
    $p.AddArc($rect.Right - $d, $rect.Y, $d, $d, 270, 90)
    $p.AddArc($rect.Right - $d, $rect.Bottom - $d, $d, $d, 0, 90)
    $p.AddArc($rect.X, $rect.Bottom - $d, $d, $d, 90, 90)
    $p.CloseFigure()
    return $p
}
function FillRR([single]$x,[single]$y,[single]$w,[single]$h,[single]$r,$brush) {
    $p = RoundPath ([System.Drawing.RectangleF]::new($x,$y,$w,$h)) $r
    $g.FillPath($brush, $p); $p.Dispose()
}
function StrokeRR([single]$x,[single]$y,[single]$w,[single]$h,[single]$r,$pen) {
    $p = RoundPath ([System.Drawing.RectangleF]::new($x,$y,$w,$h)) $r
    $g.DrawPath($pen, $p); $p.Dispose()
}
function AlignOf($name) {
    switch ($name) {
        'Center' { [System.Drawing.StringAlignment]::Center }
        'Far'    { [System.Drawing.StringAlignment]::Far }
        default  { [System.Drawing.StringAlignment]::Near }
    }
}
function Txt($s,[single]$x,[single]$y,[single]$size,$color,[bool]$bold = $false,[string]$align = 'Near') {
    $style = if ($bold) { [System.Drawing.FontStyle]::Bold } else { [System.Drawing.FontStyle]::Regular }
    $f = New-Object System.Drawing.Font('Microsoft YaHei UI', $size, $style)
    $b = New-Object System.Drawing.SolidBrush($color)
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = AlignOf $align
    $g.DrawString($s, $f, $b, $x, $y, $sf)
    $f.Dispose(); $b.Dispose(); $sf.Dispose()
}
function IconTile([single]$x,[single]$y,[single]$s,[string]$color,[string]$letter,[single]$radius) {
    $r = [System.Drawing.RectangleF]::new($x,$y,$s,$s)
    $br = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r, (C $color), (C $color 175), 90)
    FillRR $x $y $s $s $radius $br
    $br.Dispose()
    $hi = New-Object System.Drawing.Drawing2D.LinearGradientBrush($r, (C '#FFFFFF' 46), (C '#FFFFFF' 0), 90)
    FillRR $x $y $s ($s * 0.5) $radius $hi
    $hi.Dispose()
    $f = New-Object System.Drawing.Font('Segoe UI', [single]($s * 0.40), [System.Drawing.FontStyle]::Bold)
    $b = New-Object System.Drawing.SolidBrush([System.Drawing.Color]::FromArgb(240,255,255,255))
    $sf = New-Object System.Drawing.StringFormat
    $sf.Alignment = 'Center'; $sf.LineAlignment = 'Center'
    $g.DrawString($letter, $f, $b, [System.Drawing.RectangleF]::new($x,$y,$s,$s), $sf)
    $f.Dispose(); $b.Dispose(); $sf.Dispose()
}
function Card([single]$x,[single]$y,[single]$w,[single]$h,[string]$title,[string[]]$lines) {
    FillRR $x $y $w $h 10 (New-Object System.Drawing.SolidBrush((C '#FFFFFF' 12)))
    StrokeRR $x $y $w $h 10 (New-Object System.Drawing.Pen((C '#FFFFFF' 30), 1))
    Txt $title ($x + 14) ($y + 10) 9.5 (C '#EDEFF4') $true
    $yy = $y + 32
    foreach ($l in $lines) {
        Txt $l ($x + 14) $yy 9 (C '#9AA4B6')
        $yy += 19
    }
}

$ink    = C '#EDEFF4'
$muted  = C '#8892A6'
$dimPen = New-Object System.Drawing.Pen((C '#4A5568'), 1)

# ---------- background ----------
$bg = New-Object System.Drawing.Drawing2D.LinearGradientBrush(
    [System.Drawing.Rectangle]::new(0,0,$W,$H), (C '#0B0C10'), (C '#14161D'), 45)
$g.FillRectangle($bg, 0, 0, $W, $H)
$bg.Dispose()

Txt 'WinLaunch · 界面布局与尺寸标注' 60 32 20 $ink $true
Txt '设计基准 100% DPI · 参考环境 2560×1440 @96dpi（Windows 10 22H2）' 60 70 10.5 $muted

# ==================== Launchpad ====================
$px = 60; $py = 108; $pw = 1680; $ph = 640

FillRR $px $py $pw $ph 18 (New-Object System.Drawing.SolidBrush((C '#0F1117')))
StrokeRR $px $py $pw $ph 18 (New-Object System.Drawing.Pen((C '#232833'), 1))

$clip = RoundPath ([System.Drawing.RectangleF]::new($px+1,$py+1,$pw-2,$ph-2)) 17
$g.SetClip($clip)
$g.FillRectangle((New-Object System.Drawing.SolidBrush((C '#1B2333'))), $px, $py, $pw, $ph)
for ($i = 14; $i -ge 1; $i--) {
    $a = [int](5 + $i * 1.1)
    $br = New-Object System.Drawing.SolidBrush((C '#5C77A8' $a))
    $rr = 90 + $i * 62
    $g.FillEllipse($br, ($px + $pw/2 - $rr), ($py + 380 - $rr*0.5), ($rr*2), ($rr*1.05))
    $br.Dispose()
}
$g.FillRectangle((New-Object System.Drawing.SolidBrush((C '#000000' 100))), $px, $py, $pw, $ph)
$g.ResetClip(); $clip.Dispose()

Txt '① 启动台（Launchpad）' ($px + 28) ($py + 18) 12.5 $ink $true
Txt '全屏覆盖层 · 截屏 → 1/4 缩放 → 高斯模糊 r=24 → 32% 黑遮罩' ($px + 268) ($py + 21) 9.5 $muted

$sw = 420; $sh = 44; $sx = $px + $pw/2 - $sw/2; $sy = $py + 60
FillRR $sx $sy $sw $sh 22 (New-Object System.Drawing.SolidBrush((C '#FFFFFF' 20)))
StrokeRR $sx $sy $sw $sh 22 (New-Object System.Drawing.Pen((C '#FFFFFF' 50), 1))
$magFont = New-Object System.Drawing.Font('Segoe MDL2 Assets', 11)
$magBr = New-Object System.Drawing.SolidBrush($muted)
$g.DrawString([char]0xE721, $magFont, $magBr, ($sx + 18), ($sy + 12))
$magFont.Dispose(); $magBr.Dispose()
Txt '搜索应用、拼音首字母（如 wx → 微信）' ($sx + 46) ($sy + 13) 10.5 (C '#9AA4B6')

Card ($px + $pw - 418) ($py + 52) 390 112 '网格令牌' @(
    '图标 96 × 96 · 圆角 18 · 标签 13px 单行省略',
    '单元格 168 × 150 · 页点 8px / 间距 10',
    'cols = clamp(⌊W × 0.80 / 168⌋, 4, 9)',
    'rows = clamp(⌊H × 0.62 / 150⌋, 3, 6)'
)

$cols = 7; $rows = 3; $cellW = 168; $cellH = 150; $iconS = 96
$gridW = $cols * $cellW
$gx = $px + $pw/2 - $gridW/2
$gy = $py + 172
$palette = @('#6B7BFF','#34C759','#FF9F0A','#FF6B6B','#22D3EE','#A855F7','#F472B6','#4C8DFF','#10B981','#F59E0B','#EF4444','#8B5CF6','#06B6D4','#EC4899','#3B82F6','#22C55E','#F97316','#E11D48','#7C3AED','#0EA5E9','#D946EF')
$names = @('浏览器','终端','文件管理','音乐','相机','邮件','日历','地图','备忘录','设置','计算器','照片','商店','时钟','天气','视频','任务','资讯','图书','翻译','阅读')
$k = 0
for ($r = 0; $r -lt $rows; $r++) {
    for ($c = 0; $c -lt $cols; $c++) {
        $cx = $gx + $c * $cellW + ($cellW - $iconS)/2
        $cy = $gy + $r * $cellH
        IconTile $cx $cy $iconS $palette[$k % $palette.Count] ([string][char](65 + ($k % 26))) 18
        Txt $names[$k % $names.Count] ($cx + $iconS/2) ($cy + $iconS + 8) 9.5 (C '#D7DCE6') $false 'Center'
        $k++
    }
}

# 单元格示意框（虚线）
$dash = New-Object System.Drawing.Pen((C '#6B7A90'), 1)
$dash.DashStyle = [System.Drawing.Drawing2D.DashStyle]::Dash
$g.DrawRectangle($dash, $gx, ($gy - 2), $cellW, ($cellH - 6))
$dash.Dispose()
Txt '单元格 168 × 150' ($gx + 4) ($gy - 20) 9 (C '#7C8AA0')

$dotY = $py + $ph - 22
$dotTotal = 3*18 + 8
$dotX = $px + $pw/2 - $dotTotal/2
for ($i = 0; $i -lt 4; $i++) {
    $br = if ($i -eq 0) { New-Object System.Drawing.SolidBrush($ink) } else { New-Object System.Drawing.SolidBrush((C '#FFFFFF' 70)) }
    $g.FillEllipse($br, ($dotX + $i*18), $dotY, 8, 8)
    $br.Dispose()
}

Txt '打开：240ms easeOutCubic（scale 0.97→1 + fade） · 翻页：320ms · 关闭：180ms · Esc / 空白点击 / 热键二次触发' ($px + 28) ($py + $ph + 14) 9.5 (C '#9AA4B6')
Txt '键盘：方向键移动焦点，Enter 启动，Esc 清空搜索（空则关闭），PageUp/PageDown 翻页' ($px + 28) ($py + $ph + 36) 9.5 (C '#7C8AA0')

# ==================== Dock ====================
$dx = 60; $dy = 806; $dw = 1680; $dh = 370
FillRR $dx $dy $dw $dh 18 (New-Object System.Drawing.SolidBrush((C '#0F1117')))
StrokeRR $dx $dy $dw $dh 18 (New-Object System.Drawing.Pen((C '#232833'), 1))
Txt '② 程序坞（Dock）' ($dx + 28) ($dy + 18) 12.5 $ink $true
Txt '逐像素 Alpha 分层窗口 · 圆角 22 · 1px 内高光描边 · 投影 blur30 / y8' ($dx + 210) ($dy + 21) 9.5 $muted

$barH = 68; $barY = $dy + 158
$iconBase = 44; $gap = 8; $padX = 10
$apps = @(
    @{ c='#6B7BFF'; n='浏览器' },
    @{ c='#34C759'; n='终端' },
    @{ c='#FF9F0A'; n='文件' },
    @{ c='#FF6B6B'; n='音乐' },
    @{ c='#22D3EE'; n='相机' },
    @{ c='#A855F7'; n='邮件' }
)
$hover = 2
$scales = @()
for ($i=0; $i -lt $apps.Count; $i++) {
    $d = [Math]::Abs($i - $hover) / 3.0
    $t = [Math]::Max(0, 1 - $d)
    $scales += [Math]::Round((1 + 0.55 * (3*$t*$t - 2*$t*$t*$t)), 3)
}
$iconsW = 0
for ($i=0; $i -lt $scales.Count; $i++) { $iconsW += $iconBase * $scales[$i] }
$iconsW += $gap * ($scales.Count - 1)
$barW = $padX + $iconBase + $gap + 1 + $gap + $iconsW + $padX
$barX = $dx + 200 + ($dw - 200 - 380 - $barW)/2

$sh1 = New-Object System.Drawing.SolidBrush((C '#000000' 90))
FillRR ($barX-6) ($barY-6) ($barW+12) ($barH+16) 30 $sh1
$sh1.Dispose()

$barRect = [System.Drawing.RectangleF]::new($barX,$barY,$barW,$barH)
$barBr = New-Object System.Drawing.Drawing2D.LinearGradientBrush($barRect, (C '#2A2E3A' 246), (C '#191C24' 250), 90)
FillRR $barX $barY $barW $barH 22 $barBr
$barBr.Dispose()
StrokeRR $barX $barY $barW $barH 22 (New-Object System.Drawing.Pen((C '#FFFFFF' 55), 1))

# 启动台按钮：3×3 圆点
$bx = $barX + $padX
$by = $barY + ($barH - $iconBase)/2 + 1
FillRR $bx $by $iconBase $iconBase 10 (New-Object System.Drawing.SolidBrush((C '#FFFFFF' 16)))
$dotB = New-Object System.Drawing.SolidBrush((C '#E2E6EE'))
for ($r2=0; $r2 -lt 3; $r2++) {
    for ($c2=0; $c2 -lt 3; $c2++) {
        $g.FillEllipse($dotB, ($bx + 9 + $c2*10), ($by + 9 + $r2*10), 7, 7)
    }
}
$dotB.Dispose()

$sepX = $barX + $padX + $iconBase + $gap
$g.DrawLine((New-Object System.Drawing.Pen((C '#FFFFFF' 42), 1)), $sepX, ($barY + 18), $sepX, ($barY + $barH - 18))

$cur = $sepX + $gap
for ($i=0; $i -lt $apps.Count; $i++) {
    $s = $iconBase * $scales[$i]
    $ix = $cur + ($iconBase - $s)/2
    $iy = $barY + $barH - 12 - $s
    IconTile $ix $iy $s $apps[$i].c ([string][char](65+$i)) ([single]([Math]::Max(8, 10*$scales[$i])))
    if ($i -lt 3) {
        $g.FillEllipse((New-Object System.Drawing.SolidBrush((C '#E8ECF3'))), ($cur + $iconBase/2 - 2), ($barY + $barH - 9), 4, 4)
    }
    $cur += $s + $gap
}

# 悬停应用名
$hx = 0; $hc = $sepX + $gap
for ($i=0; $i -lt $apps.Count; $i++) {
    $s = $iconBase * $scales[$i]
    if ($i -eq $hover) { $hx = $hc + $iconBase/2 }
    $hc += $s + $gap
}
$lw = 92; $lh = 26
FillRR ($hx - $lw/2) ($barY - $lh - 10) $lw $lh 8 (New-Object System.Drawing.SolidBrush((C '#000000' 175)))
StrokeRR ($hx - $lw/2) ($barY - $lh - 10) $lw $lh 8 (New-Object System.Drawing.Pen((C '#FFFFFF' 34), 1))
Txt $apps[$hover].n $hx ($barY - $lh - 3) 9.5 $ink $false 'Center'

# 栏高标注（左）
$vx = $barX - 30
$g.DrawLine($dimPen, $vx, $barY, ($vx - 6), $barY)
$g.DrawLine($dimPen, $vx, ($barY + $barH), ($vx - 6), ($barY + $barH))
$g.DrawLine($dimPen, ($vx - 3), $barY, ($vx - 3), ($barY + $barH))
Txt '栏高 68' ($vx - 10) ($barY + $barH/2 - 8) 9 $muted $false 'Far'

# 栏宽标注（下）
$g.DrawLine($dimPen, ($barX + 12), ($barY + $barH + 16), ($barX + 12), ($barY + $barH + 40))
$g.DrawLine($dimPen, ($barX + $barW - 12), ($barY + $barH + 16), ($barX + $barW - 12), ($barY + $barH + 40))
$g.DrawLine($dimPen, ($barX + 12), ($barY + $barH + 30), ($barX + $barW - 12), ($barY + $barH + 30))
Txt '宽 = 内容自适应，上限 工作区宽 × 0.86' ($barX + $barW/2) ($barY + $barH + 36) 9 $muted $false 'Center'

Card ($dx + $dw - 350) ($dy + 54) 322 92 'Dock 令牌' @(
    '图标 44（可调 32–56） · 间距 8 · 圆角 22',
    '内边距 上8 / 下10 / 左右10 · 运行点 4px',
    '距工作区底边 8 · 自动隐藏滑出 220ms'
)

Card ($dx + $dw - 350) ($dy + 158) 322 120 '悬停放大模型' @(
    's = 1 + (Smax−1)·(3t² − 2t³)',
    't = clamp(1 − |xi−xc| / R, 0, 1)',
    'Smax = 1.55，R = 3 × (44+8) = 156',
    '缩放原点锚定图标底部中心',
    '逐帧计算，O(n)，n ≤ 30'
)

Txt '任务栏在底部/顶部/左侧时统一取 MONITORINFO.rcWork 作为贴靠基准；全屏检测命中则整条让位' ($dx + 28) ($dy + $dh - 30) 9.5 (C '#8FA0B8')

Txt 'WinLaunch 设计评审稿 v1.0 · 尺寸单位 DIP（100% DPI 下 1 DIP = 1 px）· 色值见源码中的配色令牌' 60 ($H - 40) 9 (C '#5F6B7D')

$outDir = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..\docs\images'))
New-Item -ItemType Directory -Force -Path $outDir | Out-Null
$bmp.Save((Join-Path $outDir 'layout-spec.png'), [System.Drawing.Imaging.ImageFormat]::Png)
$g.Dispose(); $bmp.Dispose()
Write-Output ('saved: ' + (Join-Path $outDir 'layout-spec.png'))
