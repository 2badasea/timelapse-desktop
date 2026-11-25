# TimeLapse Converter 배포용 빌드
#  - dist\TimelapseConverter.exe : .NET 설치 없이 실행되는 단일 exe
#  - dist\ffmpeg\                 : 다른 PC에서도 바로 쓰도록 FFmpeg 동봉 (-FfmpegDir 로 위치 지정)
#  - -OutDir 로 dist 대신 다른 폴더에 만들 수 있음 (dist의 프로그램을 켜 둔 채 빌드할 때)
param(
    [string]$FfmpegDir = "C:\ffmpeg",
    [string]$OutDir = ""
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = if ($OutDir) { [System.IO.Path]::GetFullPath($OutDir) } else { Join-Path $root "dist" }

# 이 폴더의 프로그램이 실행 중이면 일부 파일만 지워진 채 멈추므로 먼저 확인
$running = Get-Process TimelapseConverter -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -and $_.Path.StartsWith($dist.TrimEnd('\') + '\', [StringComparison]::OrdinalIgnoreCase) }
if ($running) { throw "$dist 의 TimelapseConverter.exe가 실행 중입니다. 프로그램을 닫거나 -OutDir 로 다른 폴더를 지정하세요." }

if (Test-Path $dist) { Remove-Item $dist -Recurse -Force }

dotnet publish (Join-Path $root "TimelapseConverter.csproj") -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true `
    -o $dist
if ($LASTEXITCODE -ne 0) { throw "빌드 실패" }

$bin = Join-Path $FfmpegDir "bin"
if ((Test-Path "$bin\ffmpeg.exe") -and (Test-Path "$bin\ffprobe.exe")) {
    $target = Join-Path $dist "ffmpeg"
    New-Item -ItemType Directory -Force $target | Out-Null
    Copy-Item "$bin\ffmpeg.exe", "$bin\ffprobe.exe" $target
    if (Test-Path "$FfmpegDir\LICENSE.txt") { Copy-Item "$FfmpegDir\LICENSE.txt" (Join-Path $target "FFmpeg-LICENSE.txt") }
    Write-Host "FFmpeg 동봉 완료: $target"
} else {
    Write-Warning "FFmpeg를 찾지 못해 동봉하지 않았습니다 ($bin). 실행 PC의 PATH 또는 C:\ffmpeg\bin 에 있어야 합니다."
}

Write-Host ""
Write-Host "완료: $dist"
