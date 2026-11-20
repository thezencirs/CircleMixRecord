# Circle Mix Record — Microsoft Store (MSIX) Paketleme Scripti
# Kullanım (Partner Center açıldıktan sonra):
#   powershell -ExecutionPolicy Bypass -File build_msix.ps1 -IdentityName "12345thezencirs.CircleMixRecord" -Publisher "CN=XXXXXXXX-XXXX-XXXX-XXXX-XXXXXXXXXXXX" -PublisherDisplayName "thezencirs"
param(
    [string]$IdentityName = "thezencirs.CircleMixRecord",
    [string]$Publisher = "CN=thezencirs",
    [string]$PublisherDisplayName = "thezencirs",
    [string]$DisplayName = "Circle Mix Record"
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
$root = $PSScriptRoot

$ver3 = (Get-Content "$root\version.txt" -Raw).Trim()
if ($ver3 -notmatch '^\d+\.\d+\.\d+$') { throw "version.txt 'MAJOR.MINOR.PATCH' biçiminde olmalı (şu an: '$ver3')" }
$msixVer = "$ver3.0"

$toolsDir = Join-Path $root "obj\msix_tools"
New-Item -ItemType Directory -Force -Path $toolsDir | Out-Null

# 1) MakeAppx.exe (Microsoft.Windows.SDK.BuildTools) kontrolü
$makeappx = Get-ChildItem -Path (Join-Path $toolsDir "sdk\bin") -Recurse -Filter "makeappx.exe" -ErrorAction SilentlyContinue |
    Where-Object { $_.FullName -match 'x64' } | Select-Object -First 1
if (-not $makeappx) {
    Write-Host "[1/5] Microsoft.Windows.SDK.BuildTools indiriliyor..." -ForegroundColor Cyan
    $nupkg = Join-Path $toolsDir "sdkbuildtools.zip"
    & curl.exe -L -s "https://www.nuget.org/api/v2/package/Microsoft.Windows.SDK.BuildTools" -o $nupkg
    Expand-Archive -Path $nupkg -DestinationPath (Join-Path $toolsDir "sdk") -Force
    $makeappx = Get-ChildItem -Path (Join-Path $toolsDir "sdk\bin") -Recurse -Filter "makeappx.exe" |
        Where-Object { $_.FullName -match 'x64' } | Select-Object -First 1
}

# 2) LGPL v3 Shared FFmpeg kontrolü
$ffExtract = Join-Path $toolsDir "ffmpeg_lgpl"
$ffExe = Get-ChildItem -Path $ffExtract -Recurse -Filter "ffmpeg.exe" -ErrorAction SilentlyContinue | Select-Object -First 1
if (-not $ffExe) {
    Write-Host "[2/5] FFmpeg LGPL (win64-lgpl-shared) indiriliyor..." -ForegroundColor Cyan
    $ffZip = Join-Path $toolsDir "ffmpeg-lgpl.zip"
    & curl.exe -L -s "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/ffmpeg-master-latest-win64-lgpl-shared.zip" -o $ffZip
    Expand-Archive -Path $ffZip -DestinationPath $ffExtract -Force
    $ffExe = Get-ChildItem -Path $ffExtract -Recurse -Filter "ffmpeg.exe" | Select-Object -First 1
}
$ffRoot = $ffExe.Directory.Parent.FullName

# 3) Resmi Circle 3 Renk (#261A66, #EF5F18, #FFFFFF) Mağaza PNG İkon Seti üretimi
Write-Host "[3/5] Microsoft Store görsel ikon seti üretiliyor..." -ForegroundColor Cyan
Add-Type -AssemblyName System.Drawing
$assetsDir = Join-Path $root "packaging\Assets"
$storeAssetsDir = Join-Path $root "dist\Store_Assets"
New-Item -ItemType Directory -Force -Path $assetsDir, $storeAssetsDir | Out-Null

function New-CircleTile([string]$OutPath, [int]$W, [int]$H, [bool]$WideBanner) {
    $bmp = New-Object System.Drawing.Bitmap($W, $H, [System.Drawing.Imaging.PixelFormat]::Format32bppArgb)
    $g = [System.Drawing.Graphics]::FromImage($bmp)
    $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias
    $g.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $g.TextRenderingHint = [System.Drawing.Text.TextRenderingHint]::AntiAliasGridFit

    $bgColor = [System.Drawing.Color]::FromArgb(255, 38, 26, 102)     # #261A66 Scarlet Bikini
    $orange  = [System.Drawing.Color]::FromArgb(255, 239, 95, 24)     # #EF5F18 Orange
    $white   = [System.Drawing.Color]::FromArgb(255, 255, 255, 255)   # #FFFFFF White

    $g.Clear($bgColor)

    $minDim = [Math]::Min($W, $H)
    if ($WideBanner) {
        $ringR = [float]($H * 0.56)
        $cx = [float]($H * 0.55)
        $cy = [float]($H * 0.50)
        $stroke = [Math]::Max(3.0, [float]($H * 0.085))
        $penWhite = New-Object System.Drawing.Pen($white, $stroke)
        $g.DrawEllipse($penWhite, $cx - $ringR/2, $cy - $ringR/2, $ringR, $ringR)

        $dotR = [float]($ringR * 0.42)
        $brushOrange = New-Object System.Drawing.SolidBrush($orange)
        $g.FillEllipse($brushOrange, $cx - $dotR/2, $cy - $dotR/2, $dotR, $dotR)

        $fontTitle = New-Object System.Drawing.Font("Segoe UI", [float]($H * 0.16), [System.Drawing.FontStyle]::Bold, [System.Drawing.GraphicsUnit]::Pixel)
        $fontSub   = New-Object System.Drawing.Font("Segoe UI", [float]($H * 0.095), [System.Drawing.FontStyle]::Regular, [System.Drawing.GraphicsUnit]::Pixel)
        $brushWhite = New-Object System.Drawing.SolidBrush($white)
        $g.DrawString("CIRCLE MIX", $fontTitle, $brushWhite, [float]($H * 0.98), [float]($H * 0.27))
        $g.DrawString("RECORD • EDIT • MEET", $fontSub, $brushOrange, [float]($H * 0.99), [float]($H * 0.53))

        $fontTitle.Dispose(); $fontSub.Dispose(); $brushWhite.Dispose(); $brushOrange.Dispose(); $penWhite.Dispose()
    } else {
        $ringR = [float]($minDim * 0.62)
        $cx = [float]($W / 2.0)
        $cy = [float]($H / 2.0)
        $stroke = [Math]::Max(2.2, [float]($minDim * 0.095))
        $penWhite = New-Object System.Drawing.Pen($white, $stroke)
        $g.DrawEllipse($penWhite, $cx - $ringR/2, $cy - $ringR/2, $ringR, $ringR)

        $dotR = [float]($ringR * 0.44)
        $brushOrange = New-Object System.Drawing.SolidBrush($orange)
        $g.FillEllipse($brushOrange, $cx - $dotR/2, $cy - $dotR/2, $dotR, $dotR)

        $penWhite.Dispose(); $brushOrange.Dispose()
    }

    $g.Dispose()
    $bmp.Save($OutPath, [System.Drawing.Imaging.ImageFormat]::Png)
    $bmp.Dispose()
}

New-CircleTile (Join-Path $assetsDir "StoreLogo.png")        50  50  $false
New-CircleTile (Join-Path $assetsDir "Square44x44Logo.png")  44  44  $false
New-CircleTile (Join-Path $assetsDir "Square150x150Logo.png") 150 150 $false
New-CircleTile (Join-Path $assetsDir "Wide310x150Logo.png")  310 150 $true
New-CircleTile (Join-Path $assetsDir "LargeTile.png")        310 310 $false
New-CircleTile (Join-Path $storeAssetsDir "Store_Logo_300x300.png") 300 300 $false
New-CircleTile (Join-Path $storeAssetsDir "Store_Poster_720x1080.png") 720 1080 $false
New-CircleTile (Join-Path $storeAssetsDir "Store_Banner_1920x1080.png") 1920 1080 $true

# 4) Self-Contained Release Derleme (.NET 10 içeride — müşteri PC'sinde ek kurulum istemez)
Write-Host "[4/5] CircleMixRecord v$ver3 (Self-Contained Win-x64) derleniyor..." -ForegroundColor Cyan
$stageDir = Join-Path $root "obj\msix_stage"
Remove-Item $stageDir -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force -Path $stageDir | Out-Null

dotnet publish "$root\CircleMixRecord.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:DebugType=None -p:DebugSymbols=false -p:Version=$ver3 `
    -o $stageDir
if ($LASTEXITCODE -ne 0) { throw "dotnet publish başarısız oldu (kod $LASTEXITCODE)" }

# FFmpeg LGPL ikili dosyaları + Lisans bildirimlerini stage içine kopyala
$stageBin = Join-Path $stageDir "bin"
New-Item -ItemType Directory -Force -Path $stageBin | Out-Null
Copy-Item -Path (Join-Path $ffRoot "bin\ffmpeg.exe") -Destination $stageBin -Force
Copy-Item -Path (Join-Path $ffRoot "bin\*.dll") -Destination $stageBin -Force
if (Test-Path (Join-Path $ffRoot "LICENSE.txt")) {
    Copy-Item -Path (Join-Path $ffRoot "LICENSE.txt") -Destination (Join-Path $stageBin "FFMPEG_LICENSE.txt") -Force
}
Copy-Item -Path "$root\THIRD_PARTY_NOTICES.txt" -Destination $stageDir -Force
Copy-Item -Path "$root\PRIVACY.md" -Destination $stageDir -Force

# Assets ve AppxManifest.xml hazırla
$stageAssets = Join-Path $stageDir "Assets"
New-Item -ItemType Directory -Force -Path $stageAssets | Out-Null
Copy-Item -Path "$assetsDir\*.png" -Destination $stageAssets -Force

$manifestTpl = [IO.File]::ReadAllText("$root\packaging\Package.appxmanifest.template", [Text.Encoding]::UTF8)
$manifestXml = $manifestTpl.
    Replace("__IDENTITY_NAME__", [Security.SecurityElement]::Escape($IdentityName)).
    Replace("__PUBLISHER__", [Security.SecurityElement]::Escape($Publisher)).
    Replace("__VERSION__", $msixVer).
    Replace("__DISPLAY_NAME__", [Security.SecurityElement]::Escape($DisplayName)).
    Replace("__PUBLISHER_DISPLAY_NAME__", [Security.SecurityElement]::Escape($PublisherDisplayName))
[IO.File]::WriteAllText((Join-Path $stageDir "AppxManifest.xml"), $manifestXml, (New-Object System.Text.UTF8Encoding($false)))

# 5) Resmi MakeAppx.exe ile .msix paketini oluştur
$distDir = Join-Path $root "dist"
New-Item -ItemType Directory -Force -Path $distDir | Out-Null
$outMsix = Join-Path $distDir "CircleMixRecord_${msixVer}_x64.msix"
if (Test-Path $outMsix) { Remove-Item $outMsix -Force }

Write-Host "[5/5] MSIX paketi oluşturuluyor: $outMsix" -ForegroundColor Cyan
& $makeappx.FullName pack /d $stageDir /p $outMsix /nv /o
if ($LASTEXITCODE -ne 0) { throw "MakeAppx başarısız oldu (kod $LASTEXITCODE)" }

$sizeMb = [Math]::Round((Get-Item $outMsix).Length / 1MB, 1)
Write-Host "`n✅ HAZIR: $outMsix ($sizeMb MB)" -ForegroundColor Green
Write-Host "   Identity : $IdentityName"
Write-Host "   Publisher: $Publisher"
Write-Host "   Version  : $msixVer"
