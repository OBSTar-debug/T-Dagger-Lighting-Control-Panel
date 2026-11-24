@echo off
rem Builds "T-Dagger Lighting Control Panel.exe" with the C# compiler that ships with Windows (.NET Framework 4.x).
rem No Visual Studio, SDK or downloads needed.
setlocal
cd /d "%~dp0"
set "CSC=%WINDIR%\Microsoft.NET\Framework64\v4.0.30319\csc.exe"
if not exist "%CSC%" set "CSC=%WINDIR%\Microsoft.NET\Framework\v4.0.30319\csc.exe"
if not exist "%CSC%" (
  echo Could not find csc.exe from .NET Framework 4. It is part of Windows 10 and 11.
  exit /b 1
)
"%CSC%" /nologo /target:winexe /optimize+ /win32icon:assets\tdagger.ico "/out:T-Dagger Lighting Control Panel.exe" src\ControlPanel.cs
if errorlevel 1 exit /b 1
echo Built "T-Dagger Lighting Control Panel.exe"
