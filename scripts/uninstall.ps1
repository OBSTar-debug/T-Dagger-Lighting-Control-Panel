# Removes T-Dagger Lighting Control Panel for the current user.
# Your keyboard keeps whatever lighting it is set to; nothing is sent to it.
param([switch]$KeepSettings)
$name = 'T-Dagger Lighting Control Panel'

Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
    $null = $_.CloseMainWindow()
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}

Remove-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'TDaggerLightingControlPanel' -ErrorAction SilentlyContinue
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))) {
    Remove-Item (Join-Path $dir "$name.lnk") -ErrorAction SilentlyContinue
}
Remove-Item (Join-Path $env:LOCALAPPDATA "Programs\$name") -Recurse -Force -ErrorAction SilentlyContinue
if (-not $KeepSettings) { Remove-Item (Join-Path $env:APPDATA $name) -Recurse -Force -ErrorAction SilentlyContinue }

Write-Host "$name was removed."
