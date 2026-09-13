using System.Globalization;
using System.Text.Json.Serialization;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Repository;

/// <summary>Die Timer des angemeldeten Technikers.</summary>
/// <remarks>
/// <para>Alle Timer-Routen liegen auf <c>/api/v1</c> und brauchen <c>loggedInUserId</c>; der
/// Client hängt ihn selbst an.</para>
/// <para><b>TANSS kennt nur einen Umschalter.</b> Es gibt kein „starten“ und kein „anhalten“,
/// sondern <c>PUT /api/v1/timers/{id}</c> ohne Rumpf, das den jeweils anderen Zustand
/// herstellt und mit 202 quittiert. Wer den Zustand kennen will, liest
/// <see cref="TanssTimer.IsRunning"/> — ein Feld <c>isRunning</c> liefert der Server nicht.</para>
/// <para><b>Die Routen sind nicht geraten, sondern nachgemessen</b> gegen eine Instanz der
/// Version 10.10.0. Wichtig ist vor allem die
/// Trennung bei den Notizen: <c>POST /api/v1/timers/notes/{id}</c> hängt eine Notiz an den
/// letzten Abschnitt an, <c>PUT /api/v1/timers/notes</c> ändert einen bestehenden Abschnitt und
/// prüft dabei dessen Prüfsumme. Wer beides verwechselt, überschreibt fremden Text oder
/// scheitert an der Prüfsumme.</para>
/// </remarks>
public sealed class TimerRepository : ITimerRepository
{
    private readonly ITanssClient _client;
    private readonly ITanssBodyDelete? _bodyDelete;

    /// <summary>Baut das Repository.</summary>
    /// <param name="client">
    /// Der HTTP-Zugang. Er muss zusätzlich <see cref="ITanssBodyDelete"/> erfüllen, sonst
    /// lässt sich kein Timer löschen — TANSS nimmt die Kennung dort ausschließlich im Rumpf.
    /// </param>
    public TimerRepository(ITanssClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _bodyDelete = client as ITanssBodyDelete;
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TanssTimer>> ListAsync(CancellationToken ct = default)
    {
        List<TanssTimer>? timers = await _client
            .GetAsync<List<TanssTimer>>(TanssRoutes.Timers, ct: ct).ConfigureAwait(false);

        return timers ?? [];
    }

    /// <inheritdoc />
    public async Task<TanssTimer> GetAsync(int timerId, CancellationToken ct = default)
    {
        TanssTimer? timer = await _client
            .GetAsync<TanssTimer>(Path(timerId), ct: ct).ConfigureAwait(false);

        return timer ?? throw new TanssException(
            $"TANSS kennt den Timer {timerId.ToString(CultureInfo.InvariantCulture)} nicht mehr. "
            + "Üblicherweise wurde er zwischenzeitlich in TANSS gelöscht oder gehört einem "
            + "anderen Techniker. Die Timerliste ist neu einzulesen.");
    }

    /// <inheritdoc />
    public Task<TanssTimer> CreateAsync(string title, int ticketId = 0,
                                        CancellationToken ct = default) =>
        CreateAsync(new TimerDraft { Title = title ?? string.Empty, TicketId = ticketId }, ct);

    /// <inheritdoc />
    public async Task<TanssTimer> CreateAsync(TimerDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        TanssTimer? created = await _client
            .PostAsync<TanssTimer>(TanssRoutes.Timers, draft, ct: ct).ConfigureAwait(false);

        return created ?? throw new TanssException(
            "TANSS hat den Timer angelegt, aber nicht zurückgegeben. Ohne seine ID lässt er sich "
            + "von hier aus nicht mehr ansprechen; er ist in TANSS nachzusehen.");
    }

    /// <inheritdoc />
    public async Task<TanssTimer> ToggleAsync(int timerId, CancellationToken ct = default)
    {
        // Ohne Rumpf: die Route nimmt ausschliesslich die Kennung aus dem Pfad.
        TanssTimer? toggled = await _client
            .PutAsync<TanssTimer>(Path(timerId), ct: ct).ConfigureAwait(false);

        return toggled ?? throw new TanssException(
            "TANSS hat den Timer umgeschaltet, aber seinen neuen Zustand nicht mitgeteilt. "
            + "Der Zustand ist neu einzulesen, nicht erneut umzuschalten — sonst steht er "
            + "am Ende falsch herum.");
    }

    /// <inheritdoc />
    /// <remarks>
    /// TANSS nimmt die Kennung im <b>Rumpf</b> des DELETE entgegen. Eine Route, die sie als
    /// Abfrageparameter nähme, gibt es in 10.10.0 nicht — nachgemessen.
    /// </remarks>
    public Task DeleteAsync(int timerId, CancellationToken ct = default)
    {
        if (_bodyDelete is null)
        {
            // Kein Rueckfallweg, weil es keinen gibt: ein DELETE mit ?id= wuerde stillschweigend
            // nichts loeschen oder mit 400 scheitern - beides schlimmer als ein klarer Fehler.
            throw new InvalidOperationException(
                "Dieser HTTP-Zugang kann kein DELETE mit Rumpf senden, deshalb lässt sich der "
                + "Timer nicht löschen. Das ist ein Programmierfehler, kein Bedienfehler: "
                + "TANSS nimmt die Timerkennung ausschließlich im Rumpf entgegen, und der "
                + "übergebene "
                + $"{_client.GetType().Name} erfüllt {nameof(ITanssBodyDelete)} nicht. "
                + $"Dem {nameof(TimerRepository)} ist ein Zugang zu übergeben, der beides "
                + "erfüllt — der mitgelieferte "
                + $"{nameof(TanssClient)} tut das.");
        }

        return _bodyDelete.DeleteWithBodyAsync(TanssRoutes.Timers, new TimerReference { Id = timerId },
                                               ct: ct);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<TimerFragment>> ListNotesAsync(int timerId,
                                                                   CancellationToken ct = default)
    {
        List<TimerFragment>? notes = await _client
            .GetAsync<List<TimerFragment>>(NotePath(timerId), ct: ct).ConfigureAwait(false);

        return notes ?? [];
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para>Die Route ist <c>POST /api/v1/timers/notes/{id}</c>. Der Server hängt den Text an
    /// den <b>zuletzt begonnenen</b> Abschnitt des Timers an, getrennt durch einen
    /// Zeilenumbruch; vorhandener Text bleibt stehen.</para>
    /// <para>Die <c>timerId</c> steht zusätzlich im Rumpf, obwohl der Pfad sie ohnehin trägt:
    /// Die Zugriffsprüfung des Servers wertet den Wert aus dem Rumpf aus. Ein Rumpf mit
    /// <c>timerId</c> 0 läuft deshalb in eine Prüfung auf einen Timer, den es nicht gibt —
    /// nachgemessen, mit 403 quittiert.</para>
    /// </remarks>
    public Task AddNoteAsync(int timerId, string note, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(note))
        {
            // Der Server antwortet auf eine leere Notiz mit TnsMissingFieldValueException.
            throw new ArgumentException(
                "Eine leere Notiz lässt sich nicht an einen Timer hängen. Üblicherweise steckt "
                + "dahinter ein Text, der erst beim Zusammensetzen leer geblieben ist. Der Text "
                + "ist vor dem Aufruf zu prüfen.", nameof(note));
        }

        TimerNote body = new() { TimerId = timerId, Note = note };
        return _client.PostAsync<object>(NotePath(timerId), body, ct: ct);
    }

    /// <inheritdoc />
    /// <remarks>
    /// <para><c>PUT /api/v1/timers/notes</c> ersetzt den Text eines bestehenden Abschnitts. Der
    /// Server sucht ihn über <c>timerId</c>, <c>startTime</c> und <c>stopTime</c> und vergleicht
    /// die mitgeschickte Prüfsumme mit der eigenen — stimmt sie nicht, hat inzwischen jemand
    /// anders geschrieben, und TANSS lehnt ab.</para>
    /// <para>Deshalb wird hier ein zuvor <b>gelesenes</b> Fragment übergeben und kein von Hand
    /// zusammengesetztes: die Prüfsumme stammt aus dem Server.</para>
    /// </remarks>
    public Task UpdateNoteAsync(TimerFragment fragment, string note, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(fragment);

        if (string.IsNullOrWhiteSpace(note))
        {
            throw new ArgumentException(
                "Ein Abschnitt lässt sich nicht auf einen leeren Text ändern. TANSS weist das "
                + "mit einem fehlenden Pflichtfeld ab. Soll die Notiz verschwinden, ist der "
                + "Abschnitt in TANSS zu löschen.", nameof(note));
        }

        TimerNoteUpdate body = new()
        {
            TimerId = fragment.TimerId,
            StartTime = fragment.StartTime,
            StopTime = fragment.StopTime,
            Note = note,
            Hash = fragment.Hash,
        };

        return _client.PutAsync<object>(TanssRoutes.TimerNotes, body, ct: ct);
    }

    private static string Path(int timerId) =>
        TanssRoutes.Timers + "/" + timerId.ToString(CultureInfo.InvariantCulture);

    private static string NotePath(int timerId) =>
        TanssRoutes.TimerNotes + "/" + timerId.ToString(CultureInfo.InvariantCulture);

    /// <summary>Der Rumpf des Löschens. TANSS wertet ausschließlich <c>id</c> aus.</summary>
    private sealed record TimerReference
    {
        [JsonPropertyName("id")] public required int Id { get; init; }
    }

    /// <summary>Der Rumpf beim Anhängen einer Notiz — ein Laufabschnitt.</summary>
    private sealed record TimerNote
    {
        [JsonPropertyName("timerId")] public required int TimerId { get; init; }
        [JsonPropertyName("note")] public required string Note { get; init; }
    }

    /// <summary>Der Rumpf beim Ändern eines bestehenden Abschnitts.</summary>
    private sealed record TimerNoteUpdate
    {
        [JsonPropertyName("timerId")] public required int TimerId { get; init; }
        [JsonPropertyName("startTime")] public required long StartTime { get; init; }
        [JsonPropertyName("stopTime")] public required long StopTime { get; init; }
        [JsonPropertyName("note")] public required string Note { get; init; }

        /// <summary>Die Prüfsumme des gelesenen Abschnitts; TANSS lehnt bei Abweichung ab.</summary>
        [JsonPropertyName("hash")] public required int Hash { get; init; }
    }
}
