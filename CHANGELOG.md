# Änderungsprotokoll

Alle nennenswerten Änderungen an diesem Projekt stehen hier. Das Format folgt
[Keep a Changelog](https://keepachangelog.com/de/1.1.0/), die Versionsnummern folgen
[Semantic Versioning](https://semver.org/lang/de/).

Die Versionsnummer selbst steht an genau einer Stelle: im Element `Version` in
`Directory.Build.props`. Setup und Veröffentlichungslauf lesen sie dort.

---

## [0.3.5] — 2026-09-14

### Behoben — das Logo auf der Über-Seite war weg

Die Datei war noch da und im Projekt eingetragen, aber **beschädigt**: Aus der PNG-Kennung
`89 50 4E 47 0D 0A 1A 0A` war `89 50 4E 47 0A 1A 0A 00` geworden, die Datei genau ein Byte
kürzer. Ein projektweites Suchen und Ersetzen (Commit `41873d2`, „Version statt Fassung") war
über die Binärdatei gelaufen und hatte ein Wagenrücklaufzeichen entfernt.

Git trifft keine Schuld — `*.png binary` in `.gitattributes` greift korrekt; die Ersetzung
selbst hat die Datei angefasst. Wiederhergestellt aus `d909203`, dem letzten heilen Stand:
742×258, RGBA, alle Blockprüfsummen stimmen.

**Das Tückische war nicht der Fehler, sondern sein Ausbleiben.** WPF meldet ein unlesbares Bild
nicht — `Image` zeigt schlicht nichts. Bau und Testsammlung blieben grün, und die beschädigte
Datei überstand **zwei Veröffentlichungen**, bis sie jemandem am Bildschirm auffiel.

- **Neu: `BildmaterialTests`.** Prüft nicht, ob eine Datei *vorhanden* ist — das war sie ja —,
  sondern ob sie *heil* ist: PNG-Kennung, jede Blockprüfsumme, Masse und Farbtyp, beim Icon die
  Verzeichniseinträge gegen die Dateilänge. Geprüft wird die **eingebettete Ressource**, also
  das, was der Techniker wirklich bekommt, nicht die Datei im Quellbaum.
- Der Wächter ist gegengeprüft: Mit dem nachgestellten Schaden schlägt er an und nennt die
  Ursache im Klartext; mit der heilen Datei ist er still.
- Auch `tray.ico` ist geprüft. Es war unversehrt.

---

## [0.3.4] — 2026-09-14

### Behoben — die Aufzeichnung auf einem Aufbau mit mehreren Bildschirmen

**Gemessen an einem echten Arbeitsplatz** mit drei Bildschirmen: einer bei **X = −1920**, einer
bei 0 (der Hauptbildschirm, also **in der Mitte**) und einer bei 1920; der linke ist mit 1200
Bildpunkten **höher** als die beiden anderen mit 1080. Geprüft wurde nicht der Rückgabewert,
sondern der Bildpunkt im fertigen Video.

- **Das Bild rutschte mitten in der Aufzeichnung um 1920 Bildpunkte zur Seite**, obwohl sich
  kein Fenster bewegt hatte. Der Direktor entschied über einen Bildschirmwechsel mit der Frage
  „sitzt die Leinwand auf dem Ursprung dieses Bildschirms?“. Eine Leinwand über zwei
  Bildschirme sitzt auf dem Ursprung des **linkesten** — hier bei −1920 —, der Bildschirm mit
  der grössten Überdeckung war aber der Hauptbildschirm bei 0/0. Gemessen: Bei t = 2 s standen
  beide Fenster im Bild, bei t = 3 s war das Fenster auf dem linken Bildschirm aus dem Bild
  gefallen und ein Drittel der Leinwand dauerhaft schwarz. Entschieden wird jetzt danach, ob die
  Leinwand den Bildschirm **schon zeigt** (`CanvasLayout.Covers`).
- **Im Bildschirmbetrieb waren zwei Drittel des Bildes schwarz.** Dort nimmt der Rekorder einen
  einzigen Bildschirm auf und setzt ihn bündig bei 0/0 auf die Leinwand; die Leinwand wurde aber
  als Hüllfläche **aller** berührten Bildschirme angelegt. Gemessen: eine Datei von 3840×1200,
  deren Mitte des grossen Fensters sich als 0/0/0 las. Im Bildschirmbetrieb gilt jetzt
  `CanvasLayout.ForScreen` — genau ein Bildschirm, und zwar der mit der grössten Überdeckung.
- **Der aufzunehmende Bildschirm wird über die Fläche bestimmt**, nicht über den Ursprung der
  Leinwand und nicht über die Reihenfolge der Aufzählung. Am echten Aufbau gemessen zählt
  Windows die Bildschirme weder von links nach rechts noch mit dem Hauptbildschirm zuerst auf;
  bei einer Sitzung auf dem mittleren Bildschirm lief so der linke mit.

**Was nachweislich schon stimmte** und jetzt durch Prüfungen festgehalten ist: Ein Fenster bei
negativem X wird vollständig aufgenommen, die Leinwand bekommt die Höhe **seines** Bildschirms
(1200, nicht die 1080 des Hauptbildschirms), und ein Fenster über einer Bildschirmgrenze steht
im Video auf beiden Hälften.

**Neu geprüft.** `MehrschirmAufzeichnungTests` misst am echten Aufbau bis zum Bildpunkt — das
Video wird dafür mit dem Decoder von Windows geöffnet (`Mp4Frame`), die Bildgrösse aus dem
`tkhd`-Block der Datei gelesen (`Mp4Duration.SizeOf`). `DreiSchirmeLageTests` rechnet dieselbe
Lage gegen erfundene Bildschirme durch, damit ein Rückfall auch auf einem Einschirmrechner
auffällt.

### Behoben — drei Tests blockierten jede Veröffentlichung

Auf einem GitHub-Läufer scheiterten drei Fälle, lokal liefen alle drei. Damit kam die
Werkstrecke nie bis zum Release.

**Die Ursache ist auf dem Läufer selbst gemessen**, nicht geraten: Ein Läufer arbeitet in
Sitzung 2 auf `WinSta0\Default` **ohne DWM-Komposition**. Zwei der Fälle setzen ein
aufnehmbares Bild voraus — nicht ihre Zusicherung war falsch, ihnen fehlte die Voraussetzung.

- **`[LiveScreenFact]`** übernimmt das Muster, das die Live-Fälle im Projekt schon benutzen:
  Fehlt die Voraussetzung, wird der Fall mit einem Grund übersprungen, der im Protokoll steht.
  Auf einem Rechner mit Bildschirm läuft er Zeile für Zeile unverändert. Ein zweiter
  Mechanismus dafür ist nicht entstanden.
- **`Das_Beenden_wartet_auf_eine_laufende_Abfrage`** hing nicht an der Datenbank, sondern am
  Strangpool: Auf einer geteilten Maschine kam die Abfrage nicht rechtzeitig los. Die Abfrage
  läuft jetzt auf einem eigenen Strang, und beide Abhängigkeiten von der Wanduhr sind weg —
  statt die Zeitgrenze hochzusetzen. Der Fall wird nirgends übersprungen und sichert jetzt
  **schärfer** zu als vorher: `Dispose` kehrt nachweislich nicht zurück, solange eine Abfrage
  läuft.

Auf einem Arbeitsplatz mit Bildschirm wird damit weiterhin kein einziger Fall übersprungen.

---

## [0.3.3] — 2026-09-14

### Behoben — beim Autostart fehlte das Symbol im Infobereich

**Der Befund.** Der Autostart lief, aber das Symbol kam nicht mit — das Werkzeug war nicht zu
bedienen. Gemessen an den Fenstern des laufenden Prozesses: Mit `--minimized` führte er kein
einziges Fenster der Klasse `H.NotifyIcon_*`, ohne den Schalter schon.

**Die Ursache war der Ort des Symbols.** Es stand in `MainWindow.xaml`, gehörte also einem
Fenster. Beim Autostart rief der Start `Hide()` auf einem Fenster, das nie gezeigt worden war
— und ein solches Fenster löst sein `Loaded`-Ereignis nicht aus. Daran hingen beide Stücke, die
gefehlt haben: das Anmelden des Symbols bei Windows und die Anzeige, aus der sein Hinweistext
kommt. Das Werkzeug lief damit unsichtbar **und** unerreichbar.

- **Das Symbol gehört jetzt der Anwendung** (`Views/AppTrayIcon.xaml`). Es entsteht in
  `App.OnStartup` und wird mit `ForceCreate` ausdrücklich bei Windows angemeldet, bevor von
  einem Fenster überhaupt die Rede ist. Kein `Show()` mit sofortigem `Hide()`: Das liesse beim
  Anmelden an Windows jedes Mal ein Fenster aufblitzen, und der Autostart ist der Normalbetrieb.
- **Der Hinweistext hängt nicht mehr am Fenster.** Die Anzeige dahinter gehört ebenfalls der
  Anwendung; Symbol und Fußzeile teilen sich dieselbe. Er sagt den Zustand also auch dann, wenn
  nie ein Fenster offen war — genau der Fall, um den es geht.
- **Das Hauptfenster entsteht erst, wenn es gebraucht wird.** „Fenster öffnen“ am Symbol baut
  es beim ersten Mal. Beim Autostart liegt damit kein halb gebautes Fenster mehr herum.
- **Beendet wird jetzt ausdrücklich** (`ShutdownMode.OnExplicitShutdown`). Ohne das hätte der
  erste Abschlussdialog, den der Techniker schliesst, die Überwachung mitgenommen: In der
  Voreinstellung endet eine WPF-Anwendung, sobald ihr letztes Fenster zugeht — und beim
  Autostart gibt es keines.
- **Der Zustand ist am Symbol ablesbar.** Das Kontextmenü führt ihn in Worten und hält einen
  Eintrag „Einrichten“ bereit, solange nichts eingerichtet ist. Der Einrichtungsassistent
  drängt sich beim Autostart weiterhin **nicht** von selbst auf — ein Assistent, der beim
  Anmelden ungefragt aufgeht, ist zudringlich, und beim Autostart steht meist niemand vor dem
  Rechner. Ohne den Eintrag wäre „nicht eingerichtet“ aber eine Meldung ohne Ausweg gewesen.
- **Der Pfad zum Sinnbild nennt seine Assembly.** Die kurze Form sucht im Einstiegsprogramm und
  scheiterte ausserhalb des Betriebs mit „Die Ressource assets/tray.ico kann nicht gefunden
  werden“.
- **Beim Abmelden werden zuerst die Bindungen gelöst.** WPF hängt eine Bindung erst in einem
  späteren Durchlauf an; fällt der hinter das Abmelden, schreibt er in ein abgemeldetes Symbol.
  Gemessen als `ObjectDisposedException` aus `TaskbarIcon.WriteToolTipSettings`, die einen
  ganzen Prozess mitnahm.

### Geändert — ein zweiter Start holt das Fenster der ersten Instanz nach vorn

Bisher zeigte ein zweiter Start nur die Meldung „läuft bereits“. Das war wahr, liess den
Benutzer aber das Symbol suchen — und solange es keines gab, suchte er vergebens.

- **Von Hand gestartet:** Die erste Instanz zeigt ihr Fenster. Wer das Werkzeug startet, will es
  sehen.
- **Aus dem Autostart (`--minimized`):** schweigend, ohne Fenster und ohne Meldung. Dort ist ein
  zweiter Start keine Absicht, sondern eine Doppelung — etwa Autostart-Verknüpfung und
  Aufgabenplanung nebeneinander. Ein Fenster oder eine Meldung, die sich beim Anmelden vor alles
  schiebt, wäre genau die Zudringlichkeit, die der Autostart vermeiden soll.
- Die Meldung bleibt als Rückfallebene, wenn die erste Instanz nicht zu erreichen ist.

---

## [0.3.2] — 2026-09-13

### Behoben — ein laufender Ladevorgang überlebt den Seitenwechsel

**Der Befund.** Wer eine neue Version herunterlädt, dann die Seite wechselt und zurückkommt,
sah keinen Fortschritt mehr — sondern eine Schaltfläche, die zum zweiten Mal einlud. Der zweite
Vorgang scheiterte dann daran, dass die Datei noch offen war.

**Die Ursache war der Ort des Zustands.** „Läuft gerade“, „wie weit“ und „wo liegt die Datei“
standen im Ansichtsmodell der Seite „Einstellungen“ — und das wird beim Verlassen der Seite
verworfen. Beim Zurückkommen entstand ein neues, das von nichts wusste.

- Der Ladezustand gehört jetzt dem Aktualisierungsdienst, der die Anwendung überdauert. Die
  Seite meldet sich an, liest beim Öffnen den Stand von jetzt und meldet sich beim Verlassen
  wieder ab. Ein laufender Vorgang ist damit nach dem Zurückkommen sofort wieder sichtbar.
- **Ein zweiter Anstoss wird abgelehnt**, solange einer läuft — und sagt das, statt an der
  offenen Datei zu scheitern.
- Auch ein **Fehlschlag** gibt den Weg wieder frei. Ohne das bliebe nach dem ersten misslungenen
  Versuch jeder weitere gesperrt, ohne dass jemand sagen könnte warum.
- Der Fortschritt wird unmittelbar gemeldet statt über `Progress<T>`: Das stellt jede Meldung
  in die Warteschlange eines Arbeitsstrangs, und ohne Synchronisierungskontext ist die
  Reihenfolge zweier Meldungen nicht zugesichert — der Balken konnte zurückspringen. Gemeldet
  wird nur bei ganzen Prozent; bei hundert Megabyte wären es sonst über tausend Meldungen.

---

## [0.3.1] — 2026-09-13

### Entfernt — die Entwicklerdokumentation wird nicht mehr ausgeliefert

Das Setup enthielt sechs `.xml`-Dateien: die aus den Quelltextkommentaren erzeugte
Dokumentation aller Baugruppen. Sie wird zur Laufzeit von nichts gelesen — sie ist
ausschliesslich für die Entwicklungsumgebung da, wenn jemand diese Baugruppen als Verweis
einbindet. Damit lag sie ohne Grund auf jedem Rechner, auf dem installiert wurde.

Der Veröffentlichungslauf entfernt sie jetzt aus der Nutzlast, genau wie die Symboldateien;
der Installer schliesst sie zusätzlich aus. Das Setup wird dadurch auch kleiner.

### Geändert — Dokumentation und Kommentare durchgesehen

Rund vierzig Stellen in Kommentaren, XML-Dokumentation und den Begleittexten sind neu
formuliert. Die sachliche Begründung — warum der Code ist, wie er ist — steht unverändert
dort; geändert hat sich nur, wie sie erzählt wird. Kein Verhalten ist davon berührt.

### Hinzugefügt — Plakette am Block „Rechtschreibung und Ausformulieren“

Auf der Seite „Einstellungen“ steht neben der Überschrift jetzt ein Wort: *eingeschaltet*,
*gesperrt* oder *aus*. Der Satz darunter sagte dasselbe, brauchte dafür aber drei Zeilen —
und die Frage beim Überfliegen lautet nur: an oder aus?

„Gesperrt“ ist dabei etwas anderes als „aus“: Ohne Einwilligung *kann* nichts übermittelt
werden, mit Einwilligung und abgeschalteter Unterstützung *soll* es nur gerade nicht.

### Behoben — im Fenster zur Sprachmodell-Unterstützung war kein Anbieter ausgewählt

Beim Öffnen stand die Auswahl leer, obwohl in der Konfiguration ein Anbieter eingetragen war.
Beide Auswahlknöpfe hingen an demselben Wahrheitswert, einer davon über einen invertierenden
Umsetzer; wählt WPF einen Knopf der Gruppe ab, läuft dieser Wert durch den Umsetzer zurück in
die Quelle, und welcher Knopf zuerst gebunden wird, entscheidet über das Ergebnis. Jeder Knopf
hat jetzt seine eigene Eigenschaft, und der Setzer handelt nur auf „angewählt“.

### Geändert — das Fenster schliesst sich nach dem Speichern

Aus „Schliessen“ wird **„Abbrechen“**, und **„Speichern“** schliesst das Fenster gleich mit —
ein Klick weniger.

Misslingt das Speichern, bleibt es offen und nennt den Grund. Dabei fiel auf, dass ein
gescheitertes Schreiben der Konfigurationsdatei bisher nur eine Meldung setzte, aber nicht als
Fehlschlag galt; das Fenster hätte sich darüber hinweg geschlossen.

---

## [0.3.0] — 2026-09-13

### Geändert — „Später“ parkt, statt sofort zu senden

**Der Befund.** Der Techniker klickte „Später“ und sah in der Warteschlange nichts. Die
Protokollspur zeigte, warum: Die Zeile wurde freigegeben, war damit sofort fällig und ging
**eine Sekunde später** hinaus — mit der automatischen Beschreibung. Eine Schaltfläche namens
„Später“, die sofort sendet, sagt das Gegenteil dessen, was sie tut.

- „Später“ reiht jetzt **wartend** ein. Die Sitzung bleibt in der Warteschlange, bis jemand
  entscheidet — unbefristet, wie ein unbeantworteter Dialog.
- Neu in der Warteschlange: `Hold` als genaues Gegenstück zu `Release`, mit derselben
  Bedingung `state = 'pending'` in der Anweisung selbst.
- **Der Preis steht dazu:** Eine geparkte Sitzung geht von selbst nie hinaus. Wer nie
  zurückkehrt, hat unverbuchte Arbeitszeit liegen; sichtbar ist sie in der Warteschlange.

### Hinzugefügt — wartende Einträge lassen sich nachträglich ändern

Solange ein Eintrag wartet, ist er nicht in TANSS — der Bericht lässt sich also noch ändern.
Bisher ging das nur beim nächsten Programmstart, wenn der Dialog von selbst wieder kam.

- Ein Stift-Symbol je Zeile legt den Abschlussdialog erneut vor. Gerufen wird derselbe Weg wie
  bei der Wiederherstellung nach einem Start — samt Riegel gegen zwei gleichzeitige Dialoge.
- Geschrieben wird ausschliesslich auf wartende Zeilen. Was unterwegs, gesendet oder aufgegeben
  ist, wird **mit Grund** abgelehnt statt stillschweigend zu nichts zu führen.

### Hinzugefügt — die Zustandsplakette sagt jetzt, was sie meint

Die Fußzeile meldete „verbunden, mit Warnung“, und nirgends stand, welche. Die Warnungen wurden
erhoben, in den Betriebszustand gelegt und nie gezeigt.

- Ein Klick auf die Plakette öffnet eine Auskunft mit Titel, Grund und Vorschlag je Warnung.
  Gibt es keine, sagt sie das — ein Klick, der nichts tut, sieht aus wie ein Fehler.
- Neu daneben: ein Hinweis auf eine verfügbare neue Version. Geprüft wurde schon immer täglich;
  gesagt wurde es bisher nur auf einer Seite, die im Betrieb niemand öffnet. Der Hinweis führt
  dorthin, wo sich die Version holen lässt.
- Der Verbindungstest meldet Erfolg jetzt grün mit Häkchen statt blau mit „i“.

### Entfernt — `recording.segment_minutes`

Der Zeittakt für neue Videodateien ist entfallen, seit eine Sitzung genau eine Datei ergibt.
Die Einstellung stand noch im Schema und erzeugte eine Dauerwarnung, zu der es nichts zu tun
gab — eine Warnung, auf die keine Handlung folgt, stumpft alle übrigen ab.

- Der Schlüssel ist aus dem Schema verschwunden. Eine bestehende `config.json` läuft trotzdem:
  `ConfigStore` räumt stillgelegte Schlüssel aus dem Text, bevor er abgebildet wird, und beim
  nächsten Speichern ist er fort.
- **Die Strenge bleibt:** Geräumt wird genau die Liste der stillgelegten Schlüssel. Ein
  vertippter fällt weiterhin auf.

### Behoben — zwei Stunden Differenz in jedem Bericht

In jedem Text, der zum Kunden geht, stand eine Zeitspanne, die rückwärts lief:

```
Zeitraum:     13.09.2026 15:52 – 13:52 (unter 1 Minute)
```

Beide Zeitpunkte waren richtig — sie trugen nur verschiedene Zonen. Der Beginn einer Sitzung ist
die Startzeit ihres Prozesses und kommt in **Ortszeit** herein, das Ende setzt die
Zustandsmaschine in **UTC**. Die Differenz rechnet `DateTimeOffset` richtig aus, deshalb stimmte
die Dauer; `ToString("HH:mm")` zeigt aber jeden Wert in seiner eigenen Zone. Die Anzeige rechnet
jetzt zuerst auf Ortszeit um — beim Datum wog es schwerer als bei der Uhrzeit: Eine Sitzung um
00:30 stand in UTC noch am Vortag.

### Behoben — der Abschlussdialog riss den Vordergrund an sich

Das Fenster geht auf, während jemand arbeitet — eine Fernwartung endet mitten im Tippen. Es holte
den Vordergrund, und der nächste Tastendruck traf einen seiner Knöpfe. `ShowActivated="False"`,
kein `Activate()`: Das Fenster steht weiterhin über allem, nimmt aber die Tastatur nicht mit.

- Nach einer geglückten Buchung schliesst es sich von selbst. Nicht beim Klick, sondern erst
  wenn TANSS geantwortet hat — und **nicht**, wenn die Mitarbeiterzuordnung unbestätigt blieb:
  Dieser eine Satz muss gelesen werden.
- Das Berichtsfeld hat keine feste Höhe mehr; sein unterer Rand hängt am Fensterrahmen und
  wächst beim Ziehen mit.

### Behoben — ein Prüffall, der unter Last launisch war

`VideoFileTests` schrieb 200 Bilder und hoffte, der Kodierer habe in dieser Zeit ein Bruchstück
geschrieben — eine Wette, keine Zusage. Unter voller Last verlor sie gelegentlich. Der Prüffall
wartet jetzt auf die Tatsache statt auf eine Dauer.


### Geändert — gebucht wird aus dem Abschlussdialog heraus, und kein Zeitablauf bucht mehr

**Der Befund.** Eine Fernwartung stand in TANSS, ohne dass jemand gefragt worden war — weder
nach Ticket noch nach Kunde oder Gerät. Die Ursache war keine kaputte Mechanik, sondern die
Mechanik selbst: Die Sitzung wurde beim Sitzungsende eingereiht und fünf Minuten
zurückgehalten; lief die Frist ab, ging sie **von selbst** hinaus, mit der automatischen
Beschreibung („Microsoft Remotedesktop: sap“) statt mit einem Bericht. In der
Zustandsdatenbank dieses Arbeitsplatzes ist genau das nachweisbar: die eine Zeile, an der
niemand den Dialog bestätigt hat, trug volle 300 Sekunden und ging exakt fünf Minuten später
hinaus.

- **Die Schonfrist ist ersatzlos fort.** An ihre Stelle tritt ein Kennzeichen in der
  Warteschlange (`queue.awaiting_decision`, Datenbankstand 6): Die Zeile entsteht beim
  Sitzungsende weiterhin sofort — sie ist die Absturzsicherung —, aber **keine Uhr gibt sie
  frei**. Aufgehoben wird das Warten allein durch eine Entscheidung.
- **„In TANSS buchen“ sendet jetzt sofort**, und zwar über denselben Weg wie der Sendedienst,
  samt Existenzprüfung — TANSS dedupliziert nicht. Die von TANSS vergebene Kennung steht
  danach im Fenster, und das Fenster bleibt offen, bis es geschlossen wird.
- **Scheitert das Senden** — keine Verbindung, TANSS antwortet nicht —, bleibt die Zeile
  stehen und wird vom Sendedienst wiederholt. Das ist ab jetzt der einzige Zweck der
  Warteschlange: der Fehlerpfad. Der Dialog sagt das und behauptet keinen Erfolg.
- **„Später“** parkt die Sitzung in der Warteschlange: Sie wartet dort auf eine Entscheidung
  und geht von selbst nicht hinaus. **„Verwerfen“** entfernt sie wie bisher, mit
  Protokolleintrag.
- **Eine unbeantwortete Sitzung** — Absturz, Feierabend, weggeklicktes Fenster — bleibt liegen
  und wird **beim nächsten Start erneut vorgelegt**. Dieser Weg verliert keine Arbeitszeit und
  bucht nichts ungefragt. Der andere denkbare Weg, beim Beenden hinauszuschicken, was noch
  wartet, hätte am Freitagabend jede Sitzung ohne Bericht gebucht — und den häufigsten Fall,
  den Absturz, gar nicht erfasst.
- In der Warteschlange heisst eine solche Zeile jetzt „wartet auf Entscheidung“ und sagt, dass
  sie ohne Antwort nicht hinausgeht. „Jetzt senden“ schickt sie auf ausdrückliches Verlangen
  mit der automatischen Beschreibung los.

### Behoben — der Abschlussdialog konnte sich stillschweigend selbst aussperren

- Der Riegel gegen zwei gleichzeitige Dialoge wurde gesetzt, **bevor** das Fenster gebaut war.
  Wäre der Bau je gescheitert, bliebe er für die restliche Laufzeit stehen: kein
  Abschlussdialog mehr, und jede weitere Sitzung verlöre still ihren Bericht. Der Riegel steht
  jetzt in `DialogGate` — er wird erst gesetzt, wenn das Fenster steht, ein Fehlschlag geht
  mit Grund ins Änderungsprotokoll (`dialog.failed`), und der nächste wartende Dialog kommt
  trotzdem dran. *Ob dieser Fall je eingetreten ist, ist unbelegt: Der Weg dorthin schluckt
  Ausnahmen ohne Protokollzeile.*
- **Das Protokoll sagt jetzt, was mit dem Dialog geschah**: `dialog.shown`, `dialog.booked`
  (samt TANSS-Kennung), `dialog.later`, `dialog.deferred`, `dialog.reoffer`, `dialog.failed`.
  Bis heute liess sich die Frage „ging er überhaupt auf?“ nur über den Umweg des
  Nutzlasttextes beantworten.
- **`--show-save-dialog` tut endlich etwas.** Der Schalter stand seit jeher in der
  Dokumentation und wurde nirgends ausgewertet. Gezeigt wird eine erkennbar **erfundene**
  Sitzung, die in keiner Warteschlange steht: zum Begutachten der Darstellung, nicht zum
  Buchen.
- **Das Fenster passt wieder auf den Bildschirm.** Die feste Höhe von 700 war auf dem
  Arbeitsplatz des Technikers gemessen zu viel — das Fenster ragte 32 Bildpunkte unter den
  Bildschirmrand, und seine Fussleiste mit „Verwerfen / Später / In TANSS buchen“ lag im
  Streifen der Taskleiste. Ein Klick auf die Taskleiste traf damit einen Knopf dieses
  Fensters. Jetzt: Höhe aus dem Inhalt, Obergrenze die Arbeitsfläche, Inhalt in einem
  Rollbereich, und das Fenster lässt sich wieder in der Grösse ziehen.


### Hinzugefügt — Bildschirmaufzeichnung, zweiter Teil: die Kette steht

Aus einem echten Fenster wird eine abspielbare Datei. Geprüft wird das nicht gegen Attrappen,
sondern gegen echte Fenster, die der Testlauf selbst öffnet, und gegen die Datei, die danach auf
der Platte liegt.

**Der Bildfang**

- `WindowCapture` nimmt genau ein Fenster auf. Gemessen und hier festgehalten: Ein fremdes
  Fenster darüber landet **nicht** im Bild — das ist der Grund, warum Fenster und nicht
  Bildschirme aufgezeichnet werden. Sonst stünde das Mailfenster des Technikers in der
  Dokumentation des Kunden.
- Ein minimiertes Fenster liefert keine Bilder mehr, ohne dass die Aufnahme endet. **Die Pause
  entsteht daraus von selbst** — es braucht dafür keine eigene Mechanik.
- Zwei Umwege, beide durch einen Laufzeitfehler erzwungen und beide belegt: Weder die
  Aktivierungsfabrik noch die aufgenommene Oberfläche lassen sich in .NET auf eine klassische
  COM-Schnittstelle umwandeln — die Umwandlung wirft. Gegangen wird deshalb über die
  Funktionstabelle. Und die Kennung der Laufzeitklasse ist **nicht** die der Schnittstelle: mit
  der falschen antwortet Windows mit `E_NOINTERFACE`, und die Meldung klingt dann nach einem
  gesperrten Fenster.

**Die Kodierung**

- `VideoFile` schreibt H.264 über Media Foundation. Die Kodiereinstellungen gehen über
  `SetInputMediaType` und nirgendwo sonst — der naheliegende Weg über `ICodecAPI` meldet Erfolg
  und tut nachweislich nichts.
- `Complete` ist ausdrücklich nicht `Dispose`: Ohne Abschluss schreibt Media Foundation keinen
  Index, und heraus kommt eine Datei, die kein Abspieler öffnet. Ein Test vergleicht beide
  Fälle.
- **Die Dauer wird aus der Datei gelesen, nicht aus unserer Buchführung.** Vierzig Bilder bei
  vier je Sekunde ergeben zehn Sekunden im `mvhd`-Block; zwanzig ergeben fünf. Prüfte man gegen
  die eigene Uhr, prüfte man die Rechnung gegen sich selbst.

**Das Zusammensetzen**

- `SessionRecorder` führt aus, was der Direktor beschliesst — hier steht kein `if` über Beginn,
  Pause oder Abschnittswechsel. Jedes Fenster wird für sich aufgenommen und an seinen Platz auf
  einer gemeinsamen Leinwand kopiert, nie skaliert.
- Ein Fenster ohne neues Bild hinterlässt **Schwarz** und nicht sein letztes. Ein
  stehengebliebenes Bild behauptete, dort sei noch etwas zu sehen.
- Ein Fenster, das sich der Aufnahme entzieht — ein Programm mit erhöhten Rechten —, kostet
  seinen Platz auf der Leinwand und nicht die ganze Aufzeichnung.
- Behoben, gefunden vom eigenen Test: Eine Pause, die bis zum Ende offen blieb, tauchte in der
  Aufstellung nicht auf. Sie wird erst beim Fortsetzen eingetragen — und ausgerechnet die
  letzte ist häufig die längste.

### Hinzugefügt — Bildschirmaufzeichnung, dritter Teil: sie läuft von selbst

Die Aufzeichnung hängt jetzt an der Sitzungsbeobachtung. Beginnt eine Fernwartung, beginnt das
Video; endet sie, wird die Datei abgeschlossen und die Begleitdatei danebengelegt. Und es gibt
einen Schalter in der Oberfläche — jetzt, wo etwas dahintersteht.

**Die Ablage**

- `recordings` in `state.db` (Schemastand 3): Jeder Abschnitt steht dort ab dem Augenblick, in
  dem er begonnen wird. Beim Beginn und nicht beim Ende, denn ein Absturz mitten im Schreiben
  hinterliesse sonst eine Waise — niemand wüsste, zu welcher Sitzung sie gehört und wann sie zu
  löschen wäre.
- Die Zeile bleibt als **Grabstein** stehen, wenn die Datei gelöscht ist. Für eine Auskunft nach
  Art. 15 DSGVO ist zu sagen, was es gab und wann es verschwand; eine gelöschte Zeile könnte das
  nicht.
- `RecordingPaths` — **im Pfad steht kein Kundenname.** Ein Dateipfad wandert in Sicherungsläufe,
  Suchindizes und jede Fehlermeldung. Wer wissen will, zu wem eine Aufzeichnung gehört, findet es
  in der Begleitdatei: dort, wo es mit der Aufzeichnung zusammen gelöscht wird.
- `RecordingManifest` — die Begleitdatei `sitzung.json` neben den Videodateien. Sie erklärt die
  Lücke zwischen Video und Sitzung (zwanzig Minuten Video zu fünfundvierzig Minuten Fernwartung
  sähen sonst nach einer abgebrochenen Aufzeichnung aus), nennt das Löschdatum und trägt die
  Rechtsgrundlage mit.
- `RecordingCleaner` — gelöscht wird nur, was **in der Buchführung steht** und **unterhalb der
  eingestellten Wurzel liegt**. Beide Prüfungen sind nötig: Die erste schützt Dateien, die jemand
  von Hand hineingelegt hat, die zweite alles andere auf der Platte vor einem Eintrag mit einem
  Pfad, der dort nicht hingehört. Bleibt kein Video übrig, geht die Begleitdatei mit.

**Der Dienst**

- `RecordingService` als vierter Hintergrunddienst. Er zeichnet **nichts auf, was nicht gebucht
  wird**: Eine Sitzung ohne Zuordnung auf einen Fernwartungstyp geht nirgendwohin, und ein Video
  davon wäre personenbezogene Daten ohne den Vorgang, zu dem sie gehören. Bleibt eine
  Aufzeichnung aus, steht der Grund im Änderungsprotokoll — eine, die stillschweigend ausbleibt,
  fällt erst an dem Tag auf, an dem jemand sie sucht.
- Jede Sitzung wird auf einem **eigenen Strang** aufgezeichnet. Die Zeitachse der Datei hängt
  daran, wie gleichmässig getaktet wird, und ein Dienst, der nebenbei aufräumt und meldet, taktet
  nicht gleichmässig.
- **Das Aufräumen läuft auch dann, wenn nicht aufgezeichnet wird** — beim Start und danach
  stündlich. Was ein früherer Lauf hinterlassen hat, muss gelöscht werden, wenn seine Frist
  abläuft. Eine Löschfrist, die sich abschalten liesse, wäre keine.

**Die Oberfläche**

- Neue Seite „Aufzeichnung“: Schalter, Kenntnisnahme, Ordner, Löschfrist und die Feinheiten.
  Der Hinweis, was dabei entsteht, steht **vor** dem Schalter — wer einschaltet, soll vorher
  gelesen haben, dass der Bildschirm des Kunden und der des Technikers aufgezeichnet werden und
  dass Letzteres nach §87 Abs. 1 Nr. 6 BetrVG mitbestimmungspflichtig ist.
- Die Rechtsgrundlage ist **nicht vorbelegt**. Eine vorausgefüllte Auswahl wäre eine Behauptung
  des Werkzeugs über einen Sachverhalt, den nur der Betrieb kennt.
- `--page=recording` für den Start auf dieser Seite.

**Behoben — die Zeitachse log bei stehendem Bildschirm**

Der Zeitstempel eines Bildes lag bei `Bildnummer / Bildrate`. Das trägt nur, solange bei jedem
Takt ein Bild geschrieben wird — Bilder werden aber nur geschrieben, wenn sich etwas geändert
hat. Ein stehender Bildschirm hätte eine Viertelstunde Lesen auf wenige Sekunden Video
zusammengeschnurrt, und der Abspieler hätte eine Dauer gezeigt, die es nie gab. Der Zeitstempel
ist jetzt die aufgezeichnete Zeit selbst: Wanduhr minus Pausen. Gemessen und festgehalten: Die
Dauer im `mvhd`-Block einer mp4 folgt den **Zeitstempeln** und nicht den angegebenen Standzeiten
— fünf Bilder im Abstand von zwei Sekunden ergeben eine Datei von acht Sekunden.

**Geprüft**

Neues Testprojekt `TanssLogWatcher.App.Tests`: Aus einem echten Fenster wird eine echte Datei,
ein Eintrag in einer echten Datenbank und eine Begleitdatei — und der Aufräumer holt nach Ablauf
der Frist alles wieder ab. Was die Umgebung nicht hergibt, wird benannt und nicht umgangen: Auf
einem Rechner ohne Bildschirmaufnahme werden diese Fälle ausdrücklich übersprungen.

### Hinzugefügt — TANSS ordnet die Firma jetzt selbst zu

Das Werkzeug schickt bei jeder Fernwartung eine **Gerätekennung** mit. Ist zu ihr in TANSS eine
Zuordnung hinterlegt, setzt TANSS die Firma selbst ein — ohne dass das Werkzeug eine
Firmenliste führen müsste.

**Gemessen am 13.09.2026 gegen eine Produktivinstanz der Version 10.10.0**, über genau die
Route, die das Werkzeug benutzt: mit hinterlegter Zuordnung kam die Firma zurück, ohne
Zuordnung eine 0. Die Gegenprobe schliesst aus, dass die Firma anderswoher stammte. Beide
angelegten Datensätze wurden im selben Durchlauf wieder entfernt.

- **Das Feld war die ganze Zeit da.** `RemoteSupportWrite.DeviceId` steht seit der ersten
  Version im Modell und wurde nie gefüllt; bei jeder Fernwartung ging ein leeres
  `"deviceId": ""` hinaus. Allein deshalb hat die Übersetzung nie gegriffen.
- **Gebildet wird die Kennung an einer Stelle**, `DeviceIdentity` — und dort steht der Riegel.
  Gesperrt ist der Platzhalter „kein Titel ermittelt“: Eine Zuordnung auf ihn träfe künftig
  **jede** unaufgelöste Sitzung, quer über alle Kunden.
- **Ob ein Profil eine Kennung liefert, entscheidet der Profilkatalog**, nicht eine Namensliste
  daneben: `MonitoringProfile.YieldsDeviceIdentity`, voreingestellt `false`. 21 der 36 Profile
  tragen es. Nicht dabei sind die zehn, deren Ziel der ganze Fenstertitel ist, die beiden
  Outlook-Profile (Nachrichtenbetreff), `devenv` und `Code` (Projektmappe oder Datei) und
  `Zoom` (eine Besprechung ist kein Gerät).
- **Unverändert oder gar nicht.** Wie TANSS vergleicht, ist nicht gemessen; die Kennung wird
  deshalb nicht kleingeschrieben, nicht von Domänensuffixen befreit und nicht gekürzt. Was zu
  lang ist, ergibt **keine** Kennung statt einer abgeschnittenen, die auf nichts passt.
- Ohne Kennung geht das Feld gar nicht mehr mit. Zwei Tests hielten das leere `deviceId`
  fest — sie prüfen jetzt beide Richtungen.

**Noch offen:** Nach einem Neustart mitten in einer Sitzung geht die Kennung verloren — sie
steht nicht in `open_sessions`. Diese eine Sitzung bucht dann ohne Übersetzung.

### Behoben — der Wiederholungsriegel hing an einer einzigen Route

`TanssRoutes.HasSideEffectOnGet` prüfte auf `/api/v1/jwts/tanss_app`. Jede weitere Tokenart
unter `/jwts` wäre damit ungeschützt in die Wiederholung gelaufen — und TANSS 10.10.0 kann ein
ausgestelltes Token **nicht widerrufen**. Eine Zeitüberschreitung hätte mehrere, bis zu ein Jahr
gültige Token hinterlassen, von denen niemand erfährt.

Geprüft wird jetzt gegen das Präfix. Neun Testfälle halten das fest, darunter ausdrücklich eine
Tokenart, die es heute noch gar nicht gibt: Ein Riegel, der erst nachgetragen werden muss, ist
beim ersten Aufruf offen.

### Hinzugefügt — die Seite „Verlauf“

Abgeschlossene Sitzungen, jüngste zuerst: was gelaufen ist, ob es in TANSS steht und was von
der Aufzeichnung übrig ist. Vierter Eintrag der Navigation, direkt unter „Timer“.

**Sie brauchte eine eigene Tabelle, und das ist nachgemessen.** Aus dem Vorhandenen war ein
Verlauf nicht abzuleiten: Die Warteschlange verliert ihre Zeile beim Verwerfen und seit
heute auch nach der Frist, das Änderungsprotokoll kennt weder Ende noch Dauer noch
Fernwartungstyp, und `recordings` kennt nur, was aufgezeichnet wurde. Neu ist deshalb
`session_history` auf Schemastand 4.

- **Der Altbestand wurde einmalig nachgetragen** — aus Warteschlange und Änderungsprotokoll.
  Auf diesem Arbeitsplatz: alle fünfzehn Sitzungen, davon drei ohne feststellbaren Ausgang.
  Die stehen als „unbekannt“ da und nicht als geratener Ausgang.
- **Ein Abdruck ist keine Gegenstelle.** Der ursprüngliche Entwurf wollte bei unbekanntem Ziel
  den Abdruck zeigen. Nachgerechnet ist das der Abdruck des Platzhalters „kein Titel
  ermittelt“ — acht Hexziffern, die wie eine Kundenkennung aussehen und nichts bedeuten. Die
  Seite schreibt jetzt „Gegenstelle nicht ermittelt“.
- **Eine Sitzung hat nicht ein Video.** In dieser Datenbank liegt eine mit drei Abschnitten und
  drei verschiedenen Löschfristen. Der Abspielknopf hätte vier von fünfzehn Sekunden
  gespielt und nach einem Teil-Löschlauf auf eine gelöschte Datei gezeigt, während das
  Videosymbol noch stand. Die Zeile sagt jetzt „Abschnitt 1 von 3“.
- **Ein geschätztes Ende sagt, dass es geschätzt ist.** Stammt das Ende aus einem
  Protokolleintrag statt aus der Messung, steht „geschätzt“ unter der Dauer — gemessen liegt
  ein solcher Eintrag bis zu fünf Minuten nach dem tatsächlichen Ende.
- Die Frist des Verlaufs läuft im selben stündlichen Takt wie die übrigen, im `StatePruner`.
  Ein Verkürzen wirkt auf Bestehendes: `delete_after <= jetzt ODER started_at <= Grenze`.

### Geändert — Fensterbeschriftungen werden voreingestellt nicht mehr geschwärzt

`logging.redact_window_titles` steht jetzt auf `false`. Bei den Profilen, die im Betrieb
benutzt werden — Microsoft Remotedesktop, der Store-Client, PuTTY — trägt der Titel einen
Rechnernamen und sonst nichts; ein Abdruck kostet dort jede Nachvollziehbarkeit, im
Änderungsprotokoll wie auf der neuen Seite „Verlauf“, und verhindert wenig: Derselbe Name
steht ohnehin im Kommentar der Fernwartung bei TANSS.

- **Wer ein Profil benutzt, das mehr als einen Rechnernamen in den Titel nimmt, schaltet die
  Schwärzung ein.** Zehn Profile des Katalogs nehmen den ganzen Fenstertitel als Ziel
  (`cmd`, `powershell`, `RoyalTS`, `WindowsTerminal` und weitere), die beiden Outlook-Profile
  die Nachrichtenbetreffzeile.
- **Dabei berichtigt:** Die Anleitung behauptete, die Outlook-Profile seien „voreingestellt
  inaktiv“. Die mitgelieferte Beispielkonfiguration führt `OUTLOOK|Message` als erste aktive
  Zuordnung. Die Anleitung sagt das jetzt.
- Zwei Tests standen auf der alten Vorgabe statt auf ihrer eigenen Aussage und sind
  richtiggestellt: Sie setzen die Einstellung jetzt ausdrücklich, statt sie aus der Vorgabe zu
  holen — sonst prüften sie nach diesem Wechsel stillschweigend nur noch eine Richtung.

### Geändert — der Menüpunkt heißt jetzt „Einstellungen“

Die Seite hieß „Verbindung“, trägt aber Instanz, Zugangstoken, Sprachmodell-Unterstützung,
Aktualisierung und die Fernwartungsanbindungen. Von acht Blöcken haben drei mit der Verbindung
zu tun; zwei — Sprachmodell und Aktualisierung — haben mit TANSS überhaupt nichts zu tun. Der
Name war schon vorher zu eng.

- Der Menüpunkt heißt **„Einstellungen“**. Ebenso die Überschrift der Seite: Jede andere Seite
  trägt als Kopf genau ihre Navigationsbeschriftung, und wer „Einstellungen“ anklickt, soll
  nicht „Verbindung zu TANSS“ lesen.
- Das Zahnrad oben rechts, das den Einrichtungsassistenten öffnet, heißt jetzt
  **„Verbindung“** statt „Einstellungen“. Sonst hieße eine Schaltfläche genauso wie die Seite,
  auf der sie steht — und führte woanders hin. Der Satz „Verbindung zu TANSS“ steht damit nur
  noch dort, wo er wörtlich zutrifft: über Schritt 1 des Assistenten.
- **Elf sichtbare Sätze zeigten auf den alten Namen** — „Das Zahnrad unter ‚Verbindung‘ öffnet
  den Assistenten“, „Die Einrichtung steht unter ‚Verbindung‘“. Sie sind mitgezogen. Ein
  Wegweiser, der auf einen Namen zeigt, den es nicht mehr gibt, ist schlimmer als keiner.
- **Nicht angefasst**, obwohl das Wort darin steht: alles, was die Netzverbindung meint —
  „Verbindung prüfen“, „Beim Senden brach die Verbindung ab“, „offene Verbindungen“, der
  Verbindungstest der Kommandozeile. Geprüft wurden dazu alle 236 Fundstellen des Wortstamms
  im Baum, jede einzeln eingeordnet.
- Der Schalterwert `--page=connection` und die Klassennamen bleiben, wie sie sind: Eine
  Verknüpfung aus einem früheren Stand soll weiter treffen. Die Dokumentation sagt jetzt
  dazu, welche Seite damit gemeint ist.
- Berichtigt, dabei aufgefallen: Der Untertitel der Seite versprach „keine Daten außerhalb
  eures Hauses“ — zwei Karten tiefer steht, dass die Sprachmodell-Unterstützung genau das tut.
  Die Zusage steht jetzt da, wo sie gilt, und bezieht sich auf TANSS.

### Behoben — zwei Zusagen, die nur auf dem Papier standen

**Die Aufbewahrungsfristen liefen gar nicht.** `logging.retention_days` sagt seit der ersten
Version dreissig Tage zu. Die beiden Löschschritte waren gebaut und geprüft — im gesamten
Produktionscode rief sie aber niemand auf. Tatsächlich blieb alles liegen: jede Protokollzeile
und jeder erledigte Warteschlangeneintrag. Letzterer trägt die Nutzlast, die nach TANSS ging,
und darin die Gegenstelle im Klartext.

- Neu `StatePruner`: eine Klasse statt einer verborgenen Stelle im Dienst, damit sich die Frist
  prüfen lässt, ohne einen Dienst laufen zu lassen. Genau daran ist sie vorher gescheitert.
- Sie läuft stündlich im Sendedienst mit, am Ende des Takts — was seit Stunden fällig ist, darf
  keinen Sendeversuch aufhalten.
- **Eine Frist für beides.** Der erledigte Warteschlangeneintrag ist der Teil, der die
  Gegenstelle im Klartext trägt; ihn länger zu behalten als das Protokoll hiesse, die Zusage an
  der unangenehmsten Stelle nicht einzuhalten.
- **Wartende, zurückgestellte und aufgegebene Einträge sind ausgenommen**, gleich wie alt sie
  werden. Steht TANSS lange still, ist ein Eintrag irgendwann älter als die Frist — ihn dann
  fortzuräumen hiesse, genau die Arbeitszeit zu löschen, für deren Aufbewahrung die
  Warteschlange da ist. Ein Test hält das fest.
- Aufgeräumt wird nur vermerkt, wenn etwas geschah (`state.prune`). Ein stündliches
  „nichts zu tun“ wäre genau das Rauschen, das es gerade fortgeräumt hat.

**Das Verwerfen einer Sitzung hinterliess keine Spur.** Die Anleitung sagt wörtlich zu, auch das
Verwerfen werde protokolliert, und verweist Verzweifelte am Monatsende ausdrücklich darauf. Der
Abschlussdialog entfernte die Zeile aus der Warteschlange und schrieb nichts.

- `queue.discard` hält jetzt fest: Sitzung, Anwendung, Beginn und Dauer, ausgelöst von Hand.
  Es ist der einzige Ausgang dieses Fensters, der Arbeitszeit verschwinden lässt — und in TANSS
  ist dabei nie etwas angekommen, es gibt also sonst nirgends einen Beleg.
- Die Gegenstelle geht in das Feld für Fensterbeschriftungen und nicht in den Begründungstext:
  Nur dort greift `redact_window_titles`. Im Freitext stünde sie im Klartext in `state.db`, ganz
  gleich was eingestellt ist.

## [0.2.0] — 2026-09-13

### Hinzugefügt — Bildschirmaufzeichnung, erster Teil

Der prüfbare Kern der Aufzeichnung steht: das neue Projekt `TanssLogWatcher.Recording`, der
Konfigurationsabschnitt samt Prüfung und die drei Stücke, an denen die Ehrlichkeit der
Aufzeichnung hängt. Aufgezeichnet wird noch nichts — es gibt bewusst keinen Schalter in der
Oberfläche, solange nichts dahintersteht.

**Gemessen, bevor entworfen wurde.** Achtzehn Prüfläufe mit eigenen Probeprogrammen gegen diese
Windows-Version (11 Pro, Build 22631.6199, Intel Iris Xe):

- **Die Fensteraufnahme liefert besessene Unterfenster nicht mit.** Ein grüner Dialog über einem
  roten Hauptfenster ergab an seiner Stelle reines Rot (R=255 G=0 B=0). Wer nur das
  Hauptfenster aufnähme, verlöre genau die Dialoge, um die es hinterher geht — das
  Anmeldefenster von WinSCP, die Bestätigungsabfrage, den Fortschrittsbalken. Deshalb wird
  jedes Fenster einzeln aufgenommen und auf eine gemeinsame Leinwand kopiert.
- **`IncludeSecondaryWindows`**, die Schnittstelle, die das abnehmen würde, **wirft auf dieser
  Windows-Version** eine `InvalidCastException`, obwohl sie in der Projektion steht. Sie
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
  eine Instanz der Version 10.10.0.
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
den dieselbe Version mal gewinnt und mal verliert.

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
jeder Anfrage eine 403 erzeugte. Sie hätte beide Fehler dieser Version verdeckt.

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
  „bestanden“ ausgewiesen. Sie halten die beiden Fehler dieser Version fest sowie die beiden
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
  neuen Version vor einem unbekannten Herausgeber. Abhilfe ist nur ein
  Code-Signing-Zertifikat; `publish.ps1 -Sign` ist dafür vorbereitet, aber ungeprüft.
- Der Befehl `tanss-logwatch setup` fehlt weiterhin. Eingerichtet wird über die Oberfläche;
  mehrere Fehlermeldungen der Kommandozeile verweisen noch auf diesen Befehl.
- Die 36 Titelmuster sind gegen aktuelle Anwendungsversionen noch nicht nachgeprüft.
- Ein Token ohne `exp`-Anspruch lässt sich nicht erneuern — auch nicht erzwungen.
- Der Rechte-Vorabtest für das Prägen wird nicht selbsttätig ausgeführt: Er erzeugt in TANSS
  ein echtes, nicht widerrufbares Token. Fehlt das Recht, meldet es der Versuch selbst.

[0.3.5]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.3.5
[0.3.4]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.3.4
[0.3.3]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.3.3
[0.3.2]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.3.2
[0.3.1]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.3.1
[0.3.0]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.3.0
[0.2.0]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.2.0
[0.1.1]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.1.1
[0.1.0]: https://github.com/pronet-systems/tanss-log-watcher/releases/tag/v0.1.0
