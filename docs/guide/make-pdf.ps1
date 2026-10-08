# guide.html → PDF (Microsoft Edge headless 인쇄)
# 화면 캡처(shots\*.png)는 TimelapseConverter.exe --snapshot 으로 만든다 (README 참고)
param(
    [string]$Out = (Join-Path $PSScriptRoot "TimeLapse Converter 사용 가이드.pdf")
)
$ErrorActionPreference = "Stop"

$edge = @("${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe",
          "$env:ProgramFiles\Microsoft\Edge\Application\msedge.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $edge) { throw "Microsoft Edge를 찾을 수 없습니다." }

$html = Join-Path $PSScriptRoot "guide.html"
$profile = Join-Path $env:TEMP "timelapse-guide-edge"
if (Test-Path $Out) { Remove-Item $Out }

$p = Start-Process $edge -PassThru -Wait -ArgumentList @(
    "--headless=new", "--disable-gpu", "--no-first-run", "--user-data-dir=`"$profile`"",
    "--no-pdf-header-footer", "--virtual-time-budget=10000",
    "--print-to-pdf=`"$Out`"", "`"$(([Uri]$html).AbsoluteUri)`"")

if (-not (Test-Path $Out)) { throw "PDF 생성 실패" }
Write-Host "PDF: $Out"
