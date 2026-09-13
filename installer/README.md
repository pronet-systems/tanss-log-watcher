# Setup lokal bauen

1. Voraussetzungen: .NET-10-SDK und Inno Setup 6 (`winget install JRSoftware.InnoSetup`).
   Ohne Adminrechte landet Inno unter `%LOCALAPPDATA%\Programs\Inno Setup 6` — genau dort
   sucht `publish.ps1` zuerst.
2. Alles in einem Schritt: `powershell -ExecutionPolicy Bypass -File build\publish.ps1`.
   Das testet, veröffentlicht Oberfläche und Kommandozeile, packt und meldet am Ende
   Größe und SHA256. Das Ergebnis liegt in `artifacts\TanssLogWatcher-<Version>-setup.exe`.
3. Version ändern: nur `Directory.Build.props`, Element `Version`. Skript und `.iss` lesen
   sie dort; `-Version x.y.z` übersteuert sie für Probeläufe.
4. Signieren: `build\publish.ps1 -Sign -CertificatePath <pfad.pfx> [-CertificatePassword <kw>]`.
   Ohne Zertifikat läuft es weiter und warnt — SmartScreen meckert dann bei jeder Version.
5. Nur packen, ohne neu zu übersetzen (setzt einen fertigen Nutzlastordner voraus):
   `"%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe" installer\TanssLogWatcher.iss /DPayloadDir=<ordner>`

Was das Setup anlegt: Programm unter `%LOCALAPPDATA%\Programs\TanssLogWatcher` (pro Benutzer,
ohne Adminrechte), Verknüpfungen im Startmenü und — abwählbar — im Autostart-Ordner mit
`--minimized`. `AppId` in der `.iss` ist die Identität der Installation und darf **nie**
geändert werden. `-DAppUrl=<adresse>` füllt die Links unter „Apps & Features“; ohne Angabe
bleiben sie leer.
