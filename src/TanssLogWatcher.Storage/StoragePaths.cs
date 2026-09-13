using System.Runtime.Versioning;

namespace TanssLogWatcher.Storage;

/// <summary>
/// Die festen Ablageorte des Werkzeugs auf dem Rechner des Technikers.
/// </summary>
/// <remarks>
/// <para>Die Trennung ist beabsichtigt: Die Konfiguration liegt im <b>roaming</b>-Profil
/// (<c>%APPDATA%</c>), weil sie den Benutzer auf einen anderen Rechner begleiten darf.
/// Token und Zustandsdatenbank liegen im <b>lokalen</b> Profil (<c>%LOCALAPPDATA%</c>):
/// Das DPAPI-Geheimnis ist an Benutzer <i>und</i> Rechner gebunden und würde mitgereist
/// nur eine unentschlüsselbare Datei ergeben, die Zustandsdatenbank wiederum beschreibt
/// Vorgänge genau dieses Rechners.</para>
///
/// <para><b>Bewusst kein Verzeichnis je Version.</b> Ein eigenes Verzeichnis je
/// Programmstand unterhalb des Produktordners hinterlässt nach vier Versionen vier
/// verwaiste Ordner, von denen keiner erkennbar der gültige ist. Hier gibt es genau einen
/// Pfad je Datei, über alle Programmstände hinweg.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class StoragePaths
{
    /// <summary>Herstellerordner. Teil des Pfads, nicht des Dateinamens.</summary>
    public const string VendorFolder = "ProNet Systems";

    /// <summary>Produktordner.</summary>
    public const string ProductFolder = "TanssLogWatcher";

    /// <summary>Dateiname der Konfiguration.</summary>
    public const string ConfigFileName = "config.json";

    /// <summary>Dateiname des verschlüsselten Tokens.</summary>
    public const string CredentialsFileName = "credentials.dat";

    /// <summary>Dateiname der Zustandsdatenbank.</summary>
    public const string StateDatabaseFileName = "state.db";

    /// <summary>
    /// Dateiname des verschlüsselten Schlüssels für die Abdrücke von Fensterbeschriftungen.
    /// </summary>
    /// <remarks>
    /// Liegt neben Token und Zustandsdatenbank im lokalen Profil und nicht etwa in der
    /// Konfiguration: Wanderte er mit dem roaming-Profil, läge der Schlüssel neben den
    /// Abdrücken, die er schützen soll, sobald jemand beide Profile in die Hand bekommt.
    /// </remarks>
    public const string FingerprintKeyFileName = "fingerprint.key";

    /// <summary>Dateiname des Anbieterschlüssels für die Sprachmodell-Unterstützung.</summary>
    public const string AiKeyFileName = "ai.dat";

    /// <summary>Dateiname des Proxy-Kennworts.</summary>
    public const string ProxyPasswordFileName = "proxy.dat";

    /// <summary><c>%APPDATA%\ProNet Systems\TanssLogWatcher</c>.</summary>
    public static string ConfigDirectory =>
        Combine(Environment.SpecialFolder.ApplicationData);

    /// <summary><c>%LOCALAPPDATA%\ProNet Systems\TanssLogWatcher</c>.</summary>
    public static string StateDirectory =>
        Combine(Environment.SpecialFolder.LocalApplicationData);

    /// <summary>Vollständiger Pfad der Konfigurationsdatei.</summary>
    public static string ConfigFile => Path.Combine(ConfigDirectory, ConfigFileName);

    /// <summary>Vollständiger Pfad der Token-Datei.</summary>
    public static string CredentialsFile => Path.Combine(StateDirectory, CredentialsFileName);

    /// <summary>Vollständiger Pfad der Zustandsdatenbank.</summary>
    public static string StateDatabaseFile => Path.Combine(StateDirectory, StateDatabaseFileName);

    /// <summary>Vollständiger Pfad des Abdruck-Schlüssels.</summary>
    public static string FingerprintKeyFile => Path.Combine(StateDirectory, FingerprintKeyFileName);

    /// <summary>
    /// Der Schlüssel für den Sprachmodell-Anbieter, DPAPI-verschlüsselt.
    /// </summary>
    /// <remarks>
    /// Getrennt von <see cref="CredentialsFile"/>, obwohl beides Geheimnisse sind: Das eine ist
    /// der Zugang zur Instanz des Kunden, das andere der zu einem fremden Anbieter. Wer den
    /// einen zurückzieht, will selten den anderen mit zurückziehen — und zwei Dateien lassen
    /// sich einzeln löschen.
    /// </remarks>
    public static string AiKeyFile => Path.Combine(StateDirectory, AiKeyFileName);

    /// <summary>
    /// Das Kennwort für den Proxy, DPAPI-verschlüsselt.
    /// </summary>
    /// <remarks>
    /// In <c>config.json</c> steht dafür nur <c>proxy.password_ref</c> — ein Verweis. Das
    /// Geheimnis selbst liegt hier, an den Windows-Benutzer gebunden, und geht damit auch beim
    /// Weitergeben der Konfigurationsdatei nicht mit.
    /// </remarks>
    public static string ProxyPasswordFile => Path.Combine(StateDirectory, ProxyPasswordFileName);

    private static string Combine(Environment.SpecialFolder root) =>
        Path.Combine(Environment.GetFolderPath(root, Environment.SpecialFolderOption.Create),
                     VendorFolder, ProductFolder);
}
