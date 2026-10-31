@echo off

rem Crea instalador\MayhemAdvisor-Instalador-<version>.exe para instalar Mayhem Advisor en otro PC.

rem 1) Publica el programa con .NET incluido (el otro PC no necesita instalar .NET).

rem 2) Inno Setup junta eso, el overlay de Electron y los datos en un solo .exe.

setlocal

cd /d "%~dp0"



set "ISCC=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"

if not exist "%ISCC%" set "ISCC=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"

if not exist "%ISCC%" (

  echo Falta Inno Setup. Instalalo con:  winget install JRSoftware.InnoSetup

  exit /b 1

)



rem La version está en un solo sitio: <Version> de src\Overlay\MayhemAdvisor.Overlay.csproj.
for /f "usebackq delims=" %%v in (`powershell -NoProfile -Command "([xml](Get-Content '..\src\Overlay\MayhemAdvisor.Overlay.csproj')).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1"`) do set "VERSION=%%v"
if not defined VERSION (
  echo No encuentro la version en el .csproj
  exit /b 1
)
echo Version %VERSION%

echo [1/2] Publicando el programa...

if exist build\app rmdir /s /q build\app

dotnet publish ..\src\Overlay -c Release -r win-x64 --self-contained -o build\app -nologo -v q || exit /b 1



echo [2/2] Creando el instalador...

"%ISCC%" /Q /DAppVersion=%VERSION% MayhemAdvisor.iss || exit /b 1



echo Listo: %~dp0MayhemAdvisor-Instalador-%VERSION%.exe

