# Mitarbeiten

Danke für das Interesse. Dieses Dokument ist kurz gehalten und nennt nur das, was man wirklich
wissen muss, bevor man etwas ändert — vor allem die zwei Stellen, an denen ein Fehler teuer wird.

> **Stand: in Entwicklung.** Was fertig ist und was nicht, steht im
> [README unter „Stand der Umsetzung"](README.md#stand-der-umsetzung).

---

## Voraussetzungen

| | |
|---|---|
| Betriebssystem | Windows 10 oder 11. Die Überwachung ist Win32, das Zielframework `net10.0-windows10.0.26100.0` bei `SupportedOSPlatformVersion` 10.0.19041.0 — das versionierte Ziel verlangt die WinRT-Projektion der Bildschirmaufzeichnung, die Untergrenze hält die Zusage auf Windows 10. |
| SDK | .NET 10 |
| Für das Setup | Inno Setup 6 (`winget install JRSoftware.InnoSetup`) — nur, wer ein Setup packen will |
| TANSS-Instanz | **nicht nötig.** Die Testsuite läuft ohne Netz und ohne Zugangsdaten. |

## Bauen und testen

```bash
dotnet restore TanssLogWatcher.slnx
dotnet build   TanssLogWatcher.slnx -c Release -warnaserror
dotnet test    TanssLogWatcher.slnx -c Release
```

Ein Setup entsteht mit `build\publish.ps1`; die Einzelheiten stehen in
[`installer/README.md`](installer/README.md). Beim Bauen der Oberfläche darf die Anwendung nicht
laufen — sonst sind `bin\` und `obj\` gesperrt.

Der CI-Lauf tut genau dasselbe (siehe [`.github/workflows/ci.yml`](.github/workflows/ci.yml)).
Was lokal grün ist, ist dort grün.

### Tests gegen eine echte Instanz

`tests/TanssLogWatcher.Live.Tests` läuft **nicht** gegen Attrappen, sondern gegen eine echte
TANSS-Instanz. Ohne Zugangsdaten überspringt es sich selbst — die Werkstrecke bei GitHub hat
keine Instanz und soll deswegen nicht scheitern. „Übersprungen" ist dabei ausdrücklich nicht
„bestanden": Der Testläufer weist es getrennt aus.

```bash
# Nur lesende Prüfungen: Erreichbarkeit, Token-Zusagen, die undokumentierten Routen
TANSS_BASE_URL=https://tanss.kunde.de/backend TANSS_USER=... TANSS_PASSWORD=... \
  dotnet test tests/TanssLogWatcher.Live.Tests

# Zusätzlich der schreibende Timer-Umlauf (legt an und räumt wieder weg)
TANSS_LIVE_WRITES=1 ... dotnet test tests/TanssLogWatcher.Live.Tests
```

**Warum es dieses Projekt gibt.** Zwei Fehler haben es wochenlang durch alle 511 übrigen Tests
geschafft, weil sie mit Attrappen unsichtbar waren:

1. Ein Tokenspeicher gab das Präfix `Bearer ` nicht mit. Da jeder Test einen gefälschten Zugang
   benutzt, wurde nie eine echte Kopfzeile gebaut — die Attrappe *konnte* den Fehler nicht
   zeigen. Gegen die Instanz antwortet TANSS darauf mit 403.
2. Die Einrichtung legte das kurzlebige Sitzungstoken aus `/api/v1/login` als Arbeitstoken ab.
   Auf `/api/tanss.x/v1` gilt es nicht; nötig ist ein über `/api/v1/jwts/tanss_app` geprägtes.

Beide sind heute als Tests hinterlegt. **Wer eine Annahme über TANSS gegen seine eigene
Nachbildung dieser Annahme prüft, bekommt immer recht** — solche Annahmen gehören hierher.

Zwei Regeln für dieses Projekt:

- **Nie eine Fernwartung anlegen.** Das wäre gebuchte Arbeitszeit beim Kunden und bliebe stehen.
  Geprüft wird das Schreiben über Timer, weil die sich restlos löschen lassen.
- **Prägen ausschliesslich mit `isForTesting=true`.** Ein regulär geprägtes Token läuft ein Jahr
  und lässt sich in TANSS 10.10.0 **nicht widerrufen**. Eine Testreihe, die bei jedem Lauf eines
  ausstellt, hinterlässt nach einem Monat dreissig gültige Token, von denen niemand mehr weiss.

---

## Hausregeln

- **Deutsche Prosa, englische Bezeichner.** Dokumentationskommentare und alle Texte, die ein
  Benutzer zu sehen bekommt, sind deutsch. Klassen, Methoden, Felder und Parameter sind englisch.
- **Kommentare sagen *warum*, nicht *was*.** Was der Code tut, steht im Code. Ein Kommentar
  begründet eine Entscheidung, warnt vor einer Falle oder nennt eine Messung.
- **Null Warnungen sind Pflicht.** `TreatWarningsAsErrors` ist an, der CI-Lauf übersetzt
  zusätzlich mit `-warnaserror`. Eine Warnung wird behoben, nicht unterdrückt; wenn doch, dann
  gezielt über `NoWarn` mit einem Kommentar, der die Ausnahme begründet.
- **Jede Meldung sagt drei Dinge:** was ist, warum das passiert und was zu tun ist. Ein
  Stacktrace allein hilft am Freitagnachmittag niemandem.
- **Erst Status prüfen, dann Rumpf.** Ein leerer Rumpf ist nur bei Erfolg eine leere Antwort.
  Stünde die Leerprüfung vorher, wäre eine leere 403 ein leerer Erfolg.
- **Zeiten ausschließlich über `TanssTime`.** TANSS rechnet in Unix-**Sekunden**. Einen
  Millisekunden-Umrechner gibt es bewusst nicht.
- **Tests gehören dazu.** Jedes Fachmodul hat ein Testprojekt unter `tests/`. Die
  Sitzungserkennung läuft ohne WPF und ohne Netz und ist deshalb vollständig testbar.

---

## Das Wichtigste für Außenstehende: die TANSS-Routen sind großenteils undokumentiert

Ein erheblicher Teil der benutzten Routen steht in **keiner offiziellen Dokumentation**. Was
dieses Projekt über sie weiß, stammt aus der Server-Implementierung und aus Messungen gegen eine
Produktivinstanz — nicht aus einer Spezifikation. Die drei wichtigsten:

| Route | Zweck | Dokumentiert |
|---|---|---|
| `POST /api/tanss.x/v1/remoteSupports` | Fernwartung anlegen — der eine Aufruf, um den es geht | nein |
| `PUT /api/v1/remoteSupports` | Fernwartungen **lesen** (Leseweg, trotz `PUT`) | nein |
| `GET /api/v1/jwts/tanss_app` | Token prägen — **kein** Lesevorgang, jeder Aufruf stellt ein Token aus | nein |

**Wer an diesen Routen etwas ändert, braucht eine Messung, keine Vermutung.** Ein Feld, das
plausibel aussieht, ist noch lange nicht wirksam, und ein Verb sagt hier nichts über die Wirkung.
In den Pull Request gehört dann, gegen welche TANSS-Version gemessen wurde und was dabei
herauskam — Anfrage, Antwort, Statuscode.

Zwei Regeln, die beim Erweitern am häufigsten kosten:

- `/api/v1/**` verlangt `loggedInUserId` **zwingend** (sonst 403).
- `/api/tanss.x/v1/**` verträgt ihn **niemals**.

`TanssRoutes.NeedsLoggedInUserId` entscheidet das; der Client setzt den Parameter selbsttätig,
ein Aufrufer nie von Hand. Die Einzelheiten samt Rumpf einer Fernwartung stehen im
[README unter „Die TANSS-Anbindung im Einzelnen"](README.md#die-tanss-anbindung-im-einzelnen).

---

## Die Regel, deren Bruch am teuersten ist: erst prüfen, dann wiederholen

**TANSS dedupliziert Fernwartungen nicht.** Ein zweiter Aufruf mit derselben Kennung erzeugt
einen zweiten Datensatz. Und eine Dublette ist **nur über einen direkten Datenbankzugriff**
wieder zu entfernen — die Löschroute der API ist ohne ein typgebundenes Token unerreichbar.

Daraus folgt die Regel, die keine Ausnahme kennt:

> **Vor jeder Wiederholung steht die Existenzprüfung.** Ein Eintrag mit unbekanntem Ausgang wird
> nie einfach noch einmal gesendet. Er wird erst per Textfilter auf die eigene Sitzungskennung in
> TANSS gesucht und nur bei Abwesenheit gesendet.

Und der Umkehrschluss, den man leicht falsch macht: **scheitert die Existenzprüfung selbst,
heißt das „unbekannt", nicht „nicht vorhanden".** Der Eintrag wird dann zurückgestellt, nicht
gesendet. Wer hier eine Fehlerbehandlung „vereinfacht", die im Zweifel sendet, erzeugt
Dubletten in der Produktivinstanz eines Kunden, die jemand von Hand aus der Datenbank
schneiden muss.

Wer an der Warteschlange, an der Wiederholung oder an der Existenzprüfung arbeitet, schreibt
einen Test dazu, der genau diesen Fall abdeckt.

---

## Änderungen einreichen

1. Einen Zweig anlegen, nicht auf `main` arbeiten.
2. Ändern, Tests dazu, `dotnet build -warnaserror` und `dotnet test` lokal grün.
3. Einen Eintrag in [`CHANGELOG.md`](CHANGELOG.md) unter **Unveröffentlicht** ergänzen — eine
   Zeile in der Sprache des Benutzers, nicht in der des Compilers.
4. Pull Request aufmachen. Er beschreibt, **warum** die Änderung nötig ist, und bei allem, was
   TANSS berührt, **wie gemessen wurde**.
5. Der CI-Lauf muss grün sein.

Commit-Meldungen: erste Zeile im Imperativ und unter 72 Zeichen, danach eine Leerzeile und der
Grund. Deutsch oder Englisch — Hauptsache, der Grund steht da.

## Was hier nicht hineingehört

- **Keine Zugangsdaten, keine Token, keine Kundendaten.** Nicht im Quelltext, nicht in Tests,
  nicht in einem Issue und nicht in einem Protokollauszug. `config.json`, `credentials.dat`,
  `*.pfx` und `*.log` stehen deshalb in `.gitignore`.
- **Kein eigener Aktualisierungsmechanismus.** Aktualisiert wird über eine neue Version des
  Setups.

## Sicherheitslücken

Nicht als Issue. Eine Lücke in einem Werkzeug, das in fremde Ticketsysteme schreibt, wird
vertraulich gemeldet: per [Security Advisory](../../security/advisories/new) oder an
ProNet Systems GmbH.

---

## Lizenz

Mit einem Beitrag stellst du ihn unter die [MIT-Lizenz](LICENSE) dieses Projekts.
