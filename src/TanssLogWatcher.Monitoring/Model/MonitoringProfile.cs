using System.Net;

namespace TanssLogWatcher.Monitoring.Model;

/// <summary>
/// Das unveränderliche Wissen über eine überwachbare Anwendung: wie ihre Prozesse heißen und wie
/// man aus ihren Fenstern oder Verbindungen ein Ziel liest.
/// </summary>
/// <remarks>
/// Ein Profil trägt <b>keine</b> Benutzerentscheidung. Ob und auf welchen TANSS-Fernwartungstyp
/// eine Anwendung abgebildet wird, steht in <see cref="MonitoringSetting"/>. Die Trennung ist
/// wichtig, weil der Katalog mit jeder Programmversion wachsen darf, ohne die gespeicherten
/// Einstellungen des Technikers zu berühren.
/// </remarks>
public sealed record MonitoringProfile
{
    /// <summary>Der unveränderliche Schlüssel. Bindeglied zur gespeicherten Einstellung.</summary>
    public required string Key { get; init; }

    /// <summary>Die Bezeichnung, die im Kommentar der Fernwartung erscheint.</summary>
    public required string TypeDescription { get; init; }

    /// <summary>
    /// Die Prozessnamen ohne Erweiterung. Ein Eintrag, der auf <c>*</c> endet, ist ein Präfix und
    /// passt auf jeden Namen mit diesem Anfang.
    /// </summary>
    public required IReadOnlyList<string> ProcessNames { get; init; }

    /// <summary>Das Verfahren zur Zielbestimmung.</summary>
    public required DestinationResolveMethod DestinationResolveMethod { get; init; }

    /// <summary>
    /// Der reguläre Ausdruck für <see cref="DestinationResolveMethod.WindowTitleRegex"/>. Der
    /// <b>Treffer selbst</b> ist das Ziel, nicht eine Gruppe daraus.
    /// </summary>
    public string? RegexPattern { get; init; }

    /// <summary>
    /// Wahr, wenn eine Umbenennung des Fensters die Sitzung fortsetzt statt sie zu beenden.
    /// Nötig überall dort, wo der Titel im Betrieb wandert — etwa eine Shell, die den
    /// Verzeichnisnamen zeigt.
    /// </summary>
    public bool IgnoreTitleChanges { get; init; }

    /// <summary>Wahr, wenn nur das Hauptfenster des Prozesses ein Ziel liefern darf.</summary>
    public bool MonitorOnlyMainWindowHandle { get; init; }

    /// <summary>
    /// Wahr, wenn ein Fenster ohne Treffer die Sitzung <b>unterdrückt</b>. Ohne diese Angabe
    /// entstünde für jedes beliebige Nebenfenster der Anwendung ein Platzhalter.
    /// </summary>
    public bool MonitorOnlyRegexMatchedWindow { get; init; }

    /// <summary>
    /// Wahr für Anwendungen ohne eigenes Fenster. Findet sich kein fensterbesitzender Prozess,
    /// wird über den Prozessnamen gesucht.
    /// </summary>
    public bool NoGui { get; init; }

    /// <summary>Titel, die kein Ziel ergeben — ordinaler Vergleich auf Gleichheit.</summary>
    public IReadOnlyList<string> IgnoredTitles { get; init; } = [];

    /// <summary>Gegenstellen, die kein Ziel ergeben — Vergleich auf Adressgleichheit.</summary>
    public IReadOnlyList<IPAddress> IgnoredIpAddresses { get; init; } = [];

    /// <summary>
    /// Namensendungen, die kein Ziel ergeben. Reiner Suffixtest ohne Punktgrenze:
    /// <c>wp.com</c> schließt deshalb auch <c>notwp.com</c> aus.
    /// </summary>
    public IReadOnlyList<string> IgnoredDnsDomains { get; init; } = [];

    /// <summary>
    /// Prüft, ob ein Prozessname zu diesem Profil gehört.
    /// </summary>
    /// <remarks>
    /// Zwei Regeln: Gleichheit ohne Beachtung der Groß-/Kleinschreibung, und für Einträge mit
    /// <c>*</c> am Ende ein Präfixvergleich ebenfalls ohne Beachtung der Schreibweise.
    /// Ein <c>*</c> in der Mitte ist kein Platzhalter, sondern ein gewöhnliches Zeichen.
    /// </remarks>
    public bool MatchesProcessName(string? processName)
    {
        if (string.IsNullOrEmpty(processName))
        {
            return false;
        }

        foreach (string candidate in ProcessNames)
        {
            if (string.Equals(candidate, processName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }

            if (candidate.Length > 1 && candidate.EndsWith('*') &&
                processName.StartsWith(candidate[..^1], StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Verlangt dieses Profil ein erkanntes Ziel, bevor eine Sitzung entsteht?
    /// </summary>
    /// <remarks>
    /// <para><b>Der Platzhalter ist für ein Fenster ohne Titel gedacht, nicht für ein Fenster
    /// mit dem falschen Titel.</b> Findet das Muster in keinem Fenstertitel ein Ziel, legt die
    /// Auflösung sonst eine Sitzung mit
    /// <see cref="SessionConstants.NoDestinationTitle"/> an — in der Erwartung, dass gleich ein
    /// echtes Ziel nachkommt. Für eine Anwendung, die nur während einer Verbindung überhaupt
    /// läuft, ist das richtig: Microsoft Remotedesktop wird gestartet, um sich zu verbinden,
    /// und sein Fenster trägt in den ersten Sekunden noch keinen Namen.</para>
    ///
    /// <para><b>Für eine Anwendung, die dauerhaft offen steht, ist es falsch</b> — und es ist
    /// im Betrieb aufgefallen: AnyDesk läuft mit seinem Hauptfenster, ob eine Fernwartung
    /// besteht oder nicht. Dessen Titel lautet dann schlicht <c>AnyDesk</c>, das Muster
    /// „alles vor dem Bindestrich“ greift nicht — und es entstand eine Sitzung, obwohl niemand
    /// verbunden war. Wer die Anwendung nur öffnet oder von einer Webseite starten lässt, hat
    /// keine Fernwartung gemacht.</para>
    ///
    /// <para>Voreingestellt <c>false</c>, und das mit Bedacht: Eine Sitzung zu viel ist
    /// sichtbar und wird im Abschlussdialog verworfen; eine Sitzung zu wenig kostet Arbeitszeit,
    /// die niemand mehr findet. Gesetzt wird die Angabe deshalb nur dort, wo sicher ist, dass
    /// die Anwendung auch ohne Fernwartung offen steht.</para>
    /// </remarks>
    public bool RequiresResolvedDestination { get; init; }

    /// <summary>
    /// Taugt der Bezeichner dieses Profils als dauerhafte Kennung des Geräts?
    /// </summary>
    /// <remarks>
    /// <para><b>Wofür das gebraucht wird.</b> TANSS führt eine eigene Übersetzungstabelle
    /// <c>deviceId → Firma</c>; wird beim Anlegen einer Fernwartung eine <c>deviceId</c>
    /// mitgeschickt, setzt TANSS die Firma selbst ein. Nachgemessen am 13.09.2026 gegen eine
    /// Instanz der Version 10.10.0: mit hinterlegter Zuordnung kam die Firma zurück, ohne sie
    /// eine 0. Der Nutzen steht und fällt damit, dass die Kennung <b>morgen dasselbe Gerät
    /// bezeichnet wie heute</b>.</para>
    ///
    /// <para><b>Voreingestellt <c>false</c>, und zwar ausdrücklich.</b> Wer ein Profil
    /// hinzufügt, muss sich die Frage stellen — ein stillschweigendes Ja wäre hier teuer: Aus
    /// einem Fenstertitel wie „Dokument1 - Word“ entstünde eine Kennung, und wer sie einmal
    /// einem Kunden zuordnet, bucht fortan jedes Word-Fenster auf diesen Kunden.</para>
    ///
    /// <para><b>Nicht gesetzt ist es deshalb bei:</b> allen Profilen, deren Ziel der
    /// <b>ganze</b> Fenstertitel ist (<c>host</c>, <c>WindowsTerminal</c>, <c>cmd</c>,
    /// <c>powershell</c>, <c>RoyalTS</c> und weitere); den beiden Outlook-Profilen, deren Ziel
    /// die <b>Nachrichtenbetreffzeile</b> ist; <c>devenv</c> und <c>Code</c>, deren Ziel eine
    /// Projektmappe oder Datei benennt; und <c>Zoom</c>, dessen Ziel eine Besprechung ist und
    /// kein Gerät.</para>
    /// </remarks>
    public bool YieldsDeviceIdentity { get; init; }

    /// <summary>Ordinaler Gleichheitstest gegen <see cref="IgnoredTitles"/>.</summary>
    public bool IsIgnoredTitle(string title) => IgnoredTitles.Contains(title, StringComparer.Ordinal);

    /// <summary>Suffixtest gegen <see cref="IgnoredDnsDomains"/>. <c>null</c> ergibt niemals einen Treffer.</summary>
    public bool IsIgnoredHostName(string? hostName)
    {
        if (string.IsNullOrEmpty(hostName))
        {
            return false;
        }

        foreach (string domain in IgnoredDnsDomains)
        {
            if (hostName.EndsWith(domain, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}
