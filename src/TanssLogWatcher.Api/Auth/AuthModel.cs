using System.Text.Json;
using System.Text.Json.Serialization;

namespace TanssLogWatcher.Api.Auth;

/// <summary>
/// Die Ansprüche aus dem Nutzteil eines JWT — gelesen, nicht geprüft.
/// </summary>
/// <remarks>
/// Diese Werte taugen zur Betriebsführung (Ablauf, Restlaufzeit, Anzeige), niemals zu einer
/// Zugriffsentscheidung: der Nutzteil eines JWT ist ohne Signaturprüfung frei erfindbar.
/// Über Zugriff entscheidet ausschließlich TANSS.
/// </remarks>
public sealed record TanssTokenClaims
{
    /// <summary>Baut die Ansprüche aus der bereits gelesenen Abbildung.</summary>
    public TanssTokenClaims(IReadOnlyDictionary<string, JsonElement> values)
    {
        ArgumentNullException.ThrowIfNull(values);
        Values = values;
    }

    /// <summary>Alle Ansprüche, so wie sie im Token stehen.</summary>
    public IReadOnlyDictionary<string, JsonElement> Values { get; }

    /// <summary>Der Ablauf aus <c>exp</c> (Unix-Sekunden); <c>null</c>, wenn nicht vorhanden.</summary>
    /// <remarks>
    /// Hier steht bewusst nicht <see cref="TanssTime.FromUnixSeconds"/>: der deutet die 0 als
    /// „nicht gesetzt“ und gäbe <c>null</c> zurück. Im JWT heißt <c>exp=0</c> aber nicht
    /// „unbefristet“, sondern „seit 1970 abgelaufen“ — und <c>null</c> bedeutet hier laut
    /// <see cref="DaysRemaining"/> unendliche Restlaufzeit. Das Fehlen des Anspruchs
    /// unterscheidet bereits <see cref="TryGetNumber"/>.
    /// </remarks>
    public DateTimeOffset? ExpiresAt =>
        TryGetNumber("exp") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>Der Ausstellungszeitpunkt aus <c>iat</c>; <c>null</c>, wenn nicht vorhanden.</summary>
    /// <remarks>Wie bei <see cref="ExpiresAt"/>: 0 ist ein Zeitpunkt, kein fehlender Wert.</remarks>
    public DateTimeOffset? IssuedAt =>
        TryGetNumber("iat") is { } seconds ? DateTimeOffset.FromUnixTimeSeconds(seconds) : null;

    /// <summary>Der Gegenstand des Tokens aus <c>sub</c>.</summary>
    public string? Subject => TryGetString("sub");

    /// <summary>
    /// Verbleibende Tage bis zum Ablauf. Negativ, wenn das Token abgelaufen ist;
    /// <see cref="double.PositiveInfinity"/>, wenn es keinen Ablauf trägt.
    /// </summary>
    public double DaysRemaining(DateTimeOffset now) =>
        ExpiresAt is { } expiry ? (expiry - now).TotalDays : double.PositiveInfinity;

    /// <summary>Liest einen Anspruch als Zeichenkette.</summary>
    public string? TryGetString(string name) =>
        Values.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>Liest einen Anspruch als ganze Zahl.</summary>
    public long? TryGetNumber(string name) =>
        Values.TryGetValue(name, out JsonElement value) && value.ValueKind == JsonValueKind.Number
        && value.TryGetInt64(out long number)
            ? number
            : null;
}

/// <summary>
/// Was bei einer Erneuerung herausgekommen ist.
/// </summary>
/// <param name="Rotated">Wurde das Token tatsächlich ausgetauscht?</param>
/// <param name="Reason">Warum — in einem Satz, der so in ein Protokoll oder eine Meldung passt.</param>
/// <param name="OldExpiry">Ablauf des bisherigen Tokens.</param>
/// <param name="NewExpiry">Ablauf des neu geprägten Tokens, sofern es überhaupt entstanden ist.</param>
/// <param name="Error">
/// Die geschwärzte Fehlermeldung, falls etwas schiefging. <c>Rotated=false</c> mit
/// <c>Error=null</c> heißt: es war schlicht nichts zu tun.
/// </param>
public sealed record TokenRotationResult(bool Rotated, string Reason, DateTimeOffset? OldExpiry,
                                         DateTimeOffset? NewExpiry, string? Error);

/// <summary>Antwort von <c>GET /api/v1/jwts/tanss_app</c>.</summary>
public sealed record MintedToken
{
    /// <summary>Das Token einschließlich des Präfixes <c>Bearer </c>.</summary>
    [JsonPropertyName("apiToken")] public string ApiToken { get; init; } = string.Empty;
}

/// <summary>Antwort von <c>POST /api/v1/login</c>.</summary>
public sealed record LoginResult
{
    /// <summary>Die Mitarbeiter-ID, die später als <c>loggedInUserId</c> dient.</summary>
    [JsonPropertyName("employeeId")] public int EmployeeId { get; init; }

    /// <summary>Der Sitzungsschlüssel. Kurzlebig; niemals protokollieren.</summary>
    [JsonPropertyName("apiKey")] public string ApiKey { get; init; } = string.Empty;

    /// <summary>Ablauf des Sitzungsschlüssels in Unix-Sekunden.</summary>
    [JsonPropertyName("expire")] public long Expire { get; init; }

    /// <summary>Schlüssel zum Verlängern der Sitzung.</summary>
    [JsonPropertyName("refresh")] public string? Refresh { get; init; }

    /// <summary>Art des Kontos; Techniker und Kundenkonten verhalten sich unterschiedlich.</summary>
    [JsonPropertyName("employeeType")] public string? EmployeeType { get; init; }

    /// <summary>Der Ablauf als Zeitpunkt. Siehe <see cref="TanssTime"/>.</summary>
    public DateTimeOffset? ExpiresAt => TanssTime.FromUnixSeconds(Expire);
}

/// <summary>Rumpf von <c>POST /api/v1/login</c>.</summary>
/// <param name="Username">Anmeldename.</param>
/// <param name="Password">Kennwort.</param>
/// <param name="Token">Zweiter Faktor; leer, wenn die Instanz keinen verlangt.</param>
internal sealed record LoginRequest(
    [property: JsonPropertyName("username")] string Username,
    [property: JsonPropertyName("password")] string Password,
    [property: JsonPropertyName("token")] string Token);
