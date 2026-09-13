using Anthropic;
using Anthropic.Models.Messages;

namespace TanssLogWatcher.App.Ai;

/// <summary>
/// Claude über das offizielle Anthropic-SDK.
/// </summary>
/// <remarks>
/// <para>Das SDK und nicht HTTP von Hand: Es bringt Wiederholungen, Zeitgrenzen und die
/// typisierten Fehlerklassen mit, und die Form der Anfrage ändert sich mit ihm, ohne dass es
/// hier nachzuziehen wäre.</para>
/// <para><b>Kein Denkmodus, keine Werkzeuge.</b> Für eine Rechtschreibprüfung wäre beides
/// aufwendig ohne besseres Ergebnis — und ein Modell mit Werkzeugen ist ein Modell, das mehr
/// tun kann als das, wofür es hier gerufen wird.</para>
/// </remarks>
public sealed class AnthropicAssistant : IAiAssistant
{
    private readonly AnthropicClient _client;
    private readonly AiPromptSet _prompts;

    /// <summary>Baut den Zugang mit einem Schlüssel.</summary>
    /// <param name="apiKey">Der Schlüssel des Anbieters.</param>
    /// <param name="prompts">
    /// Die Anweisungen an das Modell; ohne Angabe die eingebauten. Sie kommen von aussen,
    /// damit der Betrieb sie bearbeiten kann — die unverhandelbaren Regeln hängt
    /// <see cref="AiPromptSet.System"/> ohnehin an.
    /// </param>
    public AnthropicAssistant(string apiKey, AiPromptSet? prompts = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        _client = new AnthropicClient { ApiKey = apiKey };
        _prompts = prompts ?? AiPromptSet.Default;
    }

    /// <inheritdoc />
    public string ProviderName => "Anthropic (Claude)";

    /// <inheritdoc />
    public async Task<IReadOnlyList<AiModel>> ListModelsAsync(CancellationToken ct = default)
    {
        var page = await _client.Models.List(cancellationToken: ct).ConfigureAwait(false);

        return [.. page.Items
            .Select(m => new AiModel(m.ID, string.IsNullOrWhiteSpace(m.DisplayName)
                ? m.ID
                : m.DisplayName))
            .OrderByDescending(m => m.Id, StringComparer.Ordinal)];
    }

    /// <inheritdoc />
    public async Task<string> ReviseAsync(string text, AiTask task, string model,
                                          CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(text);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);

        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = model,
            MaxTokens = 4096,
            System = _prompts.System,
            Messages =
            [
                new()
                {
                    Role = Role.User,
                    Content = _prompts.For(task) + "\n\n---\n" + text,
                },
            ],
        }, cancellationToken: ct).ConfigureAwait(false);

        string answer = string.Concat(
            response.Content.Select(b => b.Value).OfType<TextBlock>().Select(t => t.Text));

        return answer.Trim();
    }

    /// <inheritdoc />
    public void Dispose() => _client.Dispose();
}
