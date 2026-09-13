using TanssLogWatcher.Monitoring.Model;

namespace TanssLogWatcher.Monitoring;

/// <summary>
/// Bildet die Kennung, unter der TANSS das Gerät einer Fernwartung wiedererkennt.
/// </summary>
/// <remarks>
/// <para><b>Wozu.</b> TANSS führt eine Übersetzungstabelle <c>deviceId → Firma</c>. Geht beim
/// Anlegen einer Fernwartung eine <c>deviceId</c> mit, setzt TANSS die Firma selbst ein —
/// nachgemessen am 13.09.2026 gegen eine Instanz der Version 10.10.0: mit hinterlegter
/// Zuordnung kam die Firma zurück, ohne Zuordnung eine 0. Das Werkzeug hat das Feld seit jeher
/// im Modell und hat es nie gefüllt; deshalb hat die Übersetzung nie gegriffen.</para>
///
/// <para><b>Warum das eine eigene Stelle ist und keine Zeile im Beobachtungsdienst.</b> Die
/// Kennung wird an drei Stellen gebildet — beim Abschluss einer Sitzung, beim geordneten Ende
/// und bei der Wiederherstellung nach einem Neustart. Drei Stellen heissen drei Gelegenheiten,
/// den Riegel zu vergessen; und der Riegel ist hier das Eigentliche.</para>
///
/// <para><b>Der Riegel.</b> Eine Kennung darf nur entstehen, wo sie <b>morgen dasselbe Gerät
/// bezeichnet wie heute</b>. Das entscheidet nicht diese Klasse, sondern der Profilkatalog über
/// <see cref="MonitoringProfile.YieldsDeviceIdentity"/> — dort steht das Wissen über die
/// Bedeutung eines Fenstertitels, und dort muss ein neues Profil sich erklären. Zusätzlich
/// gesperrt ist der Platzhalter <see cref="SessionConstants.NoDestinationTitle"/>: Eine
/// Zuordnung auf ihn träfe künftig <b>jede</b> Sitzung, deren Ziel nicht aufgelöst werden
/// konnte — der teuerste denkbare Fehlgriff, weil er sich über alle Kunden verteilt.</para>
///
/// <para><b>Unverändert oder gar nicht.</b> Die Kennung wird nicht gekürzt, nicht kleingeschrieben
/// und nicht von Domänensuffixen befreit. Wie TANSS vergleicht, ist nicht gemessen; ein selbst
/// erdachtes Normalisieren träfe dann womöglich eine andere Zeile als die, die jemand in TANSS
/// angelegt hat. Weggeschnitten werden nur Leerzeichen am Rand — die stammen aus dem
/// Fenstertitel und nicht aus dem Namen des Geräts.</para>
/// </remarks>
public static class DeviceIdentity
{
    /// <summary>
    /// Die Höchstlänge, die TANSS für <c>deviceId</c> zeigt.
    /// </summary>
    /// <remarks>
    /// Die Beschreibung nennt keine Grenze. Dieser Wert ist die Länge, die das Werkzeug auch
    /// für <c>deviceName</c> einhält — ein Wert, der sich im Betrieb bewährt hat. Er greift als
    /// Riegel und nicht als Kürzung: Was länger ist, ergibt <b>keine</b> Kennung, statt eine
    /// abgeschnittene zu liefern, die auf nichts passt.
    /// </remarks>
    public const int MaximumLength = 120;

    /// <summary>
    /// Bildet die Kennung, oder <see langword="null"/>, wenn keine zu verantworten ist.
    /// </summary>
    /// <param name="profile">
    /// Das Profil der Sitzung; <see langword="null"/>, wenn es im Katalog nicht (mehr) steht.
    /// </param>
    /// <param name="identityKey">
    /// Der identifizierende Anteil des Ziels — <see cref="Model.WatchedSession.IdentityKey"/>,
    /// nicht die angezeigte Gegenstelle. Beide fallen häufig zusammen, aber nicht immer.
    /// </param>
    /// <returns>Die Kennung, oder <see langword="null"/>.</returns>
    public static string? For(MonitoringProfile? profile, string? identityKey)
    {
        if (profile is not { YieldsDeviceIdentity: true })
        {
            return null;
        }

        return FromKey(identityKey);
    }

    /// <summary>
    /// Dasselbe, aber ausgehend vom Profilschlüssel.
    /// </summary>
    /// <remarks>
    /// Für die Aufrufer, die nur den Schlüssel zur Hand haben — die Wiederherstellung nach
    /// einem Neustart etwa liest ihn aus der Zustandsdatenbank. Ein Schlüssel, den der Katalog
    /// nicht kennt, ergibt <see langword="null"/>: Ein Profil, das es nicht mehr gibt, kann
    /// nicht zusichern, was seine Beschriftung bedeutet.
    /// </remarks>
    /// <param name="profileKey">Der Schlüssel des Profils, etwa <c>mstsc</c>.</param>
    /// <param name="identityKey">Der identifizierende Anteil des Ziels.</param>
    /// <returns>Die Kennung, oder <see langword="null"/>.</returns>
    public static string? From(string? profileKey, string? identityKey) =>
        string.IsNullOrWhiteSpace(profileKey)
            ? null
            : For(MonitoringProfiles.Find(profileKey), identityKey);

    private static string? FromKey(string? identityKey)
    {
        if (string.IsNullOrWhiteSpace(identityKey))
        {
            return null;
        }

        string trimmed = identityKey.Trim();

        // Der Platzhalter heisst "hier ist nichts aufgeloest worden". Als Kennung waere er die
        // eine Zeile, die auf ALLE unaufgeloesten Sitzungen passt - quer ueber alle Kunden.
        if (string.Equals(trimmed, SessionConstants.NoDestinationTitle, StringComparison.Ordinal))
        {
            return null;
        }

        return trimmed.Length is 0 or > MaximumLength ? null : trimmed;
    }
}
