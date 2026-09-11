namespace TanssLogWatcher.Api.Http;

/// <summary>
/// Wiederholung fehlgeschlagener Aufrufe — ausschließlich für lesende Aufrufe.
/// </summary>
/// <remarks>
/// <para><b>Schreibende Aufrufe werden niemals automatisch wiederholt.</b> Eine
/// Zeitüberschreitung heißt nicht, dass der Server nichts getan hat: die Anfrage kann
/// angekommen, verarbeitet und nur die Antwort verloren gegangen sein. Und <b>TANSS
/// dedupliziert nicht</b> — ein zweiter POST mit identischer <c>remoteMaintenanceId</c> erzeugte
/// am 11.09.2026 nachweislich einen zweiten Datensatz (IDs 38584 und 38585). Eine
/// automatische Wiederholung würde also aus jedem Netzwackler eine doppelt abgerechnete
/// Fernwartung machen. Wiederholt wird nach einem Schreibfehler nur von Hand und nur nach
/// vorheriger Existenzprüfung.</para>
/// <para>Wiederholt wird außerdem nur, was sich von selbst erledigen kann: Netzfehler und
/// 5xx. Eine 403 wiederholt sich nicht von allein, sie braucht einen Menschen.</para>
/// </remarks>
public sealed record RetryPolicy
{
    /// <summary>Versuche insgesamt, den ersten eingerechnet.</summary>
    public int MaxAttempts { get; init; } = 5;

    /// <summary>Grundwartezeit; sie verdoppelt sich mit jedem Versuch.</summary>
    public TimeSpan BaseDelay { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Deckel für die Wartezeit eines einzelnen Versuchs.</summary>
    public TimeSpan MaxDelay { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>
    /// Streuung: liefert einen Wert in <c>[0,1)</c>, der die Wartezeit zwischen der Hälfte und
    /// der vollen Länge staucht. Verhindert, dass viele Arbeitsplätze nach einem Serverneustart
    /// im Gleichtakt anklopfen. Austauschbar, damit Tests nicht würfeln müssen.
    /// </summary>
    public Func<double> Jitter { get; init; } = Random.Shared.NextDouble;

    /// <summary>
    /// Das Warten selbst. Austauschbar, damit Tests die Wiederholung prüfen können, ohne
    /// eine Minute lang zu schlafen.
    /// </summary>
    public Func<TimeSpan, CancellationToken, Task> Sleep { get; init; } =
        static (delay, ct) => Task.Delay(delay, ct);

    /// <summary>Die Hausvorgabe: fünf Versuche, 1 s Grundwartezeit, Deckel 60 s.</summary>
    public static RetryPolicy Default { get; } = new();

    /// <summary>Keine Wiederholung. Die richtige Wahl für alles Schreibende.</summary>
    public static RetryPolicy None { get; } = new() { MaxAttempts = 1 };

    /// <summary>Die Wartezeit vor dem <paramref name="attempt"/>-ten Versuch (1-basiert).</summary>
    public TimeSpan DelayFor(int attempt)
    {
        // Ueber double rechnen, weil BaseDelay * 2^n bei grossen Werten sonst ueberlaeuft.
        double exponential = BaseDelay.TotalMilliseconds * Math.Pow(2, Math.Max(0, attempt - 1));
        double capped = Math.Min(exponential, MaxDelay.TotalMilliseconds);
        double spread = capped * (0.5 + (0.5 * Math.Clamp(Jitter(), 0, 1)));
        return TimeSpan.FromMilliseconds(Math.Min(spread, MaxDelay.TotalMilliseconds));
    }

    /// <summary>
    /// Führt einen <b>lesenden</b> Aufruf aus und wiederholt ihn bei Netzfehlern und 5xx.
    /// </summary>
    /// <param name="read">
    /// Der Aufruf. Er bekommt die Nummer des Versuchs, damit der Aufrufer ihn protokollieren kann.
    /// </param>
    /// <param name="ct">Abbruchmarke; ein Abbruch beendet die Wiederholung sofort.</param>
    /// <exception cref="TanssException">
    /// Der letzte Versuch ist gescheitert. Die Ausnahme ist die des letzten Versuchs, damit die
    /// Ursache erhalten bleibt.
    /// </exception>
    public async Task<T> ExecuteReadAsync<T>(Func<int, CancellationToken, Task<T>> read,
                                             CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(read);

        for (int attempt = 1; ; attempt++)
        {
            try
            {
                return await read(attempt, ct).ConfigureAwait(false);
            }
            catch (TanssException ex) when (attempt < MaxAttempts && IsTransient(ex))
            {
                await Sleep(DelayFor(attempt), ct).ConfigureAwait(false);
            }
        }
    }

    /// <summary>
    /// Erledigt sich der Fehler vielleicht von selbst? Nur Netzfehler und 5xx tun das.
    /// </summary>
    public static bool IsTransient(TanssException error) => error switch
    {
        TanssUnreachableException => true,
        TanssAuthException => false,
        // Eine falsche Basisadresse heilt nicht durch Warten, sie braucht eine Korrektur.
        TanssConfigurationException => false,
        TanssNotFoundException => false,
        TanssRemoteSupportTypeException => false,
        TanssModuleNotLicensedException => false,
        _ => error.Status is >= 500 and < 600,
    };
}
