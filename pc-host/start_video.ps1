param(
    [int]$Output = 0,
    [ValidateSet(30,60)][int]$Fps = 60,
    [ValidateRange(5,65)][int]$Bitrate = 50
)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$python = Join-Path $root '.venv/Scripts/python.exe'
$ffmpeg = Join-Path $root '.tools/ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe'
if (!(Test-Path -LiteralPath $ffmpeg)) { throw 'Run tools/install_ffmpeg.ps1 first.' }
& $python (Join-Path $PSScriptRoot 'video_host.py') --ffmpeg $ffmpeg --output $Output --fps $Fps --bitrate $Bitrate
exit $LASTEXITCODE
