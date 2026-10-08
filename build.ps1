# TimeLapse Converter 배포용 빌드
#  - dist\TimelapseConverter.exe : .NET 설치 없이 실행되는 단일 exe
#  - dist\ffmpeg\                 : 다른 PC에서도 바로 쓰도록 FFmpeg 동봉 (-FfmpegDir 로 위치 지정)
param(
    [string]$FfmpegDir = "C:\ffmpeg"
)
$ErrorActionPreference = "Stop"
$root = $PSScriptRoot
$dist = Join-Path $root "dist"

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
