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
foreach ($entry in @(@('X-MIDI','device_XMIDI.py'), @('X-MIDI Return','device_XMIDI_Return.py'))) {
    $destination = Join-Path $UserDataFolder ('Settings\Hardware\' + $entry[0])
    New-Item -ItemType Directory -Path $destination -Force | Out-Null
    $target = Join-Path $destination $entry[1]
    if (Test-Path -LiteralPath $target) {
        Copy-Item -LiteralPath $target -Destination ($target + '.backup-' + (Get-Date -Format yyyyMMddHHmmssfff))
    }
    Copy-Item -LiteralPath (Join-Path $PSScriptRoot ('X-MIDI\' + $entry[1])) -Destination $target -Force
}
Write-Output 'Scripts instalados. FL: Input X-MIDI + script X-MIDI, Port 10. Input X-MIDI Return + script X-MIDI Return, Port 11; Output X-MIDI Return, Port 11. Ative Options > Enable MIDI output. Consulte FL-STUDIO.md.'
