# Installs T-Dagger Lighting Control Panel for the current user (no admin rights needed).
#  - copies the app to %LOCALAPPDATA%\Programs\T-Dagger Lighting Control Panel
#  - adds Desktop and Start menu shortcuts
#  - turns on "Restore at sign-in" (re-applies your last setting when you sign in; you can switch it off in the app)
$ErrorActionPreference = 'Stop'
$name = 'T-Dagger Lighting Control Panel'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root "$name.exe"
if (-not (Test-Path $exe)) { $exe = Join-Path $PSScriptRoot "$name.exe" }
if (-not (Test-Path $exe)) { throw "Could not find '$name.exe' next to the installer. Extract the whole zip first." }

$dest = Join-Path $env:LOCALAPPDATA "Programs\$name"
$target = Join-Path $dest "$name.exe"

# Close a running copy so the file can be replaced.
Get-Process -Name $name -ErrorAction SilentlyContinue | ForEach-Object {
    $null = $_.CloseMainWindow()
    if (-not $_.WaitForExit(5000)) { $_.Kill() }
}

New-Item -ItemType Directory -Force -Path $dest | Out-Null
Copy-Item $exe $target -Force
Unblock-File $target -ErrorAction SilentlyContinue

$shell = New-Object -ComObject WScript.Shell
foreach ($dir in @([Environment]::GetFolderPath('Desktop'), (Join-Path ([Environment]::GetFolderPath('StartMenu')) 'Programs'))) {
    $lnk = $shell.CreateShortcut((Join-Path $dir "$name.lnk"))
    $lnk.TargetPath = $target; $lnk.WorkingDirectory = $dest; $lnk.IconLocation = "$target,0"
    $lnk.Description = 'Lighting control for the T-Dagger Bora Lite keyboard'
    $lnk.Save()
}

$run = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
Set-ItemProperty -Path $run -Name 'TDaggerLightingControlPanel' -Value "`"$target`" --apply"

Write-Host "Installed to $dest"
Start-Process $target
