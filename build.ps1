# CircleMixRecord'u derler ve yayın paketini (zip + SHA256SUMS.txt) dist\ klasörüne üretir.
# Geliştirici: thezencirs (https://github.com/thezencirs)
# Kullanım: powershell -ExecutionPolicy Bypass -File build.ps1
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

$version = (Get-Content "$root\version.txt" -Raw).Trim()
if ($version -notmatch '^\d+\.\d+\.\d+$') { throw "version.txt 'MAJOR.MINOR.PATCH' biçiminde olmalı (şu an: '$version')" }

$dist = Join-Path $root "dist"
$pubTemp = Join-Path $root "obj\publish_stage"
Remove-Item $dist, $pubTemp -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $dist, $pubTemp | Out-Null

Write-Host "Derleniyor: CircleMixRecord v$version (by thezencirs)" -ForegroundColor Cyan
dotnet publish "$root\CircleMixRecord.csproj" -c Release -r win-x64 --self-contained false `
    -p:PublishSingleFile=true -p:DebugType=None -p:DebugSymbols=false -p:Version=$version `
    -o $pubTemp
if ($LASTEXITCODE -ne 0) { throw "Derleme başarısız (kod $LASTEXITCODE)" }

Copy-Item "$pubTemp\CircleMixRecord.exe" "$dist\CircleMixRecord.exe" -Force

$stage = Join-Path $root "obj\zip_stage"
Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory $stage | Out-Null
Copy-Item "$dist\CircleMixRecord.exe", "$root\LICENSE", "$root\README.md" $stage

$zipName = "CircleMixRecord-v$version-win-x64.zip"
Compress-Archive -Path "$stage\*" -DestinationPath (Join-Path $dist $zipName)

$utf8 = New-Object System.Text.UTF8Encoding($false)
$sums = ""
foreach ($f in @($zipName, "CircleMixRecord.exe")) {
    $h = (Get-FileHash (Join-Path $dist $f) -Algorithm SHA256).Hash.ToLowerInvariant()
    $sums += "$h  $f`n"
}
[IO.File]::WriteAllText((Join-Path $dist "SHA256SUMS.txt"), $sums, $utf8)

$cl = [IO.File]::ReadAllText("$root\CHANGELOG.md", [Text.Encoding]::UTF8)
$m = [regex]::Match($cl, "(?ms)^## \[$([regex]::Escape($version))\][^\r\n]*\r?\n(.*?)(?=^## |\z)")
$notes = if ($m.Success) { $m.Groups[1].Value.Trim() } else { "CircleMixRecord v$version by thezencirs" }
[IO.File]::WriteAllText((Join-Path $dist "RELEASE_NOTES.md"), $notes + "`n", $utf8)

Write-Host "Hazır:" -ForegroundColor Green
Get-ChildItem $dist | Select-Object Name, @{n = "KB"; e = { [math]::Round($_.Length / 1KB, 1) } } | Format-Table -AutoSize
