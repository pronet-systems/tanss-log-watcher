; ===========================================================================
;  TANSS Log-Watcher — Setup-Skript für Inno Setup 6
;  ProNet Systems GmbH — MIT-Lizenz
;
;  Von Hand übersetzen:
;    ISCC.exe installer\TanssLogWatcher.iss /DPayloadDir=..\artifacts\payload
;
;  build\publish.ps1 nimmt einem das ab: es veröffentlicht, legt den
;  Nutzlastordner an und reicht Fassung, Nutzlast und URL hier herein.
;
;  Diese Datei ist UTF-8 MIT Byte-Order-Mark. Ohne die Marke liest Inno sie
;  als ANSI und aus jedem Umlaut wird Buchstabensalat im Assistenten.
; ===========================================================================

; ---------------------------------------------------------------------------
;  Kennung der Installation.
;
;  ACHTUNG: Dieser GUID darf NIE geändert werden. Windows entscheidet an ihm,
;  ob ein Setup eine bestehende Installation aktualisiert oder daneben eine
;  zweite anlegt. Ein neuer GUID bedeutet: zwei Einträge in "Apps & Features",
;  zwei Autostart-Verknüpfungen, zwei Programme, die um dieselbe
;  Zustandsdatenbank streiten. Wer Namen oder Herausgeber ändert, lässt diesen
;  Wert trotzdem stehen.
; ---------------------------------------------------------------------------
#define AppId "{BFE8473C-2707-4FBD-A106-D06063772187}"

; --- Von außen setzbare Konstanten -----------------------------------------
; Alles, was sich zwischen zwei Veröffentlichungen ändern kann, kommt per
; /D<Name>=<Wert> herein. Im Skript steht nur der Rückfallwert.

#ifndef AppName
  #define AppName "TANSS Log-Watcher"
#endif

#ifndef AppPublisher
  #define AppPublisher "ProNet Systems GmbH"
#endif

; AppUrl hat mit Absicht keinen Rückfallwert: eine falsche Adresse im
; Eintrag unter "Apps & Features" ist schlechter als gar keine. publish.ps1
; reicht sie mit /DAppUrl=... herein, sobald die Projektseite feststeht.

#define AppExeName "TanssLogWatcher.exe"
#define CliExeName "tanss-logwatch.exe"

; Mutexname aus src\TanssLogWatcher.App\App.xaml.cs. Ändert er sich dort, muss
; er hier mitwandern — sonst merkt das Setup die laufende Anwendung nicht.
#define AppMutexName "Local\TanssLogWatcher.SingleInstance"

#ifndef PayloadDir
  #define PayloadDir AddBackslash(SourcePath) + "..\artifacts\payload"
#endif

#ifndef OutputDir
  #define OutputDir AddBackslash(SourcePath) + "..\artifacts"
#endif

#define RepoRoot AddBackslash(SourcePath) + ".."

; ---------------------------------------------------------------------------
;  Fassung: eine Quelle der Wahrheit.
;
;  Die Nummer steht in Directory.Build.props und nirgends sonst. publish.ps1
;  liest sie dort und reicht sie mit /DAppVersion herein. Wer das Skript von
;  Hand übersetzt, bekommt sie hier gelesen — damit ein Setup aus der Hand nie
;  eine andere Nummer trägt als eines aus dem Skript.
; ---------------------------------------------------------------------------
#ifndef AppVersion
  #define PropsFile RepoRoot + "\Directory.Build.props"
  #if !FileExists(PropsFile)
    #error Directory.Build.props wurde nicht gefunden; erwartet wird sie neben dem Ordner "installer". Grund: dort und nur dort steht die Fassungsnummer. Abhilfe: das Skript aus dem Projektbaum heraus uebersetzen oder /DAppVersion=x.y.z angeben.
  #endif
  ; Nachgemessen: ein #define im Rumpf eines #sub wirkt nur dort und ist nach
  ; der Rückkehr wieder weg. Der Fund muss deshalb mit #expr in die außen
  ; angelegte Variable geschrieben werden.
  #define PropsHandle FileOpen(PropsFile)
  #define PropsLine
  #define TagStart 0
  #define ScannedVersion ""
  #define ScanLineNo 0
  #sub ScanForVersion
    #define PropsLine FileRead(PropsHandle)
    ; "<Version>" trifft weder <FileVersion> noch <InformationalVersion>:
    ; beide haben vor dem Wort "Version" keinen spitzen Klammeranfang.
    #define TagStart Pos("<Version>", PropsLine)
    #if (TagStart > 0) && (ScannedVersion == "")
      #expr ScannedVersion = Copy(PropsLine, TagStart + 9, Pos("</Version>", PropsLine) - TagStart - 9)
    #endif
  #endsub
  #for {ScanLineNo = 0; !FileEof(PropsHandle); ScanLineNo++} ScanForVersion
  #expr FileClose(PropsHandle)
  #if ScannedVersion == ""
    #error In Directory.Build.props steht kein <Version>-Element. Grund: das Setup braucht eine Fassungsnummer fuer den Eintrag unter "Apps & Features". Abhilfe: <Version> dort eintragen oder /DAppVersion=x.y.z angeben.
  #endif
  #define AppVersion ScannedVersion
#endif

; ---------------------------------------------------------------------------
;  Nutzlast prüfen, bevor der Übersetzer 60 MB packt.
; ---------------------------------------------------------------------------
#if !FileExists(AddBackslash(PayloadDir) + AppExeName)
  #error Im Nutzlastordner fehlt TanssLogWatcher.exe. Grund: das Setup haette nichts zu installieren und die Verknuepfungen zeigten ins Leere. Abhilfe: build\publish.ps1 ausfuehren oder /DPayloadDir=<Ordner> auf einen fertigen Veroeffentlichungsordner richten.
#endif

; ---------------------------------------------------------------------------
;  Rein numerische Fassung für die Dateiversion.
;
;  VersionInfoVersion nimmt nur Zahlen und Punkte. Eine Vorabfassung wie
;  "0.2.0-beta.1" ließe den Übersetzer mit "Value of [Setup] section directive
;  VersionInfoVersion is invalid" abbrechen — nachgemessen. Alles ab dem
;  Bindestrich fliegt deshalb raus; angezeigt wird weiterhin die volle Nummer.
; ---------------------------------------------------------------------------
#define SuffixPos Pos("-", AppVersion)
#if SuffixPos > 0
  #define NumericVersion Copy(AppVersion, 1, SuffixPos - 1)
#else
  #define NumericVersion AppVersion
#endif

#define IconFile RepoRoot + "\src\TanssLogWatcher.App\Assets\tray.ico"

[Setup]
AppId={{#AppId}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
VersionInfoVersion={#NumericVersion}
AppPublisher={#AppPublisher}
#ifdef AppUrl
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}
AppUpdatesURL={#AppUrl}
#endif

; Installation pro Benutzer: kein Administrator, kein UAC-Dialog, keine
; Rücksprache mit der IT des Kunden nötig.
PrivilegesRequired=lowest
DefaultDirName={localappdata}\Programs\TanssLogWatcher
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible

; .NET 10 und WPF setzen Windows 10 oder neuer voraus.
MinVersion=10.0

; Das Programm bringt seine Laufzeit mit (self-contained). Deshalb gibt es
; hier keinen Vorbedingungsschritt, der Administratorrechte bräuchte.
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
OutputDir={#OutputDir}
OutputBaseFilename=TanssLogWatcher-{#AppVersion}-setup
LicenseFile={#RepoRoot}\LICENSE
UninstallDisplayName={#AppName} {#AppVersion}
UninstallDisplayIcon={app}\{#AppExeName}
#if FileExists(IconFile)
SetupIconFile={#IconFile}
#endif

; Läuft das Programm noch, sind seine Dateien gesperrt. AppMutex erkennt das
; zuverlässig: die Anwendung hält diesen Mutex, solange sie läuft — auch wenn
; sie nur als Symbol im Infobereich sitzt und kein Fenster zeigt.
AppMutex={#AppMutexName}

; Zweites Netz für die Kommandozeile, die keinen Mutex hält: der
; Neustart-Manager von Windows findet Prozesse, die eine Zieldatei offen
; halten, und bietet an, sie zu schließen.
CloseApplications=yes
CloseApplicationsFilter=*.exe

; Nach der Installation nichts von selbst wieder starten. Der Neustart-Manager
; würde die alte Befehlszeile wiederverwenden — beim Log-Watcher wäre das ein
; Start ohne --minimized, also ein Fenster, das niemand angefordert hat. Den
; Start bietet [Run] am Ende des Assistenten an.
RestartApplications=no

[Languages]
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "en"; MessagesFile: "compiler:Default.isl"

[Messages]
; Was ist, warum, was zu tun — der Standardtext von Inno sagt nur das Erste.
de.SetupAppRunningError=%1 läuft noch.%n%nSolange das Programm läuft, sind seine Dateien gesperrt und lassen sich nicht ersetzen.%n%nBeenden Sie es über das Symbol im Infobereich der Taskleiste (rechts unten neben der Uhr): Rechtsklick, "Beenden". Klicken Sie danach auf "OK", um fortzufahren. "Abbrechen" bricht die Installation ab; geändert wurde bis hierher nichts.
en.SetupAppRunningError=%1 is still running.%n%nWhile the program is running its files are locked and cannot be replaced.%n%nClose it from the tray icon next to the clock: right-click, "Exit". Then click "OK" to continue. "Cancel" aborts the installation; nothing has been changed so far.
de.UninstallAppRunningError=%1 läuft noch.%n%nEine laufende Anwendung lässt sich nicht entfernen, ihre Dateien sind in Benutzung.%n%nBeenden Sie sie über das Symbol im Infobereich der Taskleiste (Rechtsklick, "Beenden") und klicken Sie dann auf "OK".
en.UninstallAppRunningError=%1 is still running.%n%nA running application cannot be removed; its files are in use.%n%nClose it from the tray icon (right-click, "Exit") and then click "OK".

[CustomMessages]
de.AutostartGroup=Automatischer Start
de.AutostartTask=TANSS Log-Watcher beim Anmelden starten (als Symbol im Infobereich)
de.StartMenuComment=Fernwartungssitzungen erkennen und nach TANSS melden
de.AutostartComment=Startet den TANSS Log-Watcher beim Anmelden als Symbol im Infobereich
de.RemoveDataPrompt=Sollen auch Einstellungen und Daten des TANSS Log-Watchers entfernt werden?%n%nBetroffen sind diese beiden Ordner:%n%n%1%n%2%n%nDarin liegen die Konfiguration, die gespeicherten Zugangsdaten und die Zustandsdatenbank. In deren Warteschlange können noch Sitzungen liegen, die nie an TANSS übertragen wurden — die wären unwiederbringlich verloren.%n%nWählen Sie "Nein", wenn Sie das Programm später erneut installieren oder die Warteschlange vorher noch prüfen wollen; die Ordner bleiben dann unberührt. "Ja" löscht sie endgültig.
en.AutostartGroup=Automatic start
en.AutostartTask=Start TANSS Log-Watcher when I sign in (as a tray icon)
en.StartMenuComment=Detect remote support sessions and report them to TANSS
en.AutostartComment=Starts TANSS Log-Watcher at sign-in as a tray icon
en.RemoveDataPrompt=Also remove the settings and data of TANSS Log-Watcher?%n%nThis affects these two folders:%n%n%1%n%2%n%nThey hold the configuration, the stored credentials and the state database. Its queue may still contain sessions that were never transferred to TANSS — those would be lost for good.%n%nChoose "No" if you intend to reinstall later or want to check the queue first; the folders are then left untouched. "Yes" deletes them permanently.

[Tasks]
; Voreingestellt AN (kein "unchecked"): ohne automatischen Start erkennt das
; Werkzeug keine Sitzung, bis es jemand von Hand startet.
Name: "autostart"; Description: "{cm:AutostartTask}"; GroupDescription: "{cm:AutostartGroup}"

[Files]
; Der ganze Veröffentlichungsordner. Die Kommandozeile tanss-logwatch.exe
; liegt darin und gehört zum Werkzeug: sie macht dieselbe Arbeit ohne
; Oberfläche und ist das Mittel der Wahl zur Fehlersuche beim Kunden.
; *.xml ist die Entwicklerdokumentation der Baugruppen. Zur Laufzeit liest sie
; niemand; der Veröffentlichungslauf entfernt sie bereits aus der Nutzlast. Hier
; steht sie ein zweites Mal, damit sie auch dann nicht mitgeht, wenn die Nutzlast
; einmal anders entsteht.
Source: "{#AddBackslash(PayloadDir)}*"; DestDir: "{app}"; Excludes: "*.pdb,*.xml"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "{#RepoRoot}\LICENSE"; DestDir: "{app}"; DestName: "LICENSE.txt"; Flags: ignoreversion
Source: "{#RepoRoot}\README.md"; DestDir: "{app}"; Flags: ignoreversion skipifsourcedoesntexist

[Icons]
Name: "{userprograms}\{#AppName}"; Filename: "{app}\{#AppExeName}"; WorkingDir: "{app}"; Comment: "{cm:StartMenuComment}"

; Autostart über eine Verknüpfung im Autostart-Ordner, nicht über
; HKCU\...\Run: der Ordnerweg ist im Task-Manager unter "Autostart" sichtbar
; und dort abschaltbar. Bei einem Werkzeug, das Fenstertitel mitliest, ist
; diese Sichtbarkeit Voraussetzung, kein Nachteil.
Name: "{userstartup}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Parameters: "--minimized"; WorkingDir: "{app}"; Comment: "{cm:AutostartComment}"; Tasks: autostart

[Run]
Filename: "{app}\{#AppExeName}"; Description: "{cm:LaunchProgram,{#StringChange(AppName, '&', '&&')}}"; WorkingDir: "{app}"; Flags: nowait postinstall skipifsilent

[Code]
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ConfigDir, StateDir, Prompt: String;
begin
  // Erst fragen, wenn das Programm schon weg ist: vorher könnte der Benutzer
  // noch abbrechen, und gelöschte Daten ließen sich nicht zurückholen.
  if CurUninstallStep <> usPostUninstall then
    Exit;

  ConfigDir := ExpandConstant('{userappdata}\ProNet Systems\TanssLogWatcher');
  StateDir := ExpandConstant('{localappdata}\ProNet Systems\TanssLogWatcher');

  // Nicht fragen, wenn es nichts zu löschen gibt.
  if not (DirExists(ConfigDir) or DirExists(StateDir)) then
    Exit;

  Prompt := FmtMessage(CustomMessage('RemoveDataPrompt'), [ConfigDir, StateDir]);

  // MB_DEFBUTTON2 und die Vorgabe IDNO für den stillen Lauf: Behalten ist die
  // sichere Antwort, also ist sie die voreingestellte.
  if SuppressibleMsgBox(Prompt, mbConfirmation, MB_YESNO or MB_DEFBUTTON2, IDNO) <> IDYES then
    Exit;

  DelTree(ConfigDir, True, True, True);
  DelTree(StateDir, True, True, True);
end;
