$ErrorActionPreference = 'Stop'
# Portable toolchain; does not edit PATH or install a service/driver.
$root = Split-Path -Parent $PSScriptRoot
$directory = Join-Path $root '.tools'
$archive = Join-Path $directory 'ffmpeg-9.0.2-essentials.zip'
$expected = '60F467265B1E312373DBCD92200C2618A74850F98D3D078E94296BB3FA2047BA'
New-Item -ItemType Directory -Force -Path $directory | Out-Null
Invoke-WebRequest 'https://www.gyan.dev/ffmpeg/builds/packages/ffmpeg-9.0.2-essentials_build.zip' -OutFile $archive
if ((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ne $expected) {
    throw 'FFmpeg archive hash differs from the tested build. Review the release before updating the pin.'
}
Expand-Archive -LiteralPath $archive -DestinationPath $directory -Force
Write-Output (Join-Path $directory 'ffmpeg-9.0.2-essentials_build/bin/ffmpeg.exe')
