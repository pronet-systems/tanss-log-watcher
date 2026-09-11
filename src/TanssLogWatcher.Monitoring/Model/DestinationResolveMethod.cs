namespace TanssLogWatcher.Monitoring.Model;

/// <summary>
/// Auf welchem Weg das Ziel einer Sitzung bestimmt wird.
/// </summary>
/// <remarks>
/// Die Reihenfolge ist die der Vorlage und damit Teil der gespeicherten Einstellungen:
/// <c>WindowTitleRegex = 0</c>, <c>WindowTitle = 1</c>, <c>RemoteHostIpConnection = 2</c>.
/// Ein Umsortieren würde bestehende Konfigurationen still auf ein anderes Verfahren umstellen.
/// </remarks>
public enum DestinationResolveMethod
{
    /// <summary>Aus dem Fenstertitel, über einen regulären Ausdruck.</summary>
    WindowTitleRegex = 0,

    /// <summary>Der Fenstertitel selbst.</summary>
    WindowTitle = 1,

    /// <summary>Aus den ausgehenden TCP-Verbindungen des Prozesses und seiner Kinder.</summary>
    RemoteHostIpConnection = 2,
}
