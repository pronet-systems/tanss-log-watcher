namespace TanssLogWatcher.App.Services;

/// <summary>Was ein Hintergrunddienst gerade tut.</summary>
public enum ServiceState
{
    /// <summary>Noch nicht gestartet oder bereits beendet.</summary>
    Stopped,

    /// <summary>Läuft und wartet auf den nächsten Takt.</summary>
    Idle,

    /// <summary>Arbeitet gerade.</summary>
    Working,

    /// <summary>
    /// Läuft, tut aber nichts — abgeschaltet, oder es fehlt die Voraussetzung.
    /// </summary>
    /// <remarks>
    /// Getrennt von <see cref="Stopped"/>, weil der Unterschied für den Techniker zählt: Ein
    /// angehaltener Dienst nimmt seine Arbeit von selbst wieder auf, sobald die Voraussetzung
    /// da ist. Ein beendeter nicht.
    /// </remarks>
    Paused,

    /// <summary>
    /// Der letzte Takt ist fehlgeschlagen. Der Dienst läuft weiter.
    /// </summary>
    /// <remarks>
    /// Hausregel 5: Der Fehler eines einzelnen Vorgangs bricht niemals einen ganzen Durchlauf
    /// ab. Ein gestörter Takt ist deshalb ein Befund und kein Ende — der nächste kann gelingen.
    /// </remarks>
    Faulted,
}

/// <summary>Der Stand eines Hintergrunddienstes, wie ihn die Oberfläche anzeigt.</summary>
/// <remarks>
/// Unveränderlich und als Ganzes ausgetauscht, damit eine Ansicht nie einen halb
/// fortgeschriebenen Stand liest — „arbeitet“ neben der Fehlermeldung des vorletzten Takts.
/// </remarks>
public sealed record ServiceActivity
{
    /// <summary>Der Zustand.</summary>
    public required ServiceState State { get; init; }

    /// <summary>Was gerade geschieht oder zuletzt geschah — in einem Satz, deutsch.</summary>
    public required string Message { get; init; }

    /// <summary>Wann dieser Stand entstand.</summary>
    public DateTimeOffset At { get; init; } = DateTimeOffset.Now;

    /// <summary>Wann der nächste Takt frühestens läuft; <c>null</c>, wenn keiner ansteht.</summary>
    public DateTimeOffset? NextRunAt { get; init; }

    /// <summary>Wie viele Takte dieser Dienst seit dem Start gelaufen ist.</summary>
    public long Cycles { get; init; }

    /// <summary>Die letzte Fehlermeldung, bereits geschwärzt; <c>null</c>, wenn es keine gibt.</summary>
    public string? LastError { get; init; }
}

/// <summary>
/// Ein Dienst, der im Hintergrund im Takt arbeitet.
/// </summary>
/// <remarks>
/// <para><b>Jeder einzeln abschaltbar</b> (<see cref="IsEnabled"/>). Das ist kein Komfort,
/// sondern das Werkzeug zur Eingrenzung: Wer den Verdacht hat, dass das Senden Dubletten
/// erzeugt, soll es anhalten können, ohne die Beobachtung zu verlieren — die Sitzungen laufen
/// dann weiter in die Warteschlange, aus der sie später geordnet hinausgehen.</para>
///
/// <para><b>Jeder mit isolierten Fehlern</b> (Hausregel 5). Ein Fehlschlag beendet einen Takt,
/// nie den Dienst, und niemals die übrigen zwei.</para>
///
/// <para><b>Meldet über Ereignisse, statt sich abfragen zu lassen.</b> Eine Oberfläche, die
/// im Sekundentakt nachsähe, hielte die Zustandsdatenbank ohne Not offen und zeigte
/// trotzdem alles um bis zu eine Sekunde verspätet.</para>
/// </remarks>
public interface IBackgroundService
{
    /// <summary>Der Anzeigename, deutsch.</summary>
    string Name { get; }

    /// <summary>Wozu dieser Dienst da ist — ein Satz für die Anzeige.</summary>
    string Description { get; }

    /// <summary>
    /// Arbeitet dieser Dienst? Auf <c>false</c> gesetzt hält er an, ohne sich zu beenden.
    /// </summary>
    /// <remarks>
    /// Das Abschalten wirkt ab dem nächsten Takt. Ein laufender Takt wird zu Ende geführt —
    /// mittendrin abzubrechen hiesse beim Senden, einen Eintrag mit ungeklärtem Ausgang
    /// zurückzulassen.
    /// </remarks>
    bool IsEnabled { get; set; }

    /// <summary>Der aktuelle Stand.</summary>
    ServiceActivity Activity { get; }

    /// <summary>Meldet jeden neuen Stand; wird auf dem Strang der Oberfläche ausgelöst.</summary>
    event EventHandler<ServiceActivity>? ActivityChanged;

    /// <summary>Startet den Takt. Blockiert nicht.</summary>
    /// <param name="ct">Abbruchmarke für den Start selbst.</param>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>
    /// Hält den Takt an und räumt geordnet auf.
    /// </summary>
    /// <remarks>
    /// Geordnet heisst: Was noch offen ist, wird gesichert, nicht verworfen. Beim
    /// Sitzungsdienst sind das die laufenden Fernwartungen — der Mangel der Vorlage, der bares
    /// Geld kostete.
    /// </remarks>
    /// <param name="ct">Abbruchmarke; sie begrenzt das Warten, nicht das Aufräumen.</param>
    Task StopAsync(CancellationToken ct = default);
}
