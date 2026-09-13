using System.Globalization;
using System.Text.Json.Nodes;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;

namespace TanssLogWatcher.Api.Repository;

/// <summary>
/// Leistungen aus Timern.
/// </summary>
/// <remarks>
/// <para><b>Der Weg ist nachgemessen, nicht geraten</b> — gegen eine Instanz der Fassung
/// 10.10.0. <c>POST /api/v1/supports/properties</c> mit
/// <c>{"initializers":[{"type":"TIMER","id":26}]}</c> antwortet mit 200 und einer vorbelegten
/// Leistung, in der <c>usesTimerId</c> auf den Timer zeigt und <c>multiTextItems</c> genau so
/// viele Einträge hat, wie der Timer Laufabschnitte hat. <c>PUT /api/v1/supports</c> antwortet
/// mit 405 und <c>Allow: POST</c>.</para>
///
/// <para><b>Der vorbelegte Block wird unverändert zurückgereicht.</b> Siehe
/// <see cref="SupportDraft"/>: Von den über achtzig Feldern verstehen wir drei. Die übrigen
/// entscheiden über Stundensatz und Abrechnung, und sie wegzulassen hiesse, eine Leistung zu
/// buchen, die auf der Rechnung nicht auftaucht.</para>
/// </remarks>
public sealed class SupportRepository : ISupportRepository
{
    private readonly ITanssClient _client;

    /// <summary>Baut das Repository.</summary>
    /// <param name="client">Der HTTP-Zugang.</param>
    public SupportRepository(ITanssClient client)
    {
        ArgumentNullException.ThrowIfNull(client);
        _client = client;
    }

    /// <inheritdoc />
    public async Task<SupportDraft> PrepareFromTimerAsync(int timerId,
                                                          CancellationToken ct = default)
    {
        SupportPropertiesRequest request = new()
        {
            Initializers = [SupportInitializer.Timer(timerId)],
        };

        JsonNode? content = await _client
            .PostAsync<JsonNode>(TanssRoutes.SupportProperties, request, ct: ct)
            .ConfigureAwait(false);

        if (content is null)
        {
            throw new TanssException(string.Create(CultureInfo.CurrentCulture,
                $"TANSS hat zum Timer {timerId} keine Leistung vorbereitet. Üblichste Ursache: "
                + $"Den Timer gibt es nicht mehr oder er gehört einem anderen Techniker."));
        }

        SupportDraft draft = SupportDraft.From(content);

        if (draft.Segments.Count == 0)
        {
            throw new TanssException(string.Create(CultureInfo.CurrentCulture,
                $"Der Timer {timerId} hat keine abgeschlossenen Zeitabschnitte. Ein laufender "
                + $"Timer ist erst anzuhalten; vorher gibt es nichts zu buchen."));
        }

        return draft;
    }

    /// <inheritdoc />
    public async Task<int> CreateAsync(SupportDraft draft, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        JsonNode? created = await _client
            .PostAsync<JsonNode>(TanssRoutes.Supports, draft.Payload, ct: ct)
            .ConfigureAwait(false);

        // Die Kennung ist Beiwerk: Angelegt ist angelegt, auch wenn die Antwort sie nicht
        // nennt. Eine Ausnahme an dieser Stelle liesse den Techniker glauben, es sei nichts
        // passiert - und er buchte ein zweites Mal.
        return created is JsonObject root && root["id"] is { } id && int.TryParse(
            id.ToString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int value)
            ? value
            : 0;
    }
}
