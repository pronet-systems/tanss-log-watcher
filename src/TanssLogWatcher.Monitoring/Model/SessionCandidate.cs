namespace TanssLogWatcher.Monitoring.Model;

/// <summary>
/// Eine im laufenden Durchlauf beobachtete Sitzung, noch ohne Abgleich mit dem bisherigen Bestand.
/// </summary>
/// <param name="ProcessId">Der Prozess, dem die Sitzung gehört.</param>
/// <param name="ProcessName">Der Prozessname ohne Erweiterung.</param>
/// <param name="ProfileKey">Der Schlüssel des Profils, das diesen Kandidaten erzeugt hat.</param>
/// <param name="Destination">
/// Der Zieltext, wie er später im Kommentar der Fernwartung steht.
/// </param>
/// <param name="IdentityKey">
/// Der Teil des Ziels, der eine Sitzung <b>identifiziert</b>.
/// <para>Bei den beiden Titelverfahren ist er mit <paramref name="Destination"/> gleich. Bei
/// <see cref="DestinationResolveMethod.RemoteHostIpConnection"/> ist er die reine IP-Adresse,
/// während das Ziel zusätzlich den rückwärts aufgelösten Namen trägt. Das ist Absicht: der Name
/// trifft je nach Zwischenspeicher erst einen Durchlauf später ein, und ohne diese Trennung würde
/// sein Eintreffen die laufende Sitzung beenden und eine neue beginnen.</para>
/// </param>
/// <param name="WindowHandle">Das Fenster, aus dem das Ziel stammt; 0, wenn keines beteiligt war.</param>
/// <param name="StartedAt">Der Startzeitpunkt des Prozesses, sofern ermittelbar.</param>
public readonly record struct SessionCandidate(
    int ProcessId,
    string ProcessName,
    string ProfileKey,
    string Destination,
    string IdentityKey,
    nint WindowHandle,
    DateTimeOffset? StartedAt)
{
    /// <summary>Wahr, wenn dieser Kandidat nur der Platzhalter ist.</summary>
    public bool HasNoDestination =>
        string.Equals(Destination, SessionConstants.NoDestinationTitle, StringComparison.Ordinal);
}
