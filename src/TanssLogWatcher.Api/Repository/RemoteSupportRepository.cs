using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Repository;

/// <summary>Fernwartungen anlegen, nachschlagen und auf Vorhandensein prüfen.</summary>
/// <remarks>
/// <para><b>Anlegen und Lesen laufen über verschiedene Präfixe.</b> Geschrieben wird auf
/// <c>/api/tanss.x/v1/remoteSupports</c> (ohne <c>loggedInUserId</c>), gelesen auf
/// <c>PUT /api/v1/remoteSupports</c> (mit). Der Client hält die Regel selbst ein.</para>
/// <para><b>Es wird niemals blind wiederholt geschrieben.</b> TANSS dedupliziert nicht: ein
/// zweiter POST mit derselben <c>remoteMaintenanceId</c> erzeugte am 11.09.2026 nachweislich
/// einen zweiten Datensatz (IDs 38584 und 38585). Nach einer Zeitüberschreitung fragt der
/// Aufrufer erst <see cref="ExistsAsync(RemoteSupportWrite, CancellationToken)"/>.</para>
/// </remarks>
public sealed class RemoteSupportRepository : IRemoteSupportRepository
{
    /// <summary>Zuschlag auf beide Enden des Suchfensters der Existenzprüfung.</summary>
    /// <remarks>
    /// <para>Großzügig und bewusst nicht knapp. Das Fenster grenzt nur die Serverabfrage ein —
    /// entschieden wird über den genauen Vergleich der Sitzungskennung, und die ist eindeutig.
    /// Ein zu weites Fenster kostet also nichts außer ein paar Sätzen mehr auf dem Server.</para>
    /// <para>Ein zu enges dagegen kostet eine Dublette: verfehlt die Prüfung den vorhandenen
    /// Datensatz, weil die Serveruhr abweicht, gilt die Sitzung als nicht hochgeladen, und
    /// TANSS nimmt sie ein zweites Mal an, ohne zu deduplizieren.</para>
    /// </remarks>
    private static readonly TimeSpan WindowPadding = TimeSpan.FromDays(1);

    private readonly ITanssClient _client;
    private readonly RetryPolicy _retry;
    private readonly int _expectedEmployeeId;

    /// <summary>Baut das Repository.</summary>
    /// <param name="client">Der HTTP-Zugang.</param>
    /// <param name="expectedEmployeeId">
    /// Die Mitarbeiter-ID, unter der geschrieben wird. Sie dient nur der Gegenprobe der
    /// Attribution in <see cref="CreateWithDiagnosticsAsync"/>; gesendet wird die ID aus dem
    /// jeweiligen <see cref="RemoteSupportWrite"/>.
    /// </param>
    /// <param name="retry">
    /// Wiederholung für die <b>lesende</b> PUT-Abfrage. Der Client kann sie dort nicht selbst
    /// anwenden, weil er PUT nicht von einem Schreibvorgang unterscheiden kann.
    /// </param>
    public RemoteSupportRepository(ITanssClient client, int expectedEmployeeId = 0,
                                   RetryPolicy? retry = null)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
        _expectedEmployeeId = expectedEmployeeId;
        _retry = retry ?? RetryPolicy.Default;
    }

    /// <inheritdoc />
    public async Task<RemoteSupportRead> CreateAsync(RemoteSupportWrite item,
                                                     CancellationToken ct = default) =>
        (await CreateWithDiagnosticsAsync(item, ct).ConfigureAwait(false)).Support;

    /// <summary>
    /// Legt eine Fernwartung an und meldet zusätzlich, ob die Attribution serverseitig gegriffen hat.
    /// </summary>
    /// <remarks>
    /// <para>Der Nachweis steht nicht im Inhalt, sondern im <c>meta</c>-Block: enthält
    /// <c>meta.linkedEntities.employees</c> den erwarteten Mitarbeiter, hat TANSS die
    /// Fernwartung diesem Techniker zugeschrieben. Fehlt er, ist der Datensatz trotzdem
    /// angelegt — er hängt dann aber möglicherweise an niemandem und taucht in der Auswertung
    /// des Technikers nicht auf.</para>
    /// <para>Deshalb wird hier <b>nicht</b> geworfen: ein geworfener Fehler würde den Aufrufer
    /// zur Wiederholung verleiten, und die erzeugte dann einen zweiten Datensatz. Gemeldet wird
    /// stattdessen — sichtbar, aber folgenlos.</para>
    /// </remarks>
    public async Task<RemoteSupportCreateResult> CreateWithDiagnosticsAsync(
        RemoteSupportWrite item, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        // Bewusst ohne Wiederholung: schreibend.
        (RemoteSupportRead? content, IReadOnlyDictionary<string, object?> meta) =
            await _client.PostWithMetaAsync<RemoteSupportRead>(
                TanssRoutes.RemoteSupportsCreate, item, ct: ct).ConfigureAwait(false);

        if (content is null)
        {
            throw new TanssException(
                "TANSS hat das Anlegen der Fernwartung quittiert, aber keinen Datensatz "
                + "zurückgegeben. Ob sie angekommen ist, muss über die Existenzprüfung geklärt "
                + "werden — auf keinen Fall blind erneut senden, TANSS dedupliziert nicht.");
        }

        int expected = _expectedEmployeeId != 0 ? _expectedEmployeeId : item.EmployeeId;
        bool confirmed = HasLinkedEmployee(meta, expected);

        string? warning = confirmed ? null
            : string.Create(CultureInfo.InvariantCulture,
                $"Die Fernwartung wurde angelegt (ID {content.Id}), aber TANSS weist den "
                + $"Mitarbeiter {expected} nicht in meta.linkedEntities.employees aus. Die "
                + $"Zuordnung zum Techniker ist damit nicht bestätigt; der Datensatz ist in TANSS "
                + $"zu prüfen. Nicht erneut senden.");

        return new RemoteSupportCreateResult(content, confirmed, warning);
    }

    /// <inheritdoc />
    /// <remarks>
    /// Der Textfilter von TANSS durchsucht <c>comment</c> <b>und</b> <c>remoteMaintenanceId</c>.
    /// Deshalb reicht ein Treffer allein nicht: erst der genaue Vergleich der Kennung
    /// entscheidet, sonst würde eine GUID, die zufällig in einem Kommentar steht, als
    /// vorhandener Upload gelten.
    /// </remarks>
    public Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset around,
                                  CancellationToken ct = default) =>
        // Ein einzelner Bezugszeitpunkt sagt nichts ueber die Dauer. Wer die Sitzung hat,
        // nimmt die Ueberladung mit Anfang und Ende.
        ExistsAsync(remoteMaintenanceId, around, around, ct);

    /// <inheritdoc />
    public Task<bool> ExistsAsync(RemoteSupportWrite session, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(session);

        DateTimeOffset now = DateTimeOffset.UtcNow;

        // 0 heisst in TANSS "nicht gesetzt": beim Ende "laeuft noch", beim Anfang ein Datensatz,
        // der so gar nicht haette entstehen duerfen. Beide Male ist jetzt der beste Anker.
        DateTimeOffset start = TanssTime.FromUnixSeconds(session.StartTime) ?? now;
        DateTimeOffset end = TanssTime.FromUnixSeconds(session.EndTime) ?? now;

        return ExistsAsync(session.RemoteMaintenanceId, start, end, ct);
    }

    /// <inheritdoc />
    public async Task<bool> ExistsAsync(string remoteMaintenanceId, DateTimeOffset sessionStart,
                                        DateTimeOffset sessionEnd, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(remoteMaintenanceId);

        // Vertauschte Grenzen sind kein Grund aufzugeben: ein leeres Fenster faende nichts,
        // und "nichts gefunden" heisst hier "noch nicht hochgeladen" - der teure Irrtum.
        (DateTimeOffset from, DateTimeOffset till) =
            sessionStart <= sessionEnd ? (sessionStart, sessionEnd) : (sessionEnd, sessionStart);

        Timeframe window = new()
        {
            From = TanssTime.ToUnixSeconds(from - WindowPadding),
            Till = TanssTime.ToUnixSeconds(till + WindowPadding),
        };

        IReadOnlyList<RemoteSupportRead> found =
            await ListAsync(window, remoteMaintenanceId, ct).ConfigureAwait(false);

        return found.Any(entry => string.Equals(entry.RemoteMaintenanceId, remoteMaintenanceId,
                                                StringComparison.Ordinal));
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteSupportRead>> ListAsync(Timeframe timeframe,
                                                                  string? text = null,
                                                                  CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(timeframe);

        RemoteSupportQuery body = new() { Timeframe = timeframe, Text = text };

        // Lesend, obwohl PUT: hier darf und soll wiederholt werden.
        return await _retry.ExecuteReadAsync<IReadOnlyList<RemoteSupportRead>>(async (_, token) =>
        {
            JsonElement? payload = await _client
                .PutAsync<JsonElement>(TanssRoutes.RemoteSupportsList, body, ct: token)
                .ConfigureAwait(false);

            // Eine leere 200 ist ein leerer Erfolg und damit eine leere Liste.
            return payload is { } element ? ReadList(element) : [];
        }, ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public async Task<IReadOnlyList<RemoteSupportSystem>> ListSystemsAsync(CancellationToken ct = default)
    {
        List<RemoteSupportSystem>? systems = await _client
            .GetAsync<List<RemoteSupportSystem>>(TanssRoutes.RemoteSupportSystems, ct: ct)
            .ConfigureAwait(false);

        return systems ?? [];
    }

    /// <summary>Steht der erwartete Mitarbeiter in <c>meta.linkedEntities.employees</c>?</summary>
    public static bool HasLinkedEmployee(IReadOnlyDictionary<string, object?> meta, int employeeId)
    {
        ArgumentNullException.ThrowIfNull(meta);

        if (!meta.TryGetValue("linkedEntities", out object? linked) || linked is not JsonElement entities
            || entities.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (!entities.TryGetProperty("employees", out JsonElement employees)
            || employees.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        return employees.TryGetProperty(employeeId.ToString(CultureInfo.InvariantCulture), out _);
    }

    /// <summary>Liest die Liste aus dem <c>content</c> der Filterabfrage.</summary>
    /// <remarks>
    /// <para><b>Genau eine Form wird angenommen: ein schlichtes JSON-Feld.</b> Am 11.09.2026
    /// gemessen, zweifach — 30 121 Sätze über den Zeitraum 2020 bis 2027 und 2 Sätze beim
    /// Textfilter auf eine Sitzungskennung. Der Umschlag trägt die Liste unmittelbar unter
    /// <c>content</c>, ohne Zwischenobjekt.</para>
    /// <para>Jede andere Form ist deshalb ein Fehler und <b>keine leere Liste</b>. Der
    /// Unterschied ist der ganze Punkt dieser Methode: eine fälschlich leere Liste hieße
    /// „nicht vorhanden“, die Existenzprüfung gäbe grünes Licht, und weil TANSS nicht
    /// dedupliziert, stünde die Fernwartung anschließend zweimal in der Abrechnung.</para>
    /// </remarks>
    private static List<RemoteSupportRead> ReadList(JsonElement payload)
    {
        if (payload.ValueKind != JsonValueKind.Array)
        {
            throw new TanssException(
                "Die Filterabfrage PUT /api/v1/remoteSupports hat kein Feld von Fernwartungen "
                + $"geliefert, sondern {Describe(payload.ValueKind)}. Das passt zu einer "
                + "geänderten TANSS-Fassung — die Route ist undokumentiert und kann sich mit "
                + "einem Update ändern. Bis das geklärt ist, darf nichts hochgeladen werden: "
                + "diese Abfrage ist die einzige Existenzprüfung, und TANSS dedupliziert nicht. "
                + "Die Antwortform ist gegen die TANSS-Fassung abzugleichen.");
        }

        return payload.Deserialize<List<RemoteSupportRead>>(TanssJson.Options) ?? [];
    }

    /// <summary>Benennt die vorgefundene JSON-Form so, dass die Meldung ohne Rumpf auskommt.</summary>
    /// <remarks>
    /// Der Rumpf selbst gehört nicht in die Meldung: er trägt Kundendaten und Sitzungskennungen.
    /// </remarks>
    private static string Describe(JsonValueKind kind) => kind switch
    {
        JsonValueKind.Object => "ein Objekt",
        JsonValueKind.String => "eine Zeichenkette",
        JsonValueKind.Number => "eine Zahl",
        JsonValueKind.True or JsonValueKind.False => "einen Wahrheitswert",
        _ => "einen Wert der Art " + kind.ToString(),
    };

    /// <summary>Rumpf der Filterabfrage.</summary>
    private sealed record RemoteSupportQuery
    {
        [JsonPropertyName("timeframe")] public required Timeframe Timeframe { get; init; }

        /// <summary>
        /// Ohne Filter bleibt das Feld weg. Ein mitgeschicktes <c>null</c> nahm TANSS zwar
        /// hin, aber ein fehlendes Feld ist der belegte Weg.
        /// </summary>
        [JsonPropertyName("text")]
        [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        public string? Text { get; init; }
    }
}
