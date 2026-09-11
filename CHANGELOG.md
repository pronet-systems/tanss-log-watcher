# Änderungsprotokoll

Alle nennenswerten Änderungen an diesem Projekt stehen hier. Das Format folgt
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Fassungsnummern folgen
[Semantic Versioning](https://semver.org/lang/de/).

Die Fassungsnummer selbst steht an genau einer Stelle: im Element `Version` in
`Directory.Build.props`. Setup und Veröffentlichungslauf lesen sie dort.

---

## [0.1.0] — 2026-09-11

Erste Veröffentlichung. Die Kette von der Sitzungserkennung bis zur Fernwartung in TANSS
ist gegen eine Produktivinstanz nachgewiesen: erkannte Remotedesktop-Sitzung, eingereiht,
gesendet, in TANSS angelegt. Was noch offen ist, steht im
[README unter „Stand der Umsetzung"](README.md#stand-der-umsetzung) und unter „Bekannte
Einschränkungen"; vor einem breiten Produktiveinsatz stehen weiterhin ein Probelauf über
mehrere Arbeitstage und die Beteiligung der Mitbestimmung.

### Hinzugefügt

- **TANSS-Anbindung.** HTTP-Client mit `apiToken`-Kopfzeile, Anmeldung, Prägen und
  selbsttätiges Erneuern des Tokens, Anlegen und Lesen von Fernwartungen, eigene Tickets,
  Timer samt Notizen. Gegen eine Produktivinstanz verifiziert.
- **Existenzprüfung vor jeder Wiederholung.** Ein Eintrag mit unbekanntem Ausgang wird in
  TANSS gesucht, bevor er erneut gesendet wird. Notwendig, weil TANSS Fernwartungen nicht
  dedupliziert und eine Dublette nur per direktem Datenbankzugriff wieder zu entfernen ist.
- **Sitzungserkennung.** Auswertung von Fenstern, Prozessen und offenen
  Netzwerkverbindungen, Zustandsmaschine für Anfang und Ende einer Sitzung, 36
  Anwendungsprofile. Läuft ohne WPF und ohne Netz.
- **Lokale Ablage.** Konfiguration, Zugangsdaten über DPAPI, Warteschlange mit
  exponentiell wachsender Rückstauzeit, drei Protokollebenen (Betrieb, Änderungen, Token)
  mit getrennter Aufbewahrung.
- **Kommandozeile** `tanss-logwatch` mit `doctor`, `watch`, `windows`, `queue` und `token`.
- **Oberfläche** (WPF, Symbol im Infobereich): Sitzungen, Warteschlange, Timer, Überwachung,
  Diagnose und Verbindung — an die Laufzeit verdrahtet. Fußzeile und Hinweistext am Symbol
  zeigen Betriebszustand, laufende Sitzungen, Warteschlange und Token-Restlaufzeit.
- **Einrichtungsassistent in der Oberfläche.** Geht von selbst auf, solange nichts
  eingerichtet ist. Zwei Schritte: anmelden — dabei wird ein Jahrestoken geprägt, das
  Kennwort wird nicht abgelegt — und mindestens eine Anwendung einem Fernwartungstyp
  zuordnen. Über das Zahnrad unter „Verbindung" auch später erreichbar.
- **Abschlussdialog beim Sitzungsende.** Öffnet sich, wenn eine erkannte Sitzung endet, und
  ist mit Zeitraum, Gegenstelle, Anwendung und Arbeitsplatz vorausgefüllt — als
  Dokumentation für den Kunden. Drei Ausgänge: buchen, später, verwerfen. Die Sitzung liegt
  dabei bereits in der Warteschlange; das Fenster ergänzt sie nur.
- **Schonfrist von fünf Minuten** für frisch eingereihte Sitzungen. Ohne sie kann der
  Sendedienst eine Sitzung verschicken, während der Bericht noch geschrieben wird — gemessen
  und behoben. Das Speichern gibt den Eintrag sofort frei.
- **Timer** anlegen, starten, anhalten und löschen, mit tickender Laufzeit.
- **Selbsttätige Aktualisierung.** Sucht täglich nach einer neuen Veröffentlichung auf
  GitHub, lädt sie auf Anweisung, prüft den SHA256 gegen die veröffentlichte Prüfsumme und
  startet das Setup. Beendet wird dabei geordnet, damit laufende Sitzungen gesichert werden.
- **Sprachmodell-Unterstützung** (Anthropic oder OpenAI) zum Prüfen der Rechtschreibung und
  zum Ausformulieren des Berichts. **Standardmäßig abgeschaltet.** Die einzige Funktion, bei
  der Daten das Haus verlassen; sie bleibt ohne ausdrückliche, mit Zeitpunkt und Benutzer
  festgehaltene Einwilligung gesperrt. Die Modellliste kommt vom Anbieter und steht nicht
  fest im Programm. Namen von Zielsystemen und Arbeitsplätzen werden vor dem Senden
  ersetzt und danach wiederhergestellt.
- **Setup** (Inno Setup 6): Installation pro Benutzer ohne Administratorrechte nach
  `%LOCALAPPDATA%\Programs\TanssLogWatcher`, deutsche und englische Assistentenoberfläche,
  abwählbare Autostart-Verknüpfung mit `--minimized`, self-contained — die .NET-Laufzeit
  ist enthalten.
- **Veröffentlichungsskript** `build\publish.ps1`: testet, veröffentlicht Oberfläche und
  Kommandozeile, stellt die Nutzlast zusammen, packt das Setup und meldet Größe und SHA256.
- **Werkstrecke für GitHub**: `ci.yml` (Übersetzen und Tests bei jedem Push und Pull
  Request) und `release.yml` (Setup bauen und als Release mit SHA256 veröffentlichen, wenn
  eine Marke `v*` gesetzt wird).
- **CONTRIBUTING.md** mit den Hausregeln, den undokumentierten TANSS-Routen und der Regel
  zur Existenzprüfung.

### Bekannte Einschränkungen

- Das veröffentlichte Setup ist **nicht signiert**. SmartScreen warnt deshalb bei jeder
  neuen Fassung vor einem unbekannten Herausgeber. Abhilfe ist nur ein
  Code-Signing-Zertifikat; `publish.ps1 -Sign` ist dafür vorbereitet, aber ungeprüft.
- Der Befehl `tanss-logwatch setup` fehlt weiterhin. Eingerichtet wird über die Oberfläche;
  mehrere Fehlermeldungen der Kommandozeile verweisen noch auf diesen Befehl.
- Die 36 Titelmuster sind gegen aktuelle Anwendungsversionen noch nicht nachgeprüft.
- Die urheberrechtliche Herkunft der Anwendungsprofile ist ungeklärt (siehe README).
- Ein Token ohne `exp`-Anspruch lässt sich nicht erneuern — auch nicht erzwungen.
- Der Rechte-Vorabtest für das Prägen wird nicht selbsttätig ausgeführt: Er erzeugt in TANSS
  ein echtes, nicht widerrufbares Token. Fehlt das Recht, meldet es der Versuch selbst.

[0.1.0]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.1.0
