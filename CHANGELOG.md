# Änderungsprotokoll

Alle nennenswerten Änderungen an diesem Projekt stehen hier. Das Format folgt
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Fassungsnummern folgen
[Semantic Versioning](https://semver.org/lang/de/).

Die Fassungsnummer selbst steht an genau einer Stelle: im Element `Version` in
`Directory.Build.props`. Setup und Veröffentlichungslauf lesen sie dort.

---

## [0.2.0] — 2026-09-13

### Hinzugefügt — Bildschirmaufzeichnung, erster Teil

Der prüfbare Kern der Aufzeichnung steht: das neue Projekt `TanssLogWatcher.Recording`, der
Konfigurationsabschnitt samt Prüfung und die drei Stücke, an denen die Ehrlichkeit der
Aufzeichnung hängt. Aufgezeichnet wird noch nichts — es gibt bewusst keinen Schalter in der
Oberfläche, solange nichts dahintersteht.

**Gemessen, bevor entworfen wurde.** Achtzehn Prüfläufe mit eigenen Probeprogrammen gegen diese
Windows-Fassung (11 Pro, Build 22631.6199, Intel Iris Xe):

- **Die Fensteraufnahme liefert besessene Unterfenster nicht mit.** Ein grüner Dialog über einem
  roten Hauptfenster ergab an seiner Stelle reines Rot (R=255 G=0 B=0). Wer nur das
  Hauptfenster aufnähme, verlöre genau die Dialoge, um die es hinterher geht — das
  Anmeldefenster von WinSCP, die Bestätigungsabfrage, den Fortschrittsbalken. Deshalb wird
  jedes Fenster einzeln aufgenommen und auf eine gemeinsame Leinwand kopiert.
- **`IncludeSecondaryWindows`**, die Schnittstelle, die das abnehmen würde, **wirft auf dieser
  Windows-Fassung** eine `InvalidCastException`, obwohl sie in der Projektion steht. Sie
  braucht 24H2.
- **`MinUpdateInterval`** ebenso — die Bildrate wird deshalb selbst getaktet.
- **Der gelbe Aufnahmerahmen landet nicht im Video.** Randpunkte des aufgenommenen Bildes waren
  in beiden Betriebsarten schwarz. Er warnt auf dem Bildschirm, ohne die Dokumentation zu
  verschmutzen.
- **Verdeckung stört nicht:** 120 Bilder in 2000 ms, und der Bildmittelpunkt trug die Farbe des
  Zielfensters, nicht die des Verdeckers. **Minimierung** liefert exakt 0 Bilder, ohne dass die
  Aufnahme endet — die geforderte Pause fällt von selbst an.

**Was gebaut wurde**

- `RecordingClock` — die Uhr läuft nur, während geschrieben wird. Fünfundvierzig Minuten Sitzung
  mit fünfundzwanzig Minuten Pause ergeben eine Datei von zwanzig Minuten und nicht von
  fünfundvierzig. Die Zeitstempel werden aus dem Bildindex gerechnet statt aufaddiert; bei drei
  Bildern je Sekunde driftet sonst jede Stunde weiter weg.
- `CanvasLayout` — die feste Leinwand. Mehrere Fenster, ein Video. Kantenlängen immer gerade,
  weil H.264 mit 4:2:0 ungerade ablehnt. Passt eine spätere Fensterlage nicht mehr hinein, wird
  eine neue Datei begonnen — **skaliert wird nie**, das zerstörte die Lesbarkeit der Schrift.
- `FrameCadence` — die Bildrate von Hand, mit Herzschlag. Ein stehender Bildschirm bekommt
  trotzdem eine Zeitachse; während einer Pause wird kein Herzschlag fällig, sonst hinterliesse
  die Pause Bilder und wäre in der Datei keine.
- `RecordingSection` in der Konfiguration, nach dem Vorbild des KI-Abschnitts. **Vier
  Bedingungen**, und alle vier müssen gelten: eingeschaltet, Kenntnisnahme erteilt,
  Rechtsgrundlage benannt, Beleg benannt. Eine Kenntnisnahme ohne benannte Grundlage ist ein
  Haken und kein Nachweis; die Prüfung beanstandet eine halb ausgefüllte.
- Die Löschfrist steht bei dreissig Tagen. Der Löschzeitpunkt einer einzelnen Aufzeichnung wird
  beim Abschluss daraus berechnet und festgeschrieben: Eine spätere Verkürzung wirkt auch auf
  bestehende, eine Verlängerung **nicht** — ein gegebenes Versprechen wird nicht gedehnt.

**Geändert — versioniertes Windows-Ziel**

`TanssLogWatcher.Recording` und `TanssLogWatcher.App` zielen jetzt auf
`net10.0-windows10.0.26100.0` bei `SupportedOSPlatformVersion` 10.0.19041.0. Das ist keine
Geschmacksfrage: Die WinRT-Projektion kommt ausschliesslich über einen versionierten Moniker,
und ein Verweis von `net10.0-windows` darauf ergibt `NU1201` — einen harten Fehler, nachgemessen.
Die Zusage der README auf Windows 10 hält die Untergrenze.

**Der Direktor und die Fensterquelle**

- `RecordingDirector` — er trifft **jede** Entscheidung und ruft **keine** Windows-Funktion:
  beginnen, anhalten, fortsetzen, Abschnitt wechseln, beenden. Dadurch lässt sich das Verhalten
  der Aufzeichnung ohne Bildschirm prüfen, und das heisst: überhaupt. Achtzehn Tests decken die
  Fälle ab, die im Betrieb zählen — Fenster wandert über die Leinwand hinaus, Unterfenster geht
  später auf, Platte läuft voll, Sitzung endet mitten im Abschnitt.
- **Die Schonfrist** ist der Unterschied zwischen einer Pause und einem Fenster im Umzug: Der
  erste Takt ohne Fenster startet nur die Frist, angehalten wird frühestens im nächsten. Ohne
  sie stünde nach jedem Verschieben zwischen zwei Bildschirmen eine Unterbrechung im Bericht,
  die es nie gab.
- **Bei Platzmangel tritt die Aufzeichnung zurück** und sagt es. Eine volle Platte nimmt auch
  der Warteschlange den Platz, und die trägt die Arbeitszeit.
- `Win32WindowGeometrySource` — die eigene Fensteraufzählung. Sie musste sein, weil
  `WindowSource` im Überwachungsprojekt jedes Fenster **mit leerem Titel verwirft**: für die
  Erkennung richtig, für die Aufzeichnung falsch. Dialoge tragen regelmässig keinen Titel, und
  genau die gingen sonst verloren. Geprüft wird sie gegen **echte Fenster**, die der Testlauf
  selbst öffnet — ein Test, der von zufällig geöffneten Anwendungen abhinge, wäre keiner.
  Darunter der Fall, wegen dem es sie gibt: ein titelloses Unterfenster wird gefunden.
- Versteckte Fenster fallen über `DWMWA_CLOAKED` weg. Sie melden eine ordentliche Geometrie und
  liefern trotzdem kein Bild — eine Anwendung auf einem anderen virtuellen Desktop etwa. Ohne
  diese Prüfung stünde sie als schwarzes Rechteck auf der Leinwand.

**Noch nicht gebaut:** der Bildfang selbst (Windows.Graphics.Capture), die Kodierung (Media
Foundation), die Ablage samt Begleitdatei, der Löschdienst und die Oberfläche. Mehrschirm-RDP
ist ausdrücklich zurückgestellt.

### Geändert — Sekundentakt

Die Beobachtung lief alle zehn Sekunden. Bis zu zehn Sekunden Ungenauigkeit an Beginn und Ende
einer Fernwartung sind für die Abrechnung verschmerzbar; für den Abschlussdialog sind sie es
nicht — er ging auf, wenn der Techniker schon weitergezogen war. Voreinstellung jetzt **eine
Sekunde**.

- **Gemessen statt behauptet.** Über der Sitzungsliste stand fest eingebaut „Alle 10 Sekunden
  geprüft“ — auch dann, wenn `watcher.poll_interval_seconds` etwas anderes sagte. Dort steht
  jetzt der tatsächliche Takt und die **gemessene Dauer des letzten Durchlaufs**. Auf dem
  Entwicklungsrechner: 29 ms gegen ein Sekundenbudget. Wer die Zahl steigen sieht, weiss, dass
  der Takt heraufgehört.
- **Der Takt wartet ab, was der Durchlauf gebraucht hat.** Vorher wurde nach getaner Arbeit die
  volle Spanne gewartet — der Abstand war also Takt *plus* Arbeit. Bei fünfzehn Minuten belanglos,
  bei einer Sekunde nicht.
- **Die Sitzungsliste wird nur bei einer Änderung neu gemeldet.** Die Ansicht ersetzt ihre Zeilen
  bei jeder Meldung vollständig; im Sekundentakt hiesse das Flackern und eine Auswahl, die keine
  Sekunde übersteht. Die Dauer läuft davon unberührt in der Ansicht weiter.
- **Die Schwelle „gerade gestartet“ folgt dem gemessenen Abstand**, mindestens aber dem
  eingestellten Takt. Ein Durchlauf, der einmal länger braucht, verliert damit keinen echten
  Sitzungsbeginn mehr.

### Hinzugefügt — aus einem Timer wird eine Leistung

Ein Timer liess sich starten, anhalten und löschen — gebucht wurde er dann doch wieder in TANSS.
Erfasste Zeit, die nie zur Leistung wird, ist Arbeit, die niemand bezahlt.

- **Schaltfläche „Leistung“ an jedem Timer.** Sie öffnet einen Dialog mit **einem Feld je
  Zeitabschnitt**: Wer den Timer dreimal gestartet und gestoppt hat, beschreibt drei
  Arbeitsschritte einzeln. Diese Gliederung ist nicht erfunden — TANSS gibt die vorbereitete
  Leistung mit einem Texteintrag je Laufabschnitt zurück (`multiTextItems`), nachgemessen gegen
  eine Instanz der Fassung 10.10.0.
- **Zwei Schritte, und der erste legt nichts an.** `POST /api/v1/supports/properties` mit
  `{"initializers":[{"type":"TIMER","id":…}]}` lässt TANSS über achtzig Felder vorbelegen —
  Stundensatz, Abrechnungsart, Fahrzeug, Zone. Erst `POST /api/v1/supports` bucht. Der Weg ist
  belegt: `PUT` auf dieselbe Route antwortet mit `405` und `Allow: POST`.
- **Der vorbelegte Block geht unverändert zurück.** Von den achtzig Feldern versteht dieses
  Werkzeug drei; die übrigen entscheiden über die Abrechnung. Ein selbstgebautes Objekt mit den
  bekannten Feldern hätte eine Leistung mit Stundensatz 0 gebucht — aufgefallen wäre das in der
  Rechnungsstellung.
- **Gemessene und abgerechnete Dauer stehen nebeneinander**, wenn sie auseinandergehen. Sonst
  hält der Techniker die Rundung der Instanz für einen Fehler.

### Behoben — Bedienelemente verschwanden bei schmalem Fenster

Wurde das Fenster schmaler gezogen, schob sich „Timer anlegen“ aus dem sichtbaren Bereich —
erst halb abgeschnitten, dann ganz weg. Eine Durchsicht **aller** Ansichten hat gezeigt, dass
das kein Einzelfall war, sondern ein Muster.

**Die Breite wird jetzt am Fenster festgelegt, nicht durch Umbrechen aufgefangen.**
`MinWidth` steht auf 1100 DIP. Die Zahl ist gemessen, nicht gegriffen: Die breiteste Seite ist
„Überwachung“ mit 140 + 220 + 240 DIP für ihre festen Spalten, mindestens 200 für den
Anwendungsnamen und 2×16 Kartenrand — 814, mit Seitenrand 862, mit dem Navigationsbereich
(`OpenPaneLength` 220) 1082. Ein Knopf, der in die nächste Zeile rutscht, steht beim nächsten
Blick nicht mehr dort, wo er war; ein Fenster, das sich nicht kleiner ziehen lässt, hat dieses
Problem nicht.

- **Die Anlegen-Reihe ist ein Raster mit `*`-Spalten.** Die beiden Eingabefelder teilen sich,
  was da ist; „Timer anlegen“ behält seine Spalte. Kein Umbruch mehr.
- **Acht Überlappungen**, alle nach demselben Muster: zwei Kinder ohne `Grid.Column` im selben
  Feld, das eine links, das andere rechts ausgerichtet — und das rechte liegt in der
  Kinderliste hinten, deckt das linke also zu. Betroffen waren die **Fußzeile des
  Hauptfensters** (sie wächst zur Laufzeit um 124 DIP, sobald der erste Eintrag in der
  Warteschlange liegt), drei Karten unter „Verbindung“ und die Fußleisten von
  Einrichtung, Abschlussdialog und KI-Einstellungen.
- **Zwei Hinweisleisten lagen in derselben Gitterzelle** wie der Text darüber, nur durch einen
  fest eingetragenen Rand nach unten geschoben. Je schmaler das Fenster, desto mehr Zeilen
  bekam der Text — und desto sicherer schob er sich darunter. Beide haben jetzt eine eigene
  Zeile.
- **Sechs Texte ohne `TextWrapping`** hörten am rechten Rand einfach auf, ohne Auslassungspunkte.
- **Die Basisadresse in der Fußzeile** ist so lang, wie die Instanz des Kunden heißt. Sie kürzt
  jetzt mit Auslassungspunkten, statt den Rest der Zeile hinauszuschieben.

### Behoben — das Werkzeug startete auf der falschen Seite

Beim Start stand die Anwendung auf „Verbindung“ statt auf den Sitzungen — auch dann, wenn
`--page=sessions` ausdrücklich mitgegeben wurde.

Die Navigationsleiste löst das Ziel über ihre Einträge auf. Sind die beim Auslösen von
`Loaded` noch nicht aufgebaut, findet sie nichts, meldet `false` und wählt anschliessend von
sich aus den letzten Eintrag — und der heisst „Verbindung“. Ob die Einträge rechtzeitig
stehen, hängt davon ab, wie schnell der Rechner das erste Layout durchrechnet: ein Wettlauf,
den dieselbe Fassung mal gewinnt und mal verliert.

Gesehen hat es niemand, weil der Rückgabewert mit `_ = Navigation.Navigate(page)` weggeworfen
wurde. Ein fehlgeschlagener Aufruf sah damit aus wie eine Design-Entscheidung. Jetzt wird der
Rückgabewert geprüft und zweimal nachgefasst; bleibt es dabei, steht eine Meldung im
Protokoll, statt die Sache stillschweigend hinzunehmen. Nachgemessen über fünf Starts
hintereinander und über alle sechs Werte von `--page`.

### Behoben — die Timerliste ließ sich nicht rollen

`ScrollViewer.VerticalScrollBarVisibility="Auto"` stand am `ItemsControl`. Dessen Vorlage
enthält aber gar keine Rollfläche, und anders als bei den übrigen Seiten gab es auch keine
darum herum. Bei mehr Timern, als auf die Seite passen, waren die unteren schlicht nicht
erreichbar — kein Bildlauf, kein Hinweis, sie waren weg.

### Behoben — die Tabellen rollten bei jeder Fenstergröße

Der waagerechte Rollrahmen, der die Tabellen erreichbar halten sollte, war selbst der Fehler:
Eine Spalte mit `Width="*"` wird darin mit unbegrenzter Breite gemessen und nimmt sich, was ihr
Inhalt wünscht. Die Tabelle rollte deshalb **immer**, auch bei einem sehr breiten Fenster.
Entfernt; die Breite regelt jetzt `MinWidth` am Fenster.

### Behoben — „Jetzt senden“ tat nichts

Die Warteschlange zeigte „1 ausstehend“, die Schaltfläche meldete „Nichts fällig“, und nichts
ging hinaus. Der Eintrag war zurückgehalten: fünf Minuten Schonfrist, damit der Abschlussbericht
in Ruhe geschrieben werden kann. Nirgends stand das, und von einer kaputten Schaltfläche war es
nicht zu unterscheiden.

- **Von Hand heisst jetzt.** „Jetzt senden“ zieht zurückgehaltene Einträge vor — die Schonfrist
  ebenso wie den Rückstau nach einem Fehlversuch. Der Takt tut das ausdrücklich nicht.
- **Die Zeile sagt, worauf sie wartet** und bis wann.
- **„Nichts fällig“** erscheint nur noch, wenn wirklich nichts dasteht.

### Behoben — IP-Ausschlüsse waren nicht mehr zu bearbeiten

Das Feld „IP-Adressen ausnehmen“ war in 0.1.1 auf „nur lesbar“ gesetzt worden, weil ein
beschreibbares Feld ohne Wirkung schlimmer ist als gar keines. Das war die falsche Hälfte der
Lösung: Ohne Ausschluss wird jede Sitzung zum eigenen Server als Fernwartung beim Kunden gebucht.

- **Beschreibbar und wirksam**, mit Semikolon als Trennzeichen — dieselbe Schreibweise wie in
  `config.json`.
- **Jeder Eintrag wird einzeln geprüft**, Einzeladresse wie CIDR-Netz. Ein einziger fehlerhafter
  Eintrag verwirft die ganze Liste und wird benannt; „zwei von drei übernommen“ sähe der Benutzer
  als vollständig an.
- **Gesperrt, solange die Zeile auf „Nicht überwachen“ steht** — gespeichert wird nur, was
  zugeordnet ist.

---

## [0.1.1] — 2026-09-12

### Behoben — erfundene Daten und vorgetäuschte Prüfungen

Eine Durchsicht des gesamten Baums auf Stellen, die dem Benutzer etwas anzeigen, das nie
gemessen wurde. Die schwerste: Die Verbindungsseite trug eine Karte „Letzte Prüfung“ mit vier
fest eingebauten grünen Haken — darunter „Token trägt und lässt sich erneuern“. Sie stand auch
grün da, wenn nichts eingerichtet war, wenn die Prüfung fehlschlug und während das Token auf
jeder Anfrage eine 403 erzeugte. Sie hätte beide Fehler dieser Fassung verdeckt.

- **Verbindungsprüfung** zeigt jetzt echte Befunde mit einer eigenen Stufe „nicht geprüft“, die
  nicht aussieht wie „in Ordnung“.
- **Aktualisierung** meldete „stimmt mit der Prüfsumme überein“, sobald eine Prüfsummen-*Adresse*
  existierte. Ob verglichen wurde, wusste sie nicht; jetzt schon.
- **`CanRotateAsync`** machte aus „nicht erreichbar“ die Aussage „darf keine Token prägen“ und
  schickte den Techniker wegen eines Netzausfalls zum TANSS-Administrator. Drei Ausgänge statt
  zwei.
- **Proxy-Kennwort** erreichte die Leitung nie — `ToTanssOptions` setzte es nicht. Ein Proxy mit
  Anmeldung meldete sich lautlos mit leerem Kennwort an.
- **Attributions-Gegenprobe** beim Anlegen wurde berechnet und weggeworfen. Eine Fernwartung,
  die bei TANSS auf einem fremden Mitarbeiter landet, fiel damit niemandem auf.
- **`windows`** lief im Betrieb ohne Namensauflösung und zeigte Ziele, die der laufende Dienst
  ausschliesst — ausgerechnet der Befehl zum Nachsehen, warum etwas nicht erkannt wird.
- Tote Schaltfläche „Aktualisieren“, nirgends gebundene Diagnose-Zusammenfassung, beschreibbares
  IP-Feld ohne Wirkung, „Token 2147483647 Tage“ bei Token ohne Ablauf.
- Mehrere Meldungen behaupteten Tatsachen, die sie nicht kannten („bereits verschickt“, „in
  TANSS angelegt“, „Schlüssel gelöscht“).

### Berichtigt — zwei nachgemessene Falschaussagen im Quelltext

- `isForTesting=true` liefert **kein** unbrauchbares Token: TANSS hält sich bei beiden Werten an
  die angefragte Laufzeit. Kurzlebig wird der Trockentest allein dadurch, dass `MintAsync` selbst
  60 Sekunden anfragt — die Harmlosigkeit hängt an unserer Zeile, nicht an einer Zusage.
- `loggedInUserId` darf auf `/api/tanss.x/v1` sehr wohl mitgeschickt werden; er wird ignoriert.
  Die gegenteilige Behauptung stand als Ursache Nummer eins in der Meldung zu jeder 403.

### Hinzugefügt

- **`tests/TanssLogWatcher.Live.Tests`** — neun Tests gegen eine echte Instanz statt gegen
  Attrappen. Ohne Zugangsdaten überspringen sie sich selbst; „übersprungen“ wird getrennt von
  „bestanden“ ausgewiesen. Sie halten die beiden Fehler dieser Fassung fest sowie die beiden
  Falschaussagen oben. Schreibende Tests verlangen zusätzlich `TANSS_LIVE_WRITES=1` und legen
  nie eine Fernwartung an.

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

[0.2.0]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.2.0
[0.1.1]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.1.1
[0.1.0]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.1.0
