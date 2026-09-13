# TANSS Log-Watcher

Ein Windows-Werkzeug, das Fernwartungssitzungen am Arbeitsplatz erkennt und sie **direkt in eure
TANSS-Instanz** schreibt. Es beobachtet die üblichen Fernwartungsanwendungen — Remotedesktop,
AnyDesk, TeamViewer, PuTTY, ScreenConnect und drei Dutzend weitere —, erkennt Anfang und Ende
einer Sitzung, fragt beim Abschluss nach Kommentar und Ticket und bucht die Fernwartung.

Kein Zwischendienst, kein Herstellerkonto, keine Daten außerhalb eures Hauses.

> **Stand: in Entwicklung.** Die Anbindung an TANSS ist gegen eine Produktivinstanz verifiziert,
> die Fachmodule sind fertig und getestet, die Oberfläche steht. Was fehlt, steht unter
> [Stand der Umsetzung](#stand-der-umsetzung). Für einen Produktiveinsatz ist es noch zu früh.

---

## Inhalt

- [Was das Werkzeug tut](#was-das-werkzeug-tut)
- [Was es nicht tut](#was-es-nicht-tut)
- [Wie die Erkennung arbeitet](#wie-die-erkennung-arbeitet)
- [Voraussetzungen](#voraussetzungen)
- [Installation](#installation)
- [Umstieg vom TANSS-X-Log-Watcher](#umstieg-vom-tanss-x-log-watcher)
- [Einrichtung](#einrichtung)
- [Konfiguration](#konfiguration)
- [Kommandos](#kommandos)
- [Betrieb](#betrieb)
- [Wenn TANSS nicht erreichbar ist](#wenn-tanss-nicht-erreichbar-ist)
- [Protokollierung](#protokollierung)
- [Sicherheit](#sicherheit)
- [Datenschutz und Mitbestimmung](#datenschutz-und-mitbestimmung)
- [Häufige Fragen](#häufige-fragen)
- [Stand der Umsetzung](#stand-der-umsetzung)
- [Entwicklung](#entwicklung)
- [Die TANSS-Anbindung im Einzelnen](#die-tanss-anbindung-im-einzelnen)
- [Lizenz](#lizenz)

---

## Was das Werkzeug tut

- **Sitzungen erkennen.** Jede Sekunde ein Durchlauf über die sichtbaren Fenster und die
  Netzwerkverbindungen der überwachten Anwendungen. Aus Prozess und Ziel — Fenstertitel oder
  Gegenstelle — entsteht eine Sitzung mit Anfang und Ende.
- **Nachfragen, bevor gebucht wird.** Am Sitzungsende öffnet sich ein Fenster mit Kommentar und
  Ticketauswahl. Wer nichts buchen will, verwirft die Sitzung.
- **Zuverlässig übertragen.** Fertige Sitzungen liegen in einer lokalen Warteschlange, bis TANSS
  sie bestätigt hat. Ein Netzausfall, ein Neustart oder ein Absturz kostet keine Sitzung.
- **Timer bedienen.** Die TANSS-Timer lassen sich anlegen, starten, anhalten, kommentieren und
  löschen, ohne die Weboberfläche zu öffnen.
- **Sich selbst am Leben halten.** Das Zugangstoken erneuert sich lange vor seinem Ablauf und
  wird gegengetestet, bevor es übernommen wird.
- **Auf Wunsch aufzeichnen.** Die Fenster einer laufenden Fernwartung lassen sich als Video
  mitschneiden. Das ist **voreingestellt aus** und bleibt aus, bis jemand eingeschaltet, die
  Kenntnisnahme erteilt und eine Rechtsgrundlage benannt hat. Jede Aufzeichnung bekommt beim
  Beginn ein festes Löschdatum.

## Was es nicht tut

- **Keine Tastatureingaben und keine Dateien.** Gelesen werden Fenstertitel, Prozessnamen und
  die Gegenstellen offener Netzwerkverbindungen — nicht mehr. Bildschirminhalte nur dann, wenn
  die Aufzeichnung ausdrücklich eingeschaltet **und** die Kenntnisnahme erteilt ist; ohne beides
  entsteht kein einziges Bild.
- **Nichts heimlich.** Das Symbol im Infobereich ist immer sichtbar, die laufenden Sitzungen
  sind jederzeit einsehbar, und keine Sitzung wird ohne Bestätigung gebucht.
- **Kein Zwang zur Überwachung.** In der Voreinstellung ist **keine einzige Anwendung** aktiv.
  Erst wer eine Anwendung einem Fernwartungstyp zuordnet, schaltet sie scharf.
- **Keine Telemetrie.** Es gibt keinen Rückkanal an irgendwen.
- **Keine erhöhten Rechte.** Das Werkzeug läuft als angemeldeter Benutzer. Fenster fremder,
  erhöht laufender Prozesse bleiben unlesbar — das ist gewollt.

---

## Wie die Erkennung arbeitet

Eine Sitzung ist das Paar **(Prozess, Ziel)**. Das Ziel wird auf einem von drei Wegen bestimmt,
je Anwendung festgelegt:

| Weg | Ziel | typisch für |
|---|---|---|
| **Muster im Fenstertitel** | der Treffer eines regulären Ausdrucks | Remotedesktop, AnyDesk, TeamViewer |
| **Fenstertitel** | der Titel selbst | PowerShell, Windows Terminal, Royal TS |
| **Netzwerkverbindung** | Gegenstelle als `hostname [ip]` | WinSCP, OpenVPN, MobaXterm |

Zwei Regeln verhindern, dass eine Sitzung zerfällt, während sie noch läuft:

- Findet sich beim ersten Durchlauf kein Titel, entsteht eine Sitzung mit Platzhalter. Sie
  übernimmt das Ziel, sobald eines auftaucht — die **Startzeit bleibt** die ursprüngliche.
- Bei Anwendungen, die ihren Titel während der Arbeit ändern, wird über das Fensterhandle
  zugeordnet. Eine Umbenennung aktualisiert das Ziel, beendet die Sitzung aber nicht.

Verliert ein Fenster kurzzeitig seinen Titel, würde daraus sonst ein Sitzungsende und sofort
eine neue Sitzung. Fallen beide im selben Durchlauf zusammen, werden sie verworfen.

Bei Anwendungen, die über die Netzwerkverbindung erkannt werden, zählt eine Verbindung erst drei
Minuten nach Prozessstart — sonst wäre jeder Aktualisierungsabruf beim Programmstart eine
Fernwartung.

**Die 36 mitgelieferten Anwendungsprofile sind ein Ausgangspunkt, keine Garantie.** Die
Titelmuster stammen aus der Analyse einer älteren Programmfassung und wurden nicht gegen jede
aktuelle Anwendungsversion geprüft. Ändert ein Hersteller seine Fenstertitel, greift das Muster
nicht mehr, und es entsteht stillschweigend keine Sitzung. Dafür gibt es die Ansicht **Diagnose**
und den Befehl `tanss-logwatch windows`: sie zeigen alle sichtbaren Fenster mit ihren echten
Titeln, und die Muster lassen sich in der Konfiguration überschreiben.

---

## Voraussetzungen

| | |
|---|---|
| TANSS | 10.10.0 oder neuer, Modul **Fernwartung** lizenziert |
| Anbindung | mindestens eine externe Fernwartungs-Anbindung in der TANSS-Administration |
| Mitarbeiterrecht | *„Darf API-Tokens für ext. Anbindungen erzeugen"* (Recht 480) |
| Arbeitsplatz | Windows 10 oder 11, x64 |
| .NET | keins — das Programm bringt seine Laufzeit mit |

### Externe Fernwartungs-Anbindungen

Das Werkzeug schreibt Fernwartungen vom Typ ≥ 1000. Diese Typen werden in der
TANSS-Administration unter *Externe Fernwartungs-Anbindungen verwalten* gepflegt; jede trägt
einen Namen, eine Farbe und einen Leistungstyp. Beim Einrichten wird jede überwachte Anwendung
einer dieser Anbindungen zugeordnet.

Sinnvoll ist eine Anbindung je Werkzeugklasse, nicht je Anwendung — etwa *Remote Desktop*,
*SSH*, *Dateiübertragung*. Die Zuordnung lässt sich jederzeit ändern.

### Das Mitarbeiterrecht

Recht 480 braucht der Techniker **einmalig zur Einrichtung** und dauerhaft für die
Token-Erneuerung. Fehlt es, bricht die Einrichtung ab, statt zu warnen: ein Token ohne
Erneuerungsmöglichkeit stirbt Monate später kommentarlos, und genau diesen Ausfall soll niemand
suchen müssen.

---

## Installation

Setup herunterladen und ausführen. Es installiert **pro Benutzer**, ohne Administratorrechte,
nach `%LOCALAPPDATA%\Programs\TanssLogWatcher`, und legt eine Verknüpfung im Autostart-Ordner an.

Die Verknüpfung im Autostart-Ordner ist Absicht: sie steht im Task-Manager unter „Autostart" und
lässt sich dort abschalten. Ein Werkzeug, das Fenstertitel mitliest, gehört an eine Stelle, an
der man es sieht und abstellen kann.

Das Setup ist **nicht signiert**, solange kein Zertifikat vorliegt. SmartScreen warnt deshalb bei
jedem neuen Build. Der SHA256 jeder veröffentlichten Datei steht in der Release-Notiz.

### Deinstallation

Die Deinstallation entfernt Programm und Verknüpfungen. Konfiguration und Zustandsdatenbank
bleiben in der Voreinstellung stehen und werden nur auf Nachfrage entfernt — in der
Warteschlange können noch ungesendete Sitzungen liegen.

---

## Umstieg vom TANSS-X-Log-Watcher

Beide Werkzeuge schreiben dieselben Fernwartungen. Laufen sie parallel, wird **doppelt gebucht**,
und Dubletten lassen sich in TANSS nur über einen direkten Datenbankzugriff wieder entfernen.

Reihenfolge:

1. Alten Client beenden und deinstallieren.
2. Reste entfernen: `%APPDATA%\COERO GmbH\TANSS X Log-Watcher`, die Verknüpfung im
   Autostart-Ordner und die Versionsverzeichnisse unter `%LOCALAPPDATA%`.
3. Erst danach den neuen einrichten.

Die Zuordnung Anwendung → Fernwartungstyp lässt sich aus der alten Konfiguration übernehmen; der
Einrichtungsassistent bietet das an, wenn er sie findet. Das erspart die vollständige
Neukonfiguration.

---

## Einrichtung

```
tanss-logwatch setup
```

Der Assistent führt durch alle Schritte und **prüft jeden sofort gegen die echte TANSS-Instanz**,
statt Eingaben nur entgegenzunehmen:

| Schritt | Was geprüft wird |
|---|---|
| TANSS-Adresse | Erreichbarkeit und API-Fassung |
| Anmeldung | einmalig mit den eigenen Zugangsdaten, bei Bedarf mit zweitem Faktor |
| Berechtigung | Trockentest der Token-Erneuerung — schlägt er fehl, bricht die Einrichtung ab |
| Token | wird geprägt, gegengetestet und versiegelt abgelegt |
| Mitarbeiter | eigene Kennung wird ermittelt und zur Bestätigung angezeigt |
| Anbindungen | die in TANSS gepflegten Fernwartungstypen werden abgerufen |
| Zuordnung | Anwendung → Fernwartungstyp, mit Vorschlägen |
| Probelauf | ein Durchlauf ohne Schreibzugriff, mit Liste der erkannten Sitzungen |

Der Assistent ist **wiederaufnehmbar**: bestätigte Schritte werden zwischengespeichert, ein
Abbruch verliert nichts.

**Zugangsdaten werden nach der Einrichtung verworfen.** Zurück bleiben nur das Arbeitstoken, die
eigene Mitarbeiter-ID und die Adresse.

---

## Konfiguration

Die Konfiguration liegt als eine versionierte JSON-Datei unter
`%APPDATA%\ProNet Systems\TanssLogWatcher\config.json` und ist von Hand editierbar. Sie lässt
sich auch vollständig in der Oberfläche pflegen.

**Unbekannte Felder werden abgelehnt.** Eine vertippte Einstellung, die stillschweigend ignoriert
wird, ist schlimmer als ein Fehler.

```jsonc
{
  "version": 1,
  "tanss": {
    "base_url": "https://tanss.example.com/backend",
    "employee_id": 1,
    "token_ref": "dpapi:credentials.dat",
    "rotate_before_days": 60,
    "verify_tls": true,
    "timeout_seconds": 30
  },
  "watcher": {
    "poll_interval_seconds": 1,
    "ip_session_grace_seconds": 180
  },
  "monitoring": [
    { "key": "putty",   "remote_support_type_id": 1003, "exclude_ip_addresses": "" },
    { "key": "mstsc",   "remote_support_type_id": 1002, "exclude_ip_addresses": "10.0.1.0/24" },
    { "key": "AnyDesk", "remote_support_type_id": 1005, "exclude_ip_addresses": "" }
  ],
  "proxy":   { "enabled": false, "address": "", "port": 8080, "user": "" },
  "logging": { "level": "info", "redact_window_titles": true, "retention_days": 30 }
}
```

### `tanss`

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `base_url` | — | Adresse der Instanz **einschließlich `/backend`**, ohne Schrägstrich am Ende |
| `employee_id` | — | eigene TANSS-Mitarbeiter-ID. Sie trägt die gesamte Zuordnung der Fernwartungen |
| `token_ref` | `dpapi:credentials.dat` | Verweis auf das Token. Es steht nie in dieser Datei |
| `rotate_before_days` | `60` | ab welcher Restlaufzeit das Token erneuert wird |
| `verify_tls` | `true` | Zertifikatsprüfung. `false` ist ein Notbehelf und wird in der Oberfläche als Warnung angezeigt |
| `timeout_seconds` | `30` | Zeitgrenze je Aufruf |

`base_url` muss auf `/backend` enden. Zeigt sie stattdessen auf die Weboberfläche, antwortet
TANSS auf **jede** Anfrage mit HTTP 400 — auch ohne Token, was die Fehlersuche erheblich
verlängert.

### `watcher`

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `poll_interval_seconds` | `1` | Abstand zwischen zwei Durchläufen. Zugleich die Messgenauigkeit von Beginn und Ende; die Sitzungsseite zeigt die gemessene Dauer eines Durchlaufs. |
| `ip_session_grace_seconds` | `180` | Karenzzeit für Anwendungen, die über die Netzwerkverbindung erkannt werden |

### `monitoring`

Ein Eintrag je Anwendung. `key` ist der Profilschlüssel aus dem mitgelieferten Katalog
(`tanss-logwatch windows` zeigt ihn an).

| Feld | Bedeutung |
|---|---|
| `remote_support_type_id` | der TANSS-Fernwartungstyp. `-1` bedeutet: nicht überwachen. Das ist die Vorgabe für alle |
| `exclude_ip_addresses` | einzelne Adressen und CIDR-Bereiche, durch Semikolon getrennt |
| `regex_pattern_override` | optional: ein eigener regulärer Ausdruck statt des mitgelieferten |

`exclude_ip_addresses` ist nützlich für Anwendungen, die außer der Fernwartung auch eigene
Dienste kontaktieren — Lizenzserver, Aktualisierungsabrufe, Telemetrie.

### `logging`

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `level` | `info` | `debug`, `info`, `warning`, `error` |
| `redact_window_titles` | `true` | Fensterbeschriftungen im lokalen Protokoll durch einen Abdruck ersetzen |
| `retention_days` | `30` | Aufbewahrung der Betriebsprotokolle |

`redact_window_titles` ersetzt die Beschriftung durch einen **gesalzenen** Abdruck (HMAC-SHA256
unter einem je Installation erzeugten Schlüssel). Dieselbe Beschriftung bleibt damit über
mehrere Einträge hinweg wiedererkennbar, ein Wörterbuchangriff von außen scheitert aber. Der
Schlüssel liegt DPAPI-versiegelt neben der Zustandsdatenbank.

### `recording`

Die Bildschirmaufzeichnung. **Voreingestellt aus**, und sie bleibt aus, solange nicht alle vier
Angaben der Kenntnisnahme stehen — eine Kenntnisnahme ohne benannte Rechtsgrundlage ist ein
Haken und kein Nachweis. Bequemer stellt sich das alles auf der Seite „Aufzeichnung“ ein.

| Feld | Vorgabe | Bedeutung |
|---|---|---|
| `enabled` | `false` | Aufzeichnen — allein genügt der Schalter nicht |
| `directory` | leer | Wohin; leer heißt: neben dem übrigen Zustand im Profil. Gelöscht wird ausschließlich unterhalb dieses Ordners |
| `retention_days` | `30` | Nach wie vielen Tagen eine Aufzeichnung gelöscht wird |
| `frames_per_second` | `4` | Bilder je Sekunde |
| `heartbeat_seconds` | `2` | Nach welcher Ruhe trotzdem ein Bild geschrieben wird |
| `segment_minutes` | `10` | Nach wie vielen Minuten eine neue Datei beginnt |
| `minimum_free_megabytes` | `2048` | Darunter wird nicht mehr aufgezeichnet |
| `acknowledged_at` | `null` | Wann die Kenntnisnahme erteilt wurde |
| `acknowledged_by` | `null` | Von wem |
| `legal_basis` | `null` | Worauf sie sich stützt |
| `legal_reference` | `null` | Der Beleg dazu |

Der Löschzeitpunkt jeder einzelnen Aufzeichnung wird beim Beginn aus `retention_days` berechnet
und **festgeschrieben**. Wer die Frist später verkürzt, verkürzt auch die bestehenden; wer sie
verlängert, verlängert die bestehenden **nicht** — ein gegebenes Versprechen wird nicht
nachträglich gedehnt.

Neben den Videodateien jeder Sitzung liegt eine `sitzung.json`. Sie nennt Gegenstelle,
Arbeitsplatz, Techniker, die Pausen und das Löschdatum — und sie wird mit den Videodateien
zusammen gelöscht. Im Ordnernamen steht bewusst **kein Kundenname**: Ein Dateipfad wandert in
Sicherungsläufe, Suchindizes und jede Fehlermeldung.

### Wo was liegt

| Was | Wo |
|---|---|
| Konfiguration | `%APPDATA%\ProNet Systems\TanssLogWatcher\config.json` |
| Token, Abdruckschlüssel | `%LOCALAPPDATA%\ProNet Systems\TanssLogWatcher\` (DPAPI, an den Benutzer gebunden) |
| Warteschlange, Sitzungen, Änderungsprotokoll | `%LOCALAPPDATA%\ProNet Systems\TanssLogWatcher\state.db` |
| Betriebsprotokolle | `%LOCALAPPDATA%\ProNet Systems\TanssLogWatcher\logs\` |
| Aufzeichnungen (wenn eingeschaltet) | `recording.directory`, sonst `%LOCALAPPDATA%\ProNet Systems\TanssLogWatcher\Aufzeichnungen\` |
| Programm | `%LOCALAPPDATA%\Programs\TanssLogWatcher\` |

Konfiguration und Laufzeitzustand sind bewusst getrennt: die eine will man sichern und
versionieren, den anderen nicht.

---

## Kommandos

| Befehl | Zweck |
|---|---|
| `tanss-logwatch setup` | Einrichtungsassistent |
| `tanss-logwatch doctor` | prüft Erreichbarkeit, Token, Restlaufzeit, Lizenz, Rechte und Warteschlange |
| `tanss-logwatch watch [--dry-run]` | beobachtet und reiht ein; `--dry-run` zeigt nur, was geschähe |
| `tanss-logwatch windows [--profile <key>]` | alle sichtbaren Fenster mit Prozess, Titel, Handle, Verbindungen |
| `tanss-logwatch queue [--flush]` | Warteschlange anzeigen; `--flush` sendet die fälligen Einträge |
| `tanss-logwatch token status` | Restlaufzeit und Erneuerungsfähigkeit |
| `tanss-logwatch token rotate` | Token erneuern, gegentesten, übernehmen |

### Rückgabewerte

`doctor` ist der Überwachungsvertrag:

| Wert | Bedeutung |
|---|---|
| `0` | gesund |
| `1` | Warnung — läuft, verlangt aber Aufmerksamkeit |
| `2` | gestört — in diesem Zustand geht Arbeit verloren oder kommt nicht an |
| `64` | Aufruffehler — unbekannter Befehl oder fehlendes Argument |

Damit lässt sich das Werkzeug in eine bestehende Überwachung einhängen, ohne Ausgaben zu parsen.

---

## Betrieb

Im Normalbetrieb läuft das Werkzeug mit geschlossenem Fenster und sichtbarem Symbol im
Infobereich. Der Hinweistext am Symbol zeigt den Zustand: laufende Sitzungen, wartende Einträge,
Token-Restlaufzeit.

Drei Hintergrundvorgänge arbeiten unabhängig voneinander:

| Vorgang | Takt | Aufgabe |
|---|---|---|
| Beobachtung | `poll_interval_seconds` | Sitzungen erkennen, laufende sichern |
| Versand | eigener Takt mit Rückstauzeit | fällige Einträge der Warteschlange senden |
| Token | täglich | Restlaufzeit prüfen, rechtzeitig erneuern |

Der Fehler eines Vorgangs legt die anderen nicht still. Scheitert der Versand, läuft die
Beobachtung weiter und die Warteschlange füllt sich — genau dafür ist sie da.

---

## Wenn TANSS nicht erreichbar ist

Das ist der Fall, für den die Warteschlange gebaut ist.

```
Sitzung beginnt   → sofort in die lokale Datenbank (überlebt Absturz und Neustart)
Sitzung endet     → in die Warteschlange, ZUERST, vor jedem Netzkontakt
TANSS erreichbar? → nein: Eintrag bleibt liegen, Rückstauzeit wächst exponentiell,
                          Deckel eine Stunde
                  → ja:  senden, bei Erfolg abschließen
```

Es gibt **kein Verlustfenster**: die Erkennung sendet nie selbst, sie schreibt nur in die
Datenbank. Stürzt der Rechner mitten im Sendevorgang ab, fällt der Eintrag beim nächsten Start
zurück auf *wartend* — mit dem Kennzeichen **Ausgang unbekannt**, weil niemand die Antwort
gesehen hat.

**Und dann wird nicht geraten.** Vor jeder Wiederholung eines solchen Eintrags fragt das Werkzeug
per Textfilter auf die eigene Sitzungskennung nach, ob die Fernwartung schon in TANSS steht, und
sendet nur bei Abwesenheit. Das ist keine Vorsicht, sondern nötig:

> **TANSS erkennt Dubletten nicht.** Ein zweiter Aufruf mit derselben Kennung erzeugt einen
> zweiten Datensatz. Und eine Dublette ist nur über einen direkten Datenbankzugriff wieder zu
> entfernen — die Löschroute der API ist ohne ein typgebundenes Token unerreichbar.

Scheitert die Existenzprüfung selbst, heißt das **unbekannt**, nicht *nicht vorhanden*. Der
Eintrag wird zurückgestellt statt gesendet.

---

## Protokollierung

Drei Ebenen mit unterschiedlicher Aufbewahrung:

| Ebene | Inhalt | Aufbewahrung |
|---|---|---|
| Betrieb | Durchläufe, Fehler, Zustandswechsel | `retention_days`, Vorgabe 30 Tage |
| Änderungen | jede gebuchte, zurückgestellte oder verworfene Sitzung mit Grund | 1 Jahr |
| Token | Prägungen und Erneuerungen | 1 Jahr |

Das Änderungsprotokoll hält fest **warum**, nicht nur dass: Anlass, Auslöser, HTTP-Status, Dauer
und Ergebnis — auch bei einem Trockenlauf, damit ein Probelauf genau zeigt, was der echte Lauf
täte.

**Tokens und Geheimnisse werden niemals protokolliert.** JWT-Muster und `Bearer`-Werte werden
beim Schreiben geschwärzt. Fensterbeschriftungen werden, wenn `redact_window_titles` gesetzt ist,
durch einen gesalzenen Abdruck ersetzt.

Auch das **Verwerfen** einer Sitzung wird protokolliert. Wer am Monatsende eine Lücke sucht,
findet so wenigstens den Grund.

---

## Sicherheit

**Das Token ist ein Ausweis.** Es erlaubt, im Namen des Mitarbeiters zu handeln. Es liegt deshalb
DPAPI-versiegelt und an den Windows-Benutzer gebunden auf der Platte, hat eine begrenzte Laufzeit
und wird regelmäßig erneuert. Jeder Arbeitsplatz bekommt ein **eigenes** Token; ein geteiltes
wäre nicht zuzuordnen.

**TANSS 10.10.0 kennt keinen Widerruf.** Ein einmal ausgestelltes Token bleibt bis zu seinem
Ablauf gültig. Das ist keine Eigenart dieses Werkzeugs, sondern der Schnittstelle — es gehört
aber offen gesagt. Geht ein Arbeitsplatz verloren:

1. Dem Mitarbeiter das Recht 480 entziehen. Das stoppt die Erneuerung, nicht das vorhandene
   Token.
2. Im Ernstfall die instanzweiten JWT-Schlüssel in TANSS erneuern. Das entwertet **alle** Tokens
   inklusive der Web-Sitzungen.

Deshalb ist die voreingestellte Laufzeit mit 180 Tagen kürzer als die von TANSS angebotenen 365.

**Schreibende Aufrufe werden nie automatisch wiederholt.** Eine Zeitüberschreitung heißt nicht,
dass der Server nichts getan hat.

---

## Datenschutz und Mitbestimmung

Das Werkzeug erfasst laufend Fenstertitel, Prozessnamen und Verbindungsziele an Arbeitsplätzen
von Mitarbeitern und überträgt Zeiträume mit Personenbezug. In Deutschland ist das eine
**mitbestimmungspflichtige technische Einrichtung nach § 87 Abs. 1 Nr. 6 BetrVG**. Vor der
Einführung gehört der Betriebsrat eingebunden.

Fenstertitel enthalten regelmäßig personenbezogene Daten — Kundennamen, E-Mail-Betreffe,
Hostnamen. Sie landen im Kommentar der Fernwartung. In den lokalen Protokollen werden sie
standardmäßig durch einen Abdruck ersetzt.

Die beiden Outlook-Profile, die Nachrichtenbetreffe als Ziel verwenden, sind aus gutem Grund
voreingestellt inaktiv.

Was das Werkzeug gegen verdeckten Betrieb vorsieht: sichtbares Symbol im Infobereich,
einsehbare Sitzungsliste, abschaltbarer Autostart, Bestätigung vor jeder Buchung und die
Möglichkeit, eine Sitzung zu verwerfen.

### Bildschirmaufzeichnung

Sie ist der Teil mit dem größten Gewicht und deshalb der am strengsten verriegelte. Eine
Aufzeichnung zeigt den Bildschirm des Kunden — mit dessen Daten und den Daten Dritter — und den
Bildschirm des Technikers; Letzteres ist Leistungs- und Verhaltenskontrolle im Sinne von
§ 87 Abs. 1 Nr. 6 BetrVG.

- Sie ist **voreingestellt aus** und lässt sich nicht allein durch einen Schalter einschalten:
  Es braucht zusätzlich eine Kenntnisnahme mit benannter Rechtsgrundlage und Beleg. Fehlt eines
  davon, entsteht kein Bild, und die Oberfläche sagt, was fehlt.
- Die Rechtsgrundlage ist **nicht vorbelegt**. Ob bei euch eine Betriebsvereinbarung besteht,
  weiß dieses Werkzeug nicht — und es behauptet es auch nicht.
- Aufgezeichnet wird ausschließlich, was auch gebucht wird: Sitzungen von Anwendungen, die einem
  Fernwartungstyp zugeordnet sind. Ein Video ohne den Vorgang, zu dem es gehört, wäre allein
  eine Datensammlung.
- Aufgezeichnet werden die **Fenster der Sitzung**, nicht der Bildschirm. Ein fremdes Fenster
  darüber landet nachweislich nicht im Bild — das Mailfenster des Technikers gehört nicht in die
  Dokumentation des Kunden.
- Ist das Fenster minimiert, **pausiert** die Aufzeichnung, und die Pause zählt nicht zur
  aufgezeichneten Zeit. Die Datei ist damit genau so lang wie das, was sie zeigt; welche
  Abschnitte Pause waren, steht in der Begleitdatei.
- Jede Aufzeichnung bekommt beim Beginn ein **festes Löschdatum**. Gelöscht wird stündlich und
  beim Start, auch wenn die Aufzeichnung inzwischen abgeschaltet ist.
- Nichts davon verlässt den Rechner. Es gibt keinen Versand, keinen Zwischendienst und keine
  Anbindung, über die ein Video irgendwohin ginge.

---

## Häufige Fragen

### Warum wird meine Anwendung nicht erkannt?

Drei Ursachen, in dieser Reihenfolge zu prüfen:

1. Sie ist keinem Fernwartungstyp zugeordnet. In der Voreinstellung ist **keine** Anwendung
   aktiv.
2. Das Titelmuster greift nicht mehr, weil der Hersteller den Fenstertitel geändert hat.
   `tanss-logwatch windows --profile <key>` zeigt, was das Profil aus den aktuellen Fenstern
   ableiten würde.
3. Die Anwendung läuft mit erhöhten Rechten. Dann ist ihr Fenstertitel für ein Werkzeug ohne
   erhöhte Rechte nicht lesbar.

### Warum steht bei einer Sitzung „Kein Titel ermittelt"?

Das Fenster war beim ersten Durchlauf noch ohne Titel — üblich bei Anwendungen, die erst eine
Verbindung aufbauen. Die Sitzung übernimmt das Ziel, sobald eines auftaucht, und behält ihre
Startzeit. Bleibt der Platzhalter bis zum Ende stehen, greift das Muster nicht.

### Ich habe eine Sitzung versehentlich verworfen.

Sie wurde nicht gebucht, aber das Verwerfen steht mit Zeitpunkt und Grund im Änderungsprotokoll.
Die Fernwartung ist von Hand in TANSS nachzutragen.

### Warum fragt das Werkzeug erst, bevor es einen Eintrag wiederholt?

Weil TANSS Dubletten nicht erkennt und eine Dublette nur per Datenbankzugriff wieder zu
entfernen ist. Siehe [Wenn TANSS nicht erreichbar ist](#wenn-tanss-nicht-erreichbar-ist).

### Muss ich das Token regelmäßig erneuern?

Nein. Das Werkzeug erneuert es selbstständig, standardmäßig 60 Tage vor Ablauf, und prüft das
neue Token mit einem echten Aufruf, bevor es das alte ersetzt. Schlägt die Erneuerung fehl,
bleibt das bisherige aktiv — es ist ja noch gültig.

Voraussetzung ist, dass der Mitarbeiter aktiv bleibt und das Recht 480 behält.

### Was passiert, wenn ich zwei Wochen im Urlaub war?

Nichts. Das Arbeitstoken läuft 180 Tage. Ein Anmeldetoken, das nach 14 Tagen verfällt, wird
bewusst nicht verwendet — es käme nach jedem längeren Urlaub eine Anmeldeaufforderung, und für
Fernwartungen reicht es ohnehin nicht aus.

### Kann ich das Werkzeug parallel zum TANSS-X-Log-Watcher betreiben?

Nein. Beide schreiben dieselben Fernwartungen, und Dubletten sind nur per Datenbankzugriff
wieder loszuwerden. Siehe [Umstieg](#umstieg-vom-tanss-x-log-watcher).

### Warum läuft es pro Benutzer und nicht als Dienst?

Es liest Fenstertitel der angemeldeten Sitzung. Ein Dienst sähe sie nicht. Auf einem
Terminalserver läuft je angemeldetem Benutzer eine eigene Instanz — die Einzelinstanz-Sperre ist
an die Anmeldesitzung gebunden, nicht an die Maschine.

### Verlässt irgendetwas den Rechner außer den Fernwartungen?

Nein. Es gibt genau eine Gegenstelle: eure TANSS-Instanz. Keine Telemetrie, keine
Absturzberichte, keine Aktualisierungsabfrage bei einem Dritten.

---

## Stand der Umsetzung

| Baustein | Stand |
|---|---|
| TANSS-Anbindung (Client, Token, Fernwartungen, Tickets, Timer) | fertig, getestet |
| Sitzungserkennung (Fenster, Prozesse, Verbindungen, Zustandsmaschine) | fertig, getestet |
| Konfiguration, Token-Ablage, Warteschlange, Protokolle | fertig, getestet |
| Kommandozeile (`doctor`, `watch`, `windows`, `queue`, `token`) | fertig |
| Oberfläche (Tray, Sitzungen, Warteschlange, Timer, Überwachung, Diagnose, Verbindung) | verdrahtet, gegen eine Produktivinstanz geprüft |
| Einrichtungsassistent in der Oberfläche | fertig |
| Abschlussdialog mit vorausgefülltem Bericht | fertig |
| Einrichtungsassistent auf der Kommandozeile (`setup`) | offen |
| Selbsttätige Aktualisierung über GitHub | fertig |
| Sprachmodell-Unterstützung (abgeschaltet, einwilligungspflichtig) | fertig |
| Bildschirmaufzeichnung (abgeschaltet, kenntnisnahmepflichtig) | verdrahtet, gegen echte Fenster und echte Dateien geprüft; ein Probelauf über eine echte Fernwartung steht aus |
| Aufzeichnung mehrerer Bildschirme im RDP-Mehrschirmbetrieb | bewusst zurückgestellt |
| Setup und Veröffentlichung | fertig |
| Prüfung der 36 Titelmuster gegen aktuelle Anwendungsversionen | offen |

**Vor einem Produktiveinsatz** stehen außerdem aus: ein Probelauf über mehrere Arbeitstage, die
Einbindung der Mitbestimmung und eine Klärung der urheberrechtlichen Herkunft der
Anwendungsprofile (siehe [Entwicklung](#entwicklung)).

---

## Entwicklung

```bash
dotnet build          # 0 Warnungen sind Pflicht: TreatWarningsAsErrors ist an
dotnet test
```

Zielframework ist **.NET 10** (LTS bis November 2028). Die Oberfläche ist WPF mit
[WPF-UI](https://github.com/lepoco/wpfui); alle Abhängigkeiten stehen unter MIT.

### Aufbau

```
src/TanssLogWatcher.Api          HTTP, Token, Repositories — keine Windows-Abhängigkeit
src/TanssLogWatcher.Monitoring   Win32: Fenster, Prozesse, Verbindungen, Zustandsmaschine
src/TanssLogWatcher.Storage      Konfiguration, DPAPI, Warteschlange, Protokolle
src/TanssLogWatcher.Cli          Kommandozeile
src/TanssLogWatcher.App          WPF-Oberfläche
tests/                           je Modul ein Testprojekt
```

Die Schichtung ist strikt: `Client` → `Repository` → `Engine` → Oberfläche, keine
Rückwärtsabhängigkeit. Die Sitzungserkennung läuft vollständig ohne WPF und ohne Netz und ist
deshalb ohne laufende TANSS-Instanz testbar.

### Konventionen

- Deutsche Prosa in den Dokumentationskommentaren und in allen Benutzertexten, englische
  Bezeichner im Code.
- Kommentare erklären **warum**, nicht was.
- **Erst Status prüfen, dann Rumpf.** Ein leerer Rumpf ist nur bei Erfolg eine leere Antwort —
  stünde die Leerprüfung vorher, wäre eine leere 403 ein leerer Erfolg.
- Der Fehler eines einzelnen Vorgangs bricht nie einen ganzen Durchlauf ab.
- Zeiten ausschließlich über `TanssTime`. Es gibt bewusst keinen Millisekunden-Umrechner.

### Herkunft der Anwendungsprofile

Die 36 Anwendungsprofile — Prozessnamen, Erkennungsmethoden und Titelmuster — wurden aus der
Analyse eines bestehenden, proprietären Werkzeugs gewonnen. Sie beschreiben Tatsachen über
fremde Anwendungen, nicht fremden Code; jeder Eintrag ist dennoch gegen die echte Anwendung neu
zu verifizieren und neu zu formulieren, bevor dieses Projekt veröffentlicht wird. Das ist
ohnehin nötig, weil die Muster ohne solche Prüfung nicht verlässlich sind.

Aus dem analysierten Werkzeug wurde **kein Code, kein Symbol, keine Oberflächenbeschreibung und
kein Text übernommen.**

---

## Die TANSS-Anbindung im Einzelnen

Dieser Abschnitt richtet sich an Mitentwickler. Ein erheblicher Teil der benutzten Routen ist
**nicht dokumentiert** und wurde aus der Server-Implementierung und aus Messungen gegen eine
Produktivinstanz erschlossen. Wer daran etwas ändert, braucht eine Messung, keine Vermutung.

### Transport

| | |
|---|---|
| Kopfzeile | `apiToken: Bearer <jwt>` — **nicht** `Authorization` |
| Basisadresse | `https://host/backend`, ohne Schrägstrich am Ende |
| Erfolg | `{ "meta": {…}, "content": … }` |
| Fehler | `{ "error": { "text", "localizedText", "type", "traceId" } }` |
| Zeiten | **Unix-Sekunden**, überall |

### Zwei Präfixe, zwei entgegengesetzte Regeln

| Präfix | Regel |
|---|---|
| `/api/v1/**` | `loggedInUserId` ist **zwingend**. Ohne ihn antwortet TANSS mit 403 |
| `/api/tanss.x/v1/**` | `loggedInUserId` darf **niemals** mitgeschickt werden |

Das ist die wahrscheinlichste Fehlerquelle beim Erweitern. `TanssRoutes.NeedsLoggedInUserId`
entscheidet es; der Client setzt den Parameter selbsttätig, der Aufrufer nie von Hand.

### Die benutzten Routen

| Zweck | Route | Dokumentiert |
|---|---|---|
| Fernwartung anlegen | `POST /api/tanss.x/v1/remoteSupports` | nein |
| Fernwartungen lesen | `PUT /api/v1/remoteSupports` | nein |
| Anbindungen auflisten | `GET /api/tanss.x/v1/remoteSupports/systems` | nein |
| Techniker | `GET /api/tanss.x/v1/technicians` | nein |
| Eigene Tickets | `GET /api/v1/tickets/own` | ja |
| Timer | `GET/POST /api/v1/timers`, `PUT /api/v1/timers/{id}`, `DELETE /api/v1/timers` | teilweise |
| Timer-Notizen | `GET/POST /api/v1/timers/notes/{id}`, `PUT /api/v1/timers/notes` | teilweise |
| Anmeldung | `POST /api/v1/login` | ja |
| Token prägen | `GET /api/v1/jwts/tanss_app` | nein |

Zwei Eigenheiten, die Zeit kosten, wenn man sie nicht kennt:

- **`DELETE /api/v1/timers` erwartet die Kennung im Rumpf**, nicht als Abfrageparameter. Eine
  Route mit `?id=` gibt es nicht.
- **`GET /api/v1/jwts/tanss_app` ist trotz des Verbs kein Lesevorgang.** Jeder Aufruf stellt ein
  Token aus und trägt es ins serverseitige Tokenprotokoll ein. Er wird deshalb nie wiederholt.

### Der Rumpf einer Fernwartung

| Feld | Einheit | Pflicht | Anmerkung |
|---|---|---|---|
| `typeId` | — | ja | muss ≥ 1000 sein und in TANSS existieren |
| `employeeId` | — | ja | ein Wert ≠ 0 gewinnt immer; das ist die gesamte Zuordnung |
| `startTime`, `endTime` | **Sekunden** | ja | `endTime` 0 bedeutet: läuft noch |
| `remoteMaintenanceId` | — | empfohlen | unsere Sitzungskennung, Grundlage der Existenzprüfung |
| `comment` | — | optional | beginnt mit der Beschreibung der Sitzung |
| `ticketId` | — | optional | undokumentiert, aber voll wirksam |
| `id`, `fee`, `typeName` | — | **nie senden** | |

`companyId`, `linkTypeId` und `linkId` bleiben auf 0. Sie sind der **Geräteplatz**, nicht der
Ticketplatz, und werden serverseitig gemeinsam überschrieben, sobald die Geräteübersetzung
greift.

---

## Lizenz

MIT — siehe [LICENSE](LICENSE). Copyright (c) 2026 ProNet Systems GmbH.

TANSS ist eine Marke der TANSS GmbH. TANSS X ist ein Produkt der COERO GmbH. Dieses Projekt steht
in keiner Verbindung zu diesen Unternehmen und wird von ihnen weder unterstützt noch geprüft.
