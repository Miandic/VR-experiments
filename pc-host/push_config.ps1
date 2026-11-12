param(
    [string]$Adb = 'C:\Program Files\Meta VR Developer Hub\resources\bin\adb.exe',
    [string]$Serial = ''
)
$ErrorActionPreference = 'Stop'
$config = Join-Path $PSScriptRoot 'private\connection.json'
if (!(Test-Path -LiteralPath $config)) { throw 'Run setup_host.py first.' }
$adbArgs = @()
if ($Serial) { $adbArgs += @('-s', $Serial) }
& $Adb @adbArgs push $config '/sdcard/Android/data/com.miandic.questdesktop/files/connection.json'
if ($LASTEXITCODE -ne 0) { throw 'ADB copy failed. Install and launch Quest Desktop first, and authorize USB debugging.' }
Write-Host 'Connection config copied. Press Reload config in the headset.'
