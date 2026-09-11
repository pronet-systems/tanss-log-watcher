namespace TanssLogWatcher.Monitoring.Model;

/// <summary>
/// Eine laufende oder eben beendete Sitzung. Veränderlich, weil ihr Ziel im Betrieb nachgezogen
/// wird, ohne dass die Sitzung dabei endet.
/// </summary>
public sealed class WatchedSession
{
    /// <summary>Erzeugt eine Sitzung aus einem Kandidaten.</summary>
    /// <param name="candidate">Der beobachtete Kandidat.</param>
    /// <param name="startedAt">Der Beginn, den die Zustandsmaschine festgelegt hat.</param>
    /// <param name="ignoreTitleChanges">Ob Umbenennungen die Sitzung fortsetzen.</param>
    public WatchedSession(SessionCandidate candidate, DateTimeOffset startedAt, bool ignoreTitleChanges)
    {
        ProcessId = candidate.ProcessId;
        ProcessName = candidate.ProcessName;
        ProfileKey = candidate.ProfileKey;
        Destination = candidate.Destination;
        IdentityKey = candidate.IdentityKey;
        WindowHandle = candidate.WindowHandle;
        StartedAt = startedAt;
        IgnoreTitleChanges = ignoreTitleChanges;
        SessionId = Guid.NewGuid();
    }

    /// <summary>
    /// Unsere eigene Sitzungskennung. Sie wird als <c>remoteMaintenanceId</c> hochgeladen und ist
    /// damit die einzige Handhabe, einen Upload nach einer Zeitüberschreitung wiederzufinden —
    /// TANSS dedupliziert nicht.
    /// </summary>
    public Guid SessionId { get; }

    /// <summary>Der Prozess, dem die Sitzung gehört.</summary>
    public int ProcessId { get; }

    /// <summary>Der Prozessname ohne Erweiterung.</summary>
    public string ProcessName { get; }

    /// <summary>Der Schlüssel des Profils, das die Sitzung erzeugt hat.</summary>
    public string ProfileKey { get; }

    /// <summary>Ob Umbenennungen des Fensters die Sitzung fortsetzen.</summary>
    public bool IgnoreTitleChanges { get; }

    /// <summary>
    /// Das Fenster, aus dem das Ziel stammt.
    /// </summary>
    /// <remarks>
    /// Veränderlich aus demselben Grund wie <see cref="Destination"/>: übernimmt ein Platzhalter
    /// sein erstes echtes Ziel, kann dieses Ziel aus einem <b>anderen</b> Fenster desselben
    /// Prozesses stammen als dem, mit dem der Platzhalter angelegt wurde. Bliebe das alte Handle
    /// stehen, fände die handlebasierte Übernahme in der Zustandsmaschine die Sitzung nie wieder
    /// und die nächste Umbenennung spaltete sie — trotz
    /// <see cref="MonitoringProfile.IgnoreTitleChanges"/>.
    /// </remarks>
    public nint WindowHandle { get; internal set; }

    /// <summary>Der Beginn der Sitzung.</summary>
    public DateTimeOffset StartedAt { get; }

    /// <summary>Das Ende, sobald es feststeht.</summary>
    public DateTimeOffset? EndedAt { get; internal set; }

    /// <summary>
    /// Der Zieltext. Wird übernommen, wenn ein Platzhalter sein erstes echtes Ziel bekommt, wenn
    /// ein Fenster sich umbenennt oder wenn der Rückwärtsauflöser einen Namen nachliefert.
    /// </summary>
    public string Destination { get; internal set; }

    /// <summary>Der identifizierende Teil des Ziels. Siehe <see cref="SessionCandidate.IdentityKey"/>.</summary>
    public string IdentityKey { get; internal set; }

    /// <summary>Wahr, solange die Sitzung nur den Platzhalter trägt.</summary>
    public bool HasNoDestination =>
        string.Equals(Destination, SessionConstants.NoDestinationTitle, StringComparison.Ordinal);

    /// <summary>Die Dauer bis zum Ende, sonst bis zum übergebenen Zeitpunkt.</summary>
    /// <param name="now">Der Bezugszeitpunkt für noch laufende Sitzungen.</param>
    public TimeSpan Duration(DateTimeOffset now) => (EndedAt ?? now) - StartedAt;
}
