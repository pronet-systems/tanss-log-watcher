namespace TanssLogWatcher.Monitoring;

/// <summary>
/// Eine Einstellung ist unbrauchbar, etwa ein regulärer Ausdruck, den .NET nicht übersetzen kann.
/// </summary>
/// <remarks>
/// <para><b>So reagiert der Aufrufer:</b> den betroffenen Prozess in diesem Durchlauf überspringen,
/// den Durchlauf selbst aber zu Ende führen, und den Fehler <b>einmal</b> melden — er besteht
/// unverändert fort, bis jemand die Einstellung korrigiert. Ein Wiederholen hilft nicht.</para>
/// <para><b>Warum kein Rückfall auf den Platzhalter:</b> eine Sitzung ohne erkennbares Ziel würde
/// als inhaltsleere Fernwartung im TANSS des Kunden landen — und zwar für jedes Fenster der
/// Anwendung. Eine falsche Einstellung darf nichts erfinden, sie muss auffallen.</para>
/// </remarks>
public sealed class MonitoringConfigurationException : Exception
{
    /// <summary>Erzeugt die Ausnahme ohne Angaben.</summary>
    public MonitoringConfigurationException()
        : this(string.Empty, "Eine Überwachungseinstellung ist unbrauchbar.")
    {
    }

    /// <summary>Erzeugt die Ausnahme mit einem Text.</summary>
    /// <param name="message">Der Fehlertext.</param>
    public MonitoringConfigurationException(string message)
        : this(string.Empty, message)
    {
    }

    /// <summary>Erzeugt die Ausnahme mit einem Text und einer Ursache.</summary>
    /// <param name="message">Der Fehlertext.</param>
    /// <param name="innerException">Die zugrunde liegende Ausnahme.</param>
    public MonitoringConfigurationException(string message, Exception? innerException)
        : base(message, innerException) => ProfileKey = string.Empty;

    /// <summary>Erzeugt die Ausnahme zu einem bestimmten Profil.</summary>
    /// <param name="profileKey">Der Schlüssel des betroffenen Profils.</param>
    /// <param name="message">Der Fehlertext.</param>
    /// <param name="innerException">Die zugrunde liegende Ausnahme, sofern es eine gibt.</param>
    public MonitoringConfigurationException(string profileKey, string message,
                                            Exception? innerException = null)
        : base(message, innerException) => ProfileKey = profileKey;

    /// <summary>
    /// Das betroffene Profil. Leer, wenn die Ausnahme ohne Profilbezug erzeugt wurde.
    /// </summary>
    /// <remarks>
    /// Der Schlüssel und nicht der Ausdruck selbst: er benennt die Stelle in den Einstellungen,
    /// die zu berichtigen ist, ohne den beanstandeten Text weiterzureichen.
    /// </remarks>
    public string ProfileKey { get; }
}
