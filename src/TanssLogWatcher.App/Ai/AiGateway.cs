using System.Runtime.Versioning;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.App.Ai;

/// <summary>
/// Das Tor zur Sprachmodell-Unterstützung.
/// </summary>
/// <remarks>
/// <para><b>Es gibt genau eine Stelle, an der entschieden wird, ob Text das Haus verlässt, und
/// das ist diese.</b> Kein Ansichtsmodell baut sich selbst einen Zugang; wer senden will, holt
/// ihn hier und bekommt <c>null</c>, solange eine der Bedingungen fehlt. Eine zweite Stelle mit
/// derselben Prüfung wäre eine Stelle, an der sie eines Tages vergessen wird — und der Fehler
/// fiele niemandem auf, weil er nur dazu führt, dass etwas funktioniert.</para>
///
/// <para>Die Bedingungen: eingeschaltet, Einwilligung erteilt, Modell gewählt, Schlüssel
/// vorhanden. Der Schlüssel steht bewusst zuletzt — er liegt nicht in der Konfiguration,
/// sondern DPAPI-versiegelt im Profil, und sein Fehlen ist der Fall, den ein kopierter
/// Konfigurationsstand erzeugt.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class AiGateway
{
    /// <summary>Die unterstützten Anbieter, wie sie in der Konfiguration stehen.</summary>
    public const string ProviderAnthropic = "anthropic";

    /// <summary>Der zweite unterstützte Anbieter.</summary>
    public const string ProviderOpenAi = "openai";

    /// <summary>
    /// Baut einen Zugang, wenn alle Bedingungen erfüllt sind.
    /// </summary>
    /// <param name="config">Die Konfiguration, oder <c>null</c>, wenn nichts eingerichtet ist.</param>
    /// <param name="reason">
    /// Der Klartextgrund, wenn kein Zugang entsteht. Er wird angezeigt — deshalb sagt er, was
    /// fehlt, und nicht bloss, dass etwas fehlt.
    /// </param>
    /// <returns>Der Zugang, oder <c>null</c>.</returns>
    public static IAiAssistant? TryCreate(AppConfig? config, out string reason)
    {
        if (config is null)
        {
            reason = "Ohne Einrichtung steht die Unterstützung nicht zur Verfügung.";
            return null;
        }

        AiSection ai = config.Ai;

        if (!ai.Enabled)
        {
            reason = "Die Sprachmodell-Unterstützung ist abgeschaltet. Einzuschalten ist sie "
                + "unter „Verbindung“ — dort steht auch, was dabei übermittelt wird.";
            return null;
        }

        if (!ai.HasConsent)
        {
            reason = "Es liegt keine Einwilligung vor. Ohne sie wird nichts übermittelt.";
            return null;
        }

        if (string.IsNullOrWhiteSpace(ai.Model))
        {
            reason = "Es ist kein Modell gewählt. Die Liste lädt die Auswahl unter „Verbindung“.";
            return null;
        }

        string? key = DpapiSecretStore.ForAi().Read();

        if (string.IsNullOrWhiteSpace(key))
        {
            reason = "Es liegt kein Schlüssel für den Anbieter vor — oder er wurde unter einem "
                + "anderen Windows-Benutzer abgelegt und lässt sich hier nicht entsiegeln.";
            return null;
        }

        reason = string.Empty;

        return Create(ai.Provider, key);
    }

    /// <summary>
    /// Baut einen Zugang allein aus Anbieter und Schlüssel — ohne die Einwilligungsprüfung.
    /// </summary>
    /// <remarks>
    /// <b>Nur für die Einrichtung.</b> Die Modellliste muss abrufbar sein, bevor ein Modell
    /// gewählt werden kann, und gewählt werden muss es, bevor die Einwilligung überhaupt
    /// sinnvoll erteilt wird. Übermittelt wird dabei nichts als der Schlüssel selbst — die
    /// Modellliste ist ein Lesevorgang ohne Inhalt.
    /// </remarks>
    /// <param name="provider">Der Anbieter.</param>
    /// <param name="apiKey">Der Schlüssel.</param>
    public static IAiAssistant Create(string provider, string apiKey) =>
        provider?.Trim().ToLowerInvariant() switch
        {
            ProviderOpenAi => new OpenAiAssistant(apiKey),
            _ => new AnthropicAssistant(apiKey),
        };

    /// <summary>
    /// Baut die Schwärzung zu einer Sitzung.
    /// </summary>
    /// <remarks>
    /// Aufgenommen wird, was das Werkzeug sicher als Namen kennt: die Gegenstelle, der eigene
    /// Rechnername und der Anmeldename. Alles andere im Text steht dort, weil ein Mensch es
    /// hineingeschrieben hat — und was ein Mensch schreibt, kann dieses Stück nicht erraten.
    /// </remarks>
    /// <param name="destination">Die Gegenstelle der Sitzung.</param>
    /// <param name="active">Ist die Schwärzung überhaupt eingeschaltet?</param>
    public static AiRedaction BuildRedaction(string? destination, bool active)
    {
        AiRedaction redaction = new();

        if (!active)
        {
            return redaction;
        }

        redaction.Add(destination, "ZIEL");
        redaction.Add(Environment.MachineName, "RECHNER");
        redaction.Add(Environment.UserName, "BENUTZER");

        return redaction;
    }
}
