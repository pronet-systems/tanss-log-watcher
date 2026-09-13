<#
.SYNOPSIS
    Veröffentlicht den TANSS Log-Watcher und packt ein Setup daraus.

.DESCRIPTION
    Ein Durchlauf macht alles, was zwischen Quelltext und verteilbarer Datei liegt:
    Tests, dotnet publish für Oberfläche und Kommandozeile, Zusammenstellen des
    Nutzlastordners, Inno Setup, und am Ende Größe und SHA256 der erzeugten Datei.

    Der Hash gehört in die Veröffentlichungsnotiz. Nur damit lässt sich später
    nachweisen, welche Datei wirklich verteilt wurde.

.PARAMETER Version
    Fassungsnummer. Ohne Angabe wird sie aus Directory.Build.props gelesen -
    dort steht sie, und nur dort.

.PARAMETER Sign
    Signiert die erzeugten Dateien. Fehlt das Zertifikat, bricht der Lauf NICHT
    ab, sondern warnt: ein unsigniertes Setup ist brauchbar, nur unbequem.

.EXAMPLE
    .\build\publish.ps1
.EXAMPLE
    .\build\publish.ps1 -Version 0.2.0 -Sign -CertificatePath C:\pfad\code.pfx
#>

# Nachgemessen: steht #Requires VOR dem Hilfeblock, findet Get-Help ihn in
# Windows PowerShell 5.1 nicht mehr. Deshalb erst die Hilfe, dann die Bedingung.
#Requires -Version 5.1

[CmdletBinding()]
param(
    [string] $Version,
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64',

    # Eine Adresse für den Eintrag unter "Apps & Features". Ohne Angabe bleibt
    # das Feld leer - lieber keine Adresse als eine, die ins Leere führt.
    [string] $AppUrl,

    [switch] $SkipTests,
    [switch] $Sign,
    [string] $CertificatePath,
    [string] $CertificatePassword,
    [string] $SignToolPath,
    [string] $IsccPath,
    [string] $TimestampUrl = 'http://timestamp.digicert.com'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# --- Kleine Helfer ---------------------------------------------------------

function Write-Step {
    param([string] $Text)
    Write-Host ''
    Write-Host "=== $Text" -ForegroundColor Cyan
}

# Jede Abbruchmeldung sagt, was ist, warum das ein Problem ist und was zu tun
# bleibt. Ein nackter Stacktrace hilft am Freitagnachmittag niemandem.
function Stop-WithReason {
    param(
        [Parameter(Mandatory)][string] $What,
        [Parameter(Mandatory)][string] $Why,
        [Parameter(Mandatory)][string] $Todo
    )
    throw ("{0}{1}   Grund:   {2}{1}   Abhilfe: {3}" -f $What, [Environment]::NewLine, $Why, $Todo)
}

function Invoke-Tool {
    param(
        [Parameter(Mandatory)][string] $FilePath,
        [Parameter(Mandatory)][string[]] $Arguments,
        [Parameter(Mandatory)][string] $FailWhat,
        [Parameter(Mandatory)][string] $FailWhy,
        [Parameter(Mandatory)][string] $FailTodo
    )
    Write-Host "    $FilePath $($Arguments -join ' ')" -ForegroundColor DarkGray
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        Stop-WithReason -What "$FailWhat (Rückgabewert $LASTEXITCODE)" -Why $FailWhy -Todo $FailTodo
    }
}

# --- Pfade -----------------------------------------------------------------

$RepoRoot     = Split-Path -Parent $PSScriptRoot
$PropsFile    = Join-Path $RepoRoot 'Directory.Build.props'
$Solution     = Join-Path $RepoRoot 'TanssLogWatcher.slnx'
$AppProject   = Join-Path $RepoRoot 'src\TanssLogWatcher.App\TanssLogWatcher.App.csproj'
$CliProject   = Join-Path $RepoRoot 'src\TanssLogWatcher.Cli\TanssLogWatcher.Cli.csproj'
$IssFile      = Join-Path $RepoRoot 'installer\TanssLogWatcher.iss'
$ArtifactDir  = Join-Path $RepoRoot 'artifacts'
$AppPublish   = Join-Path $ArtifactDir 'publish\app'
$CliPublish   = Join-Path $ArtifactDir 'publish\cli'
$PayloadDir   = Join-Path $ArtifactDir 'payload'

# --- Fassung ---------------------------------------------------------------

function Get-VersionFromProps {
    param([Parameter(Mandatory)][string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) {
        Stop-WithReason -What "Directory.Build.props wurde nicht gefunden ($Path)." `
            -Why 'Dort steht die einzige gültige Fassungsnummer des Projekts.' `
            -Todo 'Das Skript aus dem Projektbaum heraus aufrufen oder -Version x.y.z angeben.'
    }

    # Buchstäblich dieselbe Suche wie im .iss, damit beide nie auseinanderlaufen.
    $match = Select-String -LiteralPath $Path -Pattern '<Version>([^<]+)</Version>' |
             Select-Object -First 1
    if (-not $match) {
        Stop-WithReason -What 'In Directory.Build.props steht kein Element <Version>.' `
            -Why 'Ohne Fassungsnummer bekommt das Setup keinen sinnvollen Eintrag unter "Apps & Features", und Aktualisierungen lassen sich nicht unterscheiden.' `
            -Todo 'Das Element dort eintragen oder -Version x.y.z angeben.'
    }
    return $match.Matches[0].Groups[1].Value.Trim()
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = Get-VersionFromProps -Path $PropsFile
    Write-Host "Fassung aus Directory.Build.props: $Version"
} else {
    $inProps = Get-VersionFromProps -Path $PropsFile
    if ($Version -ne $inProps) {
        # Kein Abbruch: für einen Probelauf ist eine abweichende Nummer legitim.
        Write-Warning ("Angegebene Fassung $Version weicht von Directory.Build.props ($inProps) ab. " +
                       "Das erzeugte Setup trägt $Version, die eingebauten Dateiversionen aber $inProps. " +
                       "Für eine echte Veröffentlichung zuerst Directory.Build.props ändern.")
    }
}

# --- Inno Setup finden -----------------------------------------------------

function Resolve-Iscc {
    param([string] $Preferred)

    $candidates = @()
    if ($Preferred) { $candidates += $Preferred }
    # winget installiert ohne Adminrechte ins Benutzerprofil, nicht nach
    # "Programme". Dieser Pfad kommt deshalb zuerst.
    $candidates += Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'
    $candidates += 'C:\Program Files (x86)\Inno Setup 6\ISCC.exe'
    $candidates += 'C:\Program Files\Inno Setup 6\ISCC.exe'

    foreach ($c in $candidates) {
        if ($c -and (Test-Path -LiteralPath $c)) { return (Resolve-Path -LiteralPath $c).Path }
    }

    $onPath = Get-Command 'ISCC.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }

    Stop-WithReason -What 'ISCC.exe (der Übersetzer von Inno Setup 6) wurde nicht gefunden.' `
        -Why 'Ohne ihn lässt sich aus dem veröffentlichten Ordner kein Setup packen.' `
        -Todo 'Inno Setup 6 installieren (winget install JRSoftware.InnoSetup) oder den Pfad mit -IsccPath angeben.'
}

$iscc = Resolve-Iscc -Preferred $IsccPath

# --- Signieren -------------------------------------------------------------

function Resolve-SignTool {
    param([string] $Preferred)

    if ($Preferred -and (Test-Path -LiteralPath $Preferred)) {
        return (Resolve-Path -LiteralPath $Preferred).Path
    }
    $found = Get-ChildItem -Path 'C:\Program Files (x86)\Windows Kits\10\bin' `
                           -Filter 'signtool.exe' -Recurse -ErrorAction SilentlyContinue |
             Where-Object { $_.FullName -match '\\x64\\' } |
             Sort-Object FullName -Descending |
             Select-Object -First 1
    if ($found) { return $found.FullName }

    $onPath = Get-Command 'signtool.exe' -ErrorAction SilentlyContinue
    if ($onPath) { return $onPath.Source }
    return $null
}

# Wird beim ersten Aufruf gefüllt; $null bedeutet "es wird nicht signiert".
$script:signTool = $null
$script:signingActive = $false

function Initialize-Signing {
    if (-not $Sign) {
        Write-Warning (
            "Es wird NICHT signiert (kein -Sign angegeben).`n" +
            "  Folge:   Windows SmartScreen warnt bei jedem neuen Build vor einer " +
            "'unbekannten App'. Der Benutzer muss 'Weitere Informationen' und dann " +
            "'Trotzdem ausführen' anklicken - bei jeder neuen Fassung erneut, weil der " +
            "Ruf einer unsignierten Datei bei null beginnt.`n" +
            "  Abhilfe: mit -Sign -CertificatePath <pfad.pfx> erneut aufrufen, sobald ein " +
            "Code-Signing-Zertifikat vorliegt.")
        return
    }

    if ([string]::IsNullOrWhiteSpace($CertificatePath) -or -not (Test-Path -LiteralPath $CertificatePath)) {
        Write-Warning (
            "-Sign wurde angegeben, aber das Zertifikat fehlt ('$CertificatePath').`n" +
            "  Folge:   Der Lauf geht unsigniert weiter - ein unsigniertes Setup ist " +
            "brauchbar, nur unbequem. SmartScreen wird bei jedem neuen Build warnen.`n" +
            "  Abhilfe: -CertificatePath auf eine vorhandene .pfx-Datei richten.")
        return
    }

    $script:signTool = Resolve-SignTool -Preferred $SignToolPath
    if (-not $script:signTool) {
        Write-Warning (
            "-Sign wurde angegeben, aber signtool.exe wurde nicht gefunden.`n" +
            "  Folge:   Der Lauf geht unsigniert weiter; SmartScreen wird warnen.`n" +
            "  Abhilfe: Windows SDK installieren oder -SignToolPath auf signtool.exe richten.")
        return
    }

    $script:signingActive = $true
    Write-Host "signtool: $($script:signTool)"
}

function Invoke-Signing {
    param([Parameter(Mandatory)][string] $Path)

    if (-not $script:signingActive) { return }

    $signArgs = @('sign', '/fd', 'SHA256', '/f', $CertificatePath)
    if ($CertificatePassword) { $signArgs += @('/p', $CertificatePassword) }
    # Zeitstempel: ohne ihn wird die Signatur ungültig, sobald das Zertifikat
    # abläuft - auch für längst verteilte Dateien.
    if ($TimestampUrl) { $signArgs += @('/tr', $TimestampUrl, '/td', 'SHA256') }
    $signArgs += $Path

    # @signArgs und NICHT @args: $args ist die automatische Variable und in einer Funktion mit
    # deklarierten Parametern immer leer. signtool.exe bekaeme kein einziges Argument, gaebe
    # seine Hilfe aus und einen Rueckgabewert ungleich null - das Signieren scheiterte also
    # immer, und zwar mit einer Meldung ueber ein angeblich kaputtes Zertifikat.
    & $script:signTool @signArgs
    if ($LASTEXITCODE -ne 0) {
        # Hier wird abgebrochen: wer signieren wollte und eine kaputte Signatur
        # bekommt, soll das nicht erst beim Kunden merken.
        Stop-WithReason -What "Das Signieren von '$Path' schlug fehl (Rückgabewert $LASTEXITCODE)." `
            -Why 'Eine halb signierte Veröffentlichung ist schlimmer als eine unsignierte: sie sieht geprüft aus und ist es nicht.' `
            -Todo 'Zertifikat, Kennwort und Erreichbarkeit des Zeitstempeldienstes prüfen - oder ohne -Sign veröffentlichen.'
    }
}

# --- Los geht es -----------------------------------------------------------

Write-Host "TANSS Log-Watcher - Veröffentlichung $Version ($Configuration, $Runtime)"
Write-Host "Projektwurzel: $RepoRoot"
Initialize-Signing

# 1. Tests ------------------------------------------------------------------

if ($SkipTests) {
    Write-Warning ('Die Tests wurden mit -SkipTests übersprungen. Dieses Setup ist ein Probelauf ' +
                   'und nicht verteilbar: niemand weiß, ob die Fachmodule noch tun, was sie sollen.')
} else {
    Write-Step 'Tests'
    Invoke-Tool -FilePath 'dotnet' -Arguments @('test', $Solution, '-c', $Configuration, '--nologo') `
        -FailWhat 'Mindestens ein Test schlug fehl.' `
        -FailWhy 'Ein Setup aus rotem Quelltext schreibt beim Kunden in dessen TANSS-Instanz. Das wird nicht gepackt.' `
        -FailTodo 'Die Ausgabe oben zeigt den fehlgeschlagenen Test. Erst reparieren, dann erneut veröffentlichen.'
}

# 2. Veröffentlichen --------------------------------------------------------

Write-Step 'Veröffentlichen'

if (Test-Path -LiteralPath (Join-Path $ArtifactDir 'publish')) {
    Remove-Item -LiteralPath (Join-Path $ArtifactDir 'publish') -Recurse -Force
}
if (Test-Path -LiteralPath $PayloadDir) {
    Remove-Item -LiteralPath $PayloadDir -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $AppPublish, $CliPublish, $PayloadDir | Out-Null

$publishArgs = @(
    '-c', $Configuration,
    '-r', $Runtime,
    '--self-contained', 'true',
    '-p:PublishSingleFile=true',
    '-p:EnableCompressionInSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    "-p:Version=$Version",
    '--nologo'
)

Invoke-Tool -FilePath 'dotnet' -Arguments (@('publish', $AppProject) + $publishArgs + @('-o', $AppPublish)) `
    -FailWhat 'Die Oberfläche (TanssLogWatcher.App) ließ sich nicht veröffentlichen.' `
    -FailWhy 'Ohne sie gibt es nichts zu installieren; das Setup wäre leer.' `
    -FailTodo 'Die Übersetzungsfehler oben beheben. Läuft parallel ein anderer Build oder die Anwendung selbst, sind bin\ und obj\ gesperrt - erst beenden.'

Invoke-Tool -FilePath 'dotnet' -Arguments (@('publish', $CliProject) + $publishArgs + @('-o', $CliPublish)) `
    -FailWhat 'Die Kommandozeile (TanssLogWatcher.Cli) ließ sich nicht veröffentlichen.' `
    -FailWhy 'Sie gehört zum Werkzeug und ist das Mittel der Wahl zur Fehlersuche beim Kunden.' `
    -FailTodo 'Die Übersetzungsfehler oben beheben und erneut aufrufen.'

# 3. Nutzlast zusammenstellen ----------------------------------------------

Write-Step 'Nutzlast zusammenstellen'

# Beide Veröffentlichungsordner werden ineinandergeschoben. Kopiert wird im
# Ganzen und nicht Datei für Datei mit selbst gerechnetem Teilpfad: sobald ein
# Pfad in seiner kurzen 8.3-Schreibweise hereinkommt, stimmen die Längen von
# angegebenem und tatsächlichem Pfad nicht überein und der Teilpfad wird falsch.
# Nachgemessen - dabei landete die Nutzlast in einem Unterordner ihrer selbst.
foreach ($source in @($AppPublish, $CliPublish)) {
    Copy-Item -Path (Join-Path $source '*') -Destination $PayloadDir -Recurse -Force
}

# Symboldateien fliegen wieder raus: sie blähen das Setup auf und helfen nur
# demjenigen, der ohnehin den Quelltext hat.
#
# Die XML-Dokumentation ebenso, und dort wiegt es schwerer: Sie wird zur Laufzeit
# von nichts gelesen - sie ist ausschliesslich für die Entwicklungsumgebung da,
# wenn jemand diese Baugruppen als Verweis einbindet. Im Setup lag damit die
# gesamte Dokumentation dieses Hauses beim Kunden auf der Platte, mitsamt jeder
# Begründung, die in einem /// -Kommentar steht.
Get-ChildItem -LiteralPath $PayloadDir -Recurse -File -Include '*.pdb', '*.xml' |
    ForEach-Object { Remove-Item -LiteralPath $_.FullName -Force }

foreach ($required in @('TanssLogWatcher.exe', 'tanss-logwatch.exe')) {
    if (-not (Test-Path -LiteralPath (Join-Path $PayloadDir $required))) {
        Stop-WithReason -What "Im Nutzlastordner fehlt $required." `
            -Why 'Das Setup würde eine Verknüpfung anlegen, die ins Leere zeigt.' `
            -Todo "Prüfen, ob AssemblyName im zugehörigen Projekt noch stimmt, und ob dotnet publish nach '$PayloadDir' geschrieben hat."
    }
    Invoke-Signing -Path (Join-Path $PayloadDir $required)
}

$payloadFiles = @(Get-ChildItem -LiteralPath $PayloadDir -Recurse -File)
$payloadBytes = ($payloadFiles | Measure-Object -Property Length -Sum).Sum
Write-Host ("Nutzlast: {0} Datei(en), {1:N1} MB in {2}" -f $payloadFiles.Count, ($payloadBytes / 1MB), $PayloadDir)

# 4. Setup packen -----------------------------------------------------------

Write-Step 'Setup packen'

$isccArgs = @(
    $IssFile,
    "/DAppVersion=$Version",
    "/DPayloadDir=$PayloadDir",
    "/DOutputDir=$ArtifactDir"
)
if ($AppUrl) { $isccArgs += "/DAppUrl=$AppUrl" }

Invoke-Tool -FilePath $iscc -Arguments $isccArgs `
    -FailWhat 'Inno Setup konnte das Setup nicht übersetzen.' `
    -FailWhy 'Ohne diesen Schritt gibt es keine verteilbare Datei.' `
    -FailTodo 'Die Fehlerzeile oben nennt Datei und Zeile in installer\TanssLogWatcher.iss.'

$setupPath = Join-Path $ArtifactDir "TanssLogWatcher-$Version-setup.exe"
if (-not (Test-Path -LiteralPath $setupPath)) {
    Stop-WithReason -What "Inno Setup meldete Erfolg, aber '$setupPath' liegt nicht dort." `
        -Why 'OutputDir oder OutputBaseFilename im .iss passen nicht mehr zu dem, was dieses Skript erwartet.' `
        -Todo 'Beide Werte in installer\TanssLogWatcher.iss mit den Pfaden in diesem Skript abgleichen.'
}

Invoke-Signing -Path $setupPath

# 5. Ergebnis ---------------------------------------------------------------

$setup = Get-Item -LiteralPath $setupPath
$hash = (Get-FileHash -LiteralPath $setupPath -Algorithm SHA256).Hash

Write-Step 'Ergebnis'
Write-Host ("Datei:   {0}" -f $setup.FullName)
Write-Host ("Größe:   {0:N0} Bytes ({1:N1} MB)" -f $setup.Length, ($setup.Length / 1MB))
Write-Host ("SHA256:  {0}" -f $hash)
Write-Host ("Signiert: {0}" -f $(if ($script:signingActive) { 'ja' } else { 'nein' }))
Write-Host ''
Write-Host 'Der SHA256 gehört in die Veröffentlichungsnotiz. Nur damit lässt sich später'
Write-Host 'nachweisen, welche Datei wirklich verteilt wurde.'

# Rückgabewert für aufrufende Skripte.
[PSCustomObject]@{
    Version  = $Version
    Path     = $setup.FullName
    Bytes    = $setup.Length
    Sha256   = $hash
    Signed   = $script:signingActive
}
