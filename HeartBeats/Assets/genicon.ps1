Add-Type -AssemblyName System.Drawing

$dir = Split-Path -Parent $MyInvocation.MyCommand.Definition

$size = 64
$bmp = New-Object Drawing.Bitmap $size, $size
$g = [Drawing.Graphics]::FromImage($bmp)
$g.SmoothingMode = 'AntiAlias'
$g.TextRenderingHint = 'AntiAliasGridFit'
$g.Clear([Drawing.Color]::FromArgb(0,0,0,0))

# 深色圆形背景（与 HUD 窗口风格一致）
$bgDark = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,28,31,34))
$g.FillEllipse($bgDark, 2, 2, $size-4, $size-4)

# 边框
$borderPen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(80,255,255,255)), 2
$g.DrawEllipse($borderPen, 3, 3, $size-6, $size-6)

# 心形图标（红色渐变效果）
$heartPath = New-Object Drawing.Drawing2D.GraphicsPath
# 绘制心形：两个圆弧 + 底部尖角
$cx = $size / 2
$cy = $size / 2 - 2
$heartSize = 24
# 左半圆
$heartPath.AddArc($cx - $heartSize/2 - 1, $cy - $heartSize/4, $heartSize/2 + 2, $heartSize/2, 135, 225)
# 右半圆
$heartPath.AddArc($cx - 1, $cy - $heartSize/4, $heartSize/2 + 2, $heartSize/2, 180, 225)
$heartPath.CloseFigure()

$heartBrush = New-Object Drawing.SolidBrush ([Drawing.Color]::FromArgb(255,220,38,38))
$g.FillPath($heartBrush, $heartPath)

# 心跳波形线（白色）
$pulsePen = New-Object Drawing.Pen ([Drawing.Color]::FromArgb(255,255,255,255)), 2.5
$pulsePen.StartCap = [Drawing.Drawing2D.LineCap]::Round
$pulsePen.EndCap = [Drawing.Drawing2D.LineCap]::Round
$pulsePen.LineJoin = [Drawing.Drawing2D.LineJoin]::Round

$pulseY = $cy + 2
$pulsePoints = @(
    [Drawing.PointF]::new(14, $pulseY),
    [Drawing.PointF]::new(22, $pulseY),
    [Drawing.PointF]::new(26, $pulseY - 8),
    [Drawing.PointF]::new(32, $pulseY + 10),
    [Drawing.PointF]::new(38, $pulseY - 6),
    [Drawing.PointF]::new(42, $pulseY),
    [Drawing.PointF]::new(50, $pulseY)
)
$g.DrawLines($pulsePen, $pulsePoints)

$g.Dispose()

$hicon = $bmp.GetHicon()
$ico = [Drawing.Icon]::FromHandle($hicon)
$outPath = Join-Path $dir 'app.ico'
$fs = [IO.File]::Open($outPath,'Create')
$ico.Save($fs)
$fs.Dispose()

Write-Host "Icon generated: $outPath"
