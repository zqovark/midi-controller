param([string]$UserDataFolder)
$ErrorActionPreference = 'Stop'
if (-not $UserDataFolder) {
    $shared = Get-ItemProperty 'HKCU:\Software\Image-Line\Shared\Paths' -ErrorAction SilentlyContinue
    if ($shared -and $shared.'Shared data') {
        $UserDataFolder = Join-Path $shared.'Shared data' 'FL Studio'
    } else {
        $UserDataFolder = Join-Path ([Environment]::GetFolderPath('MyDocuments')) 'Image-Line\FL Studio'
    }
}
$destination = Join-Path $UserDataFolder 'Settings\Hardware\X-MIDI'
New-Item -ItemType Directory -Path $destination -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'X-MIDI\device_XMIDI.py') -Destination (Join-Path $destination 'device_XMIDI.py') -Force
Write-Output "Script instalado em $destination. No FL: F10 > MIDI > Input X-MIDI > Controller type: X-MIDI (user)."
