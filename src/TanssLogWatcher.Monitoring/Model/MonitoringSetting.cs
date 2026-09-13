namespace TanssLogWatcher.Monitoring.Model;

/// <summary>
/// Die Entscheidung des Technikers zu einem Profil. Das Einzige, was dauerhaft gespeichert wird.
/// </summary>
/// <remarks>
/// <b>Voreinstellung ist Nichtstun.</b> Solange <see cref="RemoteSupportTypeId"/> auf
/// <see cref="NotMonitored"/> steht, wird die Anwendung nicht beobachtet. Ein Werkzeug, das
/// ungefragt Fenstertitel von Kundenrechnern in ein Ticketsystem schreibt, wäre sonst schon beim
/// ersten Start ein Datenschutzvorfall.
/// </remarks>
public sealed record MonitoringSetting
{
    /// <summary>Der Wert, mit dem ein Profil ausdrücklich <b>nicht</b> überwacht wird.</summary>
    public const int NotMonitored = -1;

    /// <summary>Der Schlüssel des zugehörigen <see cref="MonitoringProfile"/>.</summary>
    public required string Key { get; init; }

    /// <summary>
    /// Die Kennung der TANSS-Fernwartungsanbindung. Muss mindestens 1000 sein und in TANSS
    /// existieren, sonst weist der Server das Anlegen mit <c>TYPE_GREATER_1000</c> beziehungsweise
    /// <c>TYPE_DOESNT_EXIST</c> ab. <see cref="NotMonitored"/> schaltet die Überwachung ab.
    /// </summary>
    public int RemoteSupportTypeId { get; init; } = NotMonitored;

    /// <summary>
    /// Zusätzlich auszuschließende Gegenstellen, mit Semikolon getrennt. Je Eintrag entweder eine
    /// einzelne Adresse oder ein CIDR-Bereich wie <c>10.0.0.0/8</c>.
    /// </summary>
    public string? ExcludeIpAddresses { get; init; }

    /// <summary>
    /// Ersetzt den regulären Ausdruck des Profils. Gedacht für Anwendungen, deren Titelaufbau sich
    /// mit einer Version ändert, bevor der Katalog nachzieht.
    /// </summary>
    public string? RegexPatternOverride { get; init; }

    /// <summary>Wahr, wenn dieses Profil überwacht wird.</summary>
    public bool IsActive => RemoteSupportTypeId != NotMonitored;
}
