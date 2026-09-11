namespace TanssLogWatcher.Api;

/// <summary>Alles, was der Netzzugriff über die Instanz wissen muss.</summary>
public sealed record TanssOptions
{
    /// <summary>
    /// Basisadresse einschließlich <c>/backend</c>, ohne Schrägstrich am Ende.
    /// Zeigt die Adresse auf die Weboberfläche statt auf <c>/backend</c>, antwortet die
    /// PHP-Oberfläche auf jede Anfrage mit HTTP 400 — auch ohne Token.
    /// </summary>
    public required string BaseUrl { get; init; }

    /// <summary>Mitarbeiter-ID des Technikers. Trägt die gesamte Attribution der Fernwartungen.</summary>
    public required int EmployeeId { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);

    public bool VerifyTls { get; init; } = true;

    public ProxyOptions? Proxy { get; init; }
}

/// <summary>Vorgeschalteter Proxy, wie ihn das Original ebenfalls kannte.</summary>
public sealed record ProxyOptions
{
    public required string Address { get; init; }
    public int Port { get; init; } = 8080;
    public string? User { get; init; }
    public string? Password { get; init; }
}
