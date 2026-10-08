# GitHub 릴리스용 zip 만들기
#   1) .\build.ps1                 → dist\ (exe + ffmpeg)
#   2) .\docs\guide\make-pdf.ps1   → 사용 가이드 PDF
#   3) .\package.ps1               → release\TimeLapseConverter-v<버전>-win-x64.zip
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot

[xml]$proj = Get-Content (Join-Path $root "TimelapseConverter.csproj")
$version = ($proj.Project.PropertyGroup | Where-Object { $_.Version } | Select-Object -First 1).Version
$name = "TimeLapseConverter-v$version"

$dist = Join-Path $root "dist"
$pdf = Join-Path $root "docs\guide\TimeLapse Converter 사용 가이드.pdf"
foreach ($required in "$dist\TimelapseConverter.exe", "$dist\ffmpeg\ffmpeg.exe", "$dist\ffmpeg\ffprobe.exe", $pdf) {
    if (-not (Test-Path $required)) { throw "없는 파일: $required" }
}

$releaseDir = Join-Path $root "release"
$stage = Join-Path $releaseDir $name
$zip = Join-Path $releaseDir "$name-win-x64.zip"
if (Test-Path $stage) { Remove-Item $stage -Recurse -Force }
if (Test-Path $zip) { Remove-Item $zip -Force }
New-Item -ItemType Directory -Force $stage | Out-Null

Copy-Item "$dist\TimelapseConverter.exe" $stage
Copy-Item "$dist\ffmpeg" $stage -Recurse
Copy-Item $pdf $stage

# 한글 파일명이 깨지지 않도록 UTF-8, 경로 구분자는 zip 표준인 '/'로 압축
# (Windows PowerShell 5.1의 CreateFromDirectory는 '\'로 저장해서 다른 압축 프로그램에서 폴더가 깨질 수 있음)
Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
$stream = [System.IO.File]::Open($zip, [System.IO.FileMode]::CreateNew)
$archive = New-Object System.IO.Compression.ZipArchive($stream, [System.IO.Compression.ZipArchiveMode]::Create, $false, [System.Text.Encoding]::UTF8)
try {
    foreach ($file in Get-ChildItem $stage -Recurse -File) {
        $entry = "$name/" + $file.FullName.Substring($stage.Length + 1).Replace('\', '/')
        [System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entry,
            [System.IO.Compression.CompressionLevel]::Optimal) | Out-Null
    }
}
finally {
    $archive.Dispose()
    $stream.Dispose()
}

Write-Host ("완료: {0} ({1:N0} MB)" -f $zip, ((Get-Item $zip).Length / 1MB))
