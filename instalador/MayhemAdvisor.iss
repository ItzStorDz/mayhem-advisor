; Instalador de Mayhem Advisor (Inno Setup 6). No lo compiles a mano: usa "Crear instalador.bat",
; que primero publica el programa con .NET incluido (build\app) y luego llama a este script.
; Se instala solo para tu usuario (sin pedir permisos de administrador) en %LOCALAPPDATA%\Programs.

#define AppName "Mayhem Advisor"
; La versión la pasa "Crear instalador.bat" (/DAppVersion=…), leída del .csproj.
#ifndef AppVersion
  #define AppVersion "0.0.0"
#endif
#define AppAuthor "ItzStorDz"
#define AppExe "MayhemAdvisor.exe"
#define Root ".."

[Setup]
; El AppId identifica el programa para actualizarlo o desinstalarlo: no lo cambies nunca.
AppId={{6B0D6C7A-2F43-4E8B-9A51-3C7E0D2B9F14}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppAuthor}
AppCopyright=© 2026 {#AppAuthor}
VersionInfoVersion={#AppVersion}
VersionInfoCompany={#AppAuthor}
VersionInfoCopyright=© 2026 {#AppAuthor}
VersionInfoDescription={#AppName} Setup
PrivilegesRequired=lowest
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir=.
OutputBaseFilename=MayhemAdvisor-Instalador-{#AppVersion}
SetupIconFile={#Root}\src\Overlay\icono.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
; El LEEME de cada idioma va en [Languages].
ShowLanguageDialog=auto
WizardStyle=modern
Compression=lzma2/ultra64
SolidCompression=yes
LZMAUseSeparateProcess=yes
; Cierra Mayhem Advisor (y su overlay) si está abierto al instalar o desinstalar.
CloseApplications=force
RestartApplications=no

[Languages]
; Español o inglés según el idioma de Windows (si no es ninguno de los dos, pregunta).
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"; InfoBeforeFile: "LEEME.txt"
Name: "en"; MessagesFile: "compiler:Default.isl"; InfoBeforeFile: "README-en.txt"

[CustomMessages]
es.Desktop=Crear un acceso directo en el &Escritorio
en.Desktop=Create a &Desktop shortcut
es.Shortcuts=Accesos directos:
en.Shortcuts=Shortcuts:
es.Startup=&Iniciar con Windows (se queda en la bandeja, junto al reloj)
en.Startup=&Start with Windows (stays in the tray, next to the clock)
es.Options=Opciones:
en.Options=Options:
es.Comment=Consejero de aumentos y objetos para ARAM: Caos
en.Comment=Augment and item advisor for ARAM: Mayhem
es.Uninstall=Desinstalar {#AppName}
en.Uninstall=Uninstall {#AppName}
es.OpenNow=Abrir {#AppName} ahora
en.OpenNow=Open {#AppName} now

[Tasks]
Name: "desktopicon"; Description: "{cm:Desktop}"; GroupDescription: "{cm:Shortcuts}"
Name: "startup"; Description: "{cm:Startup}"; GroupDescription: "{cm:Options}"; Flags: unchecked

[Files]
; Programa en C# con .NET incluido (no hace falta instalar .NET), la ventana principal (wwwroot) y los datos (datos).
Source: "build\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
; Overlay de Electron: va en {app}\overlay-electron, que es donde lo busca ElectronBridge.cs.
Source: "{#Root}\overlay-electron\*.js"; DestDir: "{app}\overlay-electron"; Flags: ignoreversion
Source: "{#Root}\overlay-electron\*.html"; DestDir: "{app}\overlay-electron"; Flags: ignoreversion
Source: "{#Root}\overlay-electron\*.css"; DestDir: "{app}\overlay-electron"; Flags: ignoreversion
Source: "{#Root}\overlay-electron\package.json"; DestDir: "{app}\overlay-electron"; Flags: ignoreversion
Source: "{#Root}\overlay-electron\icono.ico"; DestDir: "{app}\overlay-electron"; Flags: ignoreversion
Source: "{#Root}\overlay-electron\node_modules\electron\dist\*"; DestDir: "{app}\overlay-electron\node_modules\electron\dist"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#Root}\overlay-electron\node_modules\electron\package.json"; DestDir: "{app}\overlay-electron\node_modules\electron"; Flags: ignoreversion
Source: "{#Root}\overlay-electron\node_modules\electron-overlay-window\package.json"; DestDir: "{app}\overlay-electron\node_modules\electron-overlay-window"; Flags: ignoreversion
Source: "{#Root}\overlay-electron\node_modules\electron-overlay-window\LICENSE"; DestDir: "{app}\overlay-electron\node_modules\electron-overlay-window"; Flags: ignoreversion
Source: "{#Root}\overlay-electron\node_modules\electron-overlay-window\dist\*"; DestDir: "{app}\overlay-electron\node_modules\electron-overlay-window\dist"; Flags: ignoreversion recursesubdirs
Source: "{#Root}\overlay-electron\node_modules\electron-overlay-window\prebuilds\win32-x64\*"; DestDir: "{app}\overlay-electron\node_modules\electron-overlay-window\prebuilds\win32-x64"; Flags: ignoreversion recursesubdirs
Source: "{#Root}\overlay-electron\node_modules\node-gyp-build\*"; DestDir: "{app}\overlay-electron\node_modules\node-gyp-build"; Flags: ignoreversion recursesubdirs
Source: "{#Root}\overlay-electron\node_modules\throttle-debounce\*"; DestDir: "{app}\overlay-electron\node_modules\throttle-debounce"; Flags: ignoreversion recursesubdirs
Source: "LEEME.txt"; DestDir: "{app}"; Flags: ignoreversion
Source: "README-en.txt"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; El acceso directo de desinstalar tiene el nombre en el idioma del instalador: el del otro idioma sobra.
Type: files; Name: "{autoprograms}\Desinstalar {#AppName}.lnk"; Languages: en
Type: files; Name: "{autoprograms}\Uninstall {#AppName}.lnk"; Languages: es

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; Comment: "{cm:Comment}"
Name: "{autoprograms}\{cm:Uninstall}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Registry]
; La misma entrada que activa la opción "Iniciar con Windows" del menú de la bandeja (WindowsStartup.cs).
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "MayhemAdvisor"; ValueData: """{app}\{#AppExe}"""; Tasks: startup; Flags: uninsdeletevalue

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:OpenNow}"; Flags: nowait postinstall skipifsilent

[UninstallRun]
; Cierra el programa y su overlay antes de borrar los archivos.
Filename: "{sys}\taskkill.exe"; Parameters: "/F /IM {#AppExe}"; Flags: runhidden; RunOnceId: "CerrarApp"
Filename: "{sys}\taskkill.exe"; Parameters: "/F /FI ""IMAGENAME eq electron.exe"" /FI ""WINDOWTITLE eq Mayhem*"""; Flags: runhidden; RunOnceId: "CerrarOverlay"

[Code]
// Si se activó "Iniciar con Windows" desde la bandeja (no desde el instalador), también se quita al desinstalar.
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
begin
  if CurUninstallStep = usPostUninstall then
    RegDeleteValue(HKEY_CURRENT_USER, 'Software\Microsoft\Windows\CurrentVersion\Run', 'MayhemAdvisor');
end;
