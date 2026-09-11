using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanssLogWatcher.App.Ai;

/// <summary>
/// OpenAI über die Chat-Completions-Schnittstelle.
/// </summary>
/// <remarks>
/// <para><b>Über HTTP und nicht über ein SDK.</b> Gebraucht werden genau zwei Routen —
/// Modellliste und eine Vervollständigung. Dafür ein weiteres Paket samt seiner Abhängigkeiten
/// in ein Werkzeug zu ziehen, das beim Kunden installiert wird, steht in keinem Verhältnis. Bei
/// Claude liegt es anders: Dort ist das offizielle SDK der vorgesehene Weg, und der Aufwand
/// entfällt damit ohnehin.</para>
/// <para>Die Adresse ist einstellbar, damit auch ein kompatibler Dienst im eigenen Haus
/// verwendet werden kann — der Weg, diese Funktion zu nutzen, ohne dass Daten hinausgehen.</para>
/// </remarks>
public sealed class OpenAiAssistant : IAiAssistant
{
    /// <summary>Die vorgesehene Adresse der Schnittstelle.</summary>
    public const string DefaultBaseUrl = "https://api.openai.com/v1";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;

    /// <summary>Baut den Zugang mit einem Schlüssel.</summary>
    /// <param name="apiKey">Der Schlüssel des Anbieters.</param>
    /// <param name="baseUrl">Die Adresse; ohne Angabe die von OpenAI.</param>
    public OpenAiAssistant(string apiKey, string? baseUrl = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);

        _http = new HttpClient
        {
            BaseAddress = new Uri((baseUrl ?? DefaultBaseUrl).TrimEnd('/') + "/"),
            Timeout = TimeSpan.FromMinutes(2),
        };

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
    }

    /// <inheritdoc />
    public string ProviderName => "OpenAI";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct = default)
    {
        ModelList? list = await _http.GetFromJsonAsync<ModelList>("models", Json, ct)
            .ConfigureAwait(false);

        if (list is null)
        {
            return [];
        }

        // Die Liste enthaelt auch Modelle fuer Bilder, Sprache und Einbettungen. Sie hier
        // anzubieten hiesse, dem Techniker eine Auswahl vorzulegen, von der die Haelfte beim
        // ersten Versuch scheitert.
        return [.. list.Data
            .Where(m => IsTextModel(m.Id))
            .Select(m => new AiModel(m.Id, m.Id))
            .OrderByDescending(m => m.Id, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<string> ReviseAsync(string text, AiTask task, string model,
                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        ChatRequest request = new()
        {
            Model = model,
            Messages =
            [
                new ChatMessage { Role = "system", Content = AiPrompts.System },
                new ChatMessage { Role = "user", Content = AiPrompts.For(task) + "\n\n---\n" + text },
            ],
        };

        using HttpResponseMessage response = await _http
            .PostAsJsonAsync("chat/completions", request, Json, ct).ConfigureAwait(false);

        if (!response.IsSuccessStatusCode)
        {
            string body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            throw new HttpRequestException(
                $"OpenAI hat mit {(int)response.StatusCode} geantwortet: {Shorten(body)}",
                null, response.StatusCode);
        }

        ChatResponse? answer = await response.Content
            .ReadFromJsonAsync<ChatResponse>(Json, ct).ConfigureAwait(false);

        string content = answer is { Choices.Count: > 0 }
            ? answer.Choices[0].Message?.Content ?? string.Empty
            : string.Empty;

        return content.Trim();
    }

    /// <inheritdoc />
    public void Dispose() => _http.Dispose();

    /// <summary>Taugt dieses Modell für Text?</summary>
    /// <remarks>
    /// Über eine Ausschlussliste und nicht über eine Aufzählung der erlaubten: Eine Aufzählung
    /// wäre an dem Tag unvollständig, an dem der Anbieter ein neues Modell veröffentlicht — und
    /// genau das soll diese Auswahl ja anbieten.
    /// </remarks>
    private static bool IsTextModel(string id) =>
        !id.Contains("embedding", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("whisper", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("tts", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("audio", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("dall-e", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("image", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("moderation", StringComparison.OrdinalIgnoreCase)
        && !id.Contains("realtime", StringComparison.OrdinalIgnoreCase);

    private static string Shorten(string text) =>
        text.Length <= 300 ? text : text[..300] + "…";

    private sealed record ModelList
    {
        [JsonPropertyName("data")] public IReadOnlyList<ModelEntry> Data { get; init; } = [];
    }

    private sealed record ModelEntry
    {
        [JsonPropertyName("id")] public string Id { get; init; } = string.Empty;
    }

    private sealed record ChatRequest
    {
        [JsonPropertyName("model")] public string Model { get; init; } = string.Empty;

        [JsonPropertyName("messages")] public IReadOnlyList<ChatMessage> Messages { get; init; } = [];
    }

    private sealed record ChatMessage
    {
        [JsonPropertyName("role")] public string Role { get; init; } = string.Empty;

        [JsonPropertyName("content")] public string Content { get; init; } = string.Empty;
    }

    private sealed record ChatResponse
    {
        [JsonPropertyName("choices")] public IReadOnlyList<ChatChoice> Choices { get; init; } = [];
    }

    private sealed record ChatChoice
    {
        [JsonPropertyName("message")] public ChatMessage? Message { get; init; }
    }
}
