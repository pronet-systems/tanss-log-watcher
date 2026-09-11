using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using TanssLogWatcher.Api.Contract;

namespace TanssLogWatcher.Storage.Secrets;

/// <summary>
/// Das TANSS-Arbeitstoken, DPAPI-verschlüsselt unter
/// <c>%LOCALAPPDATA%\ProNet Systems\TanssLogWatcher\credentials.dat</c>.
/// </summary>
/// <remarks>
/// <para>Verschlüsselt wird mit <see cref="DataProtectionScope.CurrentUser"/>. Der Schlüssel
/// hängt damit am Windows-Anmeldekonto: Ein anderer Benutzer desselben Rechners kann die
/// Datei lesen, aber nicht entschlüsseln. Das ist der eigentliche Zweck — ein Tray-Werkzeug
/// läuft im Benutzerkontext, und sein Token ist ein Ausweis auf die TANSS-Instanz des Kunden.</para>
///
/// <para><b>Warum eine feste zusätzliche Entropie.</b> Ohne sie genügt es, die Datei zu lesen
/// und <c>Unprotect</c> aufzurufen — jedes beliebige Programm im selben Benutzerkontext kann
/// das, ganz ohne erhöhte Rechte. Die Entropie zwingt dazu, auch diesen Wert zu kennen. Sie
/// steht bewusst als Konstante im Quelltext und wird nicht zufällig erzeugt: Ein zufälliger
/// Wert müsste neben dem Geheimtext liegen, wo ihn genau derselbe Angreifer fände, und
/// müsste jeden Programmstand überleben. Die Entropie ist kein Kennwort und ersetzt DPAPI
/// nicht — sie hebt die Hürde von „Datei lesen“ auf „diesen Programmstand kennen“.</para>
///
/// <para><b>Vor jedem Schreiben entsteht eine <c>.bak</c>.</b> Der gefährliche Moment ist die
/// Token-Erneuerung: Wäre der neue Wert unbrauchbar und der alte bereits überschrieben, hätte
/// der Techniker gar kein Token mehr und müsste sich neu anmelden — womöglich an einem Tag,
/// an dem die Instanz nicht erreichbar ist.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiTokenStore : ITokenStore
{
    /// <summary>Das Präfix, das der Kopfzeilenwert <c>apiToken</c> tragen muss.</summary>
    public const string BearerPrefix = "Bearer ";

    /// <summary>Endung der Sicherungsdatei.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Zusätzliche Entropie. Ändert sich dieser Wert, sind alle bisherigen Dateien
    /// unlesbar — er ist Teil des Dateiformats und wird nur mit einer Überführung angefasst.
    /// </summary>
    private static readonly byte[] Entropy =
        "ProNet Systems/TanssLogWatcher/credentials/v1"u8.ToArray();

    /// <summary>Legt den Speicher an einem beliebigen Pfad an.</summary>
    public DpapiTokenStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        BackupPath = Path + BackupSuffix;
    }

    /// <summary>Legt den Speicher am vorgesehenen Ort im lokalen Profil an.</summary>
    public static DpapiTokenStore Default() => new(StoragePaths.CredentialsFile);

    /// <summary>Pfad der verschlüsselten Datei.</summary>
    public string Path { get; }

    /// <summary>Pfad der Sicherung des vorherigen Standes.</summary>
    public string BackupPath { get; }

    /// <summary>Liegt bereits ein Token vor?</summary>
    public bool Exists() => File.Exists(Path);

    /// <inheritdoc/>
    public string Read() => ReadFrom(Path, isBackup: false);

    /// <summary>Liest die Sicherung des vorherigen Standes.</summary>
    /// <remarks>
    /// Für den Fall, dass eine Erneuerung ein unbrauchbares Token hinterlassen hat. Der
    /// Aufrufer entscheidet, ob er damit weiterarbeitet oder über
    /// <see cref="RestoreFromBackup"/> zurückgeht.
    /// </remarks>
    public string ReadBackup() => ReadFrom(BackupPath, isBackup: true);

    /// <inheritdoc/>
    public void Write(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);

        string normalized = Normalize(token);
        byte[] plain = Encoding.UTF8.GetBytes(normalized);

        byte[] cipher;
        try
        {
            cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new TokenStoreException(
                "Windows konnte das Token nicht verschlüsseln. Das deutet auf ein beschädigtes "
                + "Benutzerprofil hin; eine Neuanmeldung in Windows stellt den Schlüsselspeicher "
                + $"meist wieder her. Ursprüngliche Meldung: {exception.Message}", exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        // Erst sichern, dann ueberschreiben. Andersherum waere der Zeitraum, in dem gar kein
        // brauchbares Token existiert, genau der Zeitraum des Schreibens.
        AtomicFile.Backup(Path, BackupPath);
        AtomicFile.WriteBytes(Path, cipher);
    }

    /// <summary>Holt den vorherigen Stand zurück.</summary>
    /// <returns><c>false</c>, wenn keine Sicherung vorliegt.</returns>
    public bool RestoreFromBackup()
    {
        if (!File.Exists(BackupPath))
        {
            return false;
        }

        AtomicFile.WriteBytes(Path, File.ReadAllBytes(BackupPath));
        return true;
    }

    /// <summary>Entfernt Token und Sicherung. Für die Abmeldung.</summary>
    public void Clear()
    {
        TryDelete(Path);
        TryDelete(BackupPath);
    }

    /// <summary>
    /// Stellt das Präfix <c>Bearer </c> sicher.
    /// </summary>
    /// <remarks>
    /// TANSS liefert unter <c>/api/v1/jwts/tanss_app</c> ein <c>apiToken</c>, das das Präfix
    /// bereits trägt; die Anmeldung unter <c>/api/v1/login</c> liefert unter <c>apiKey</c>
    /// dagegen den nackten Wert. Beides landet hier. Ohne Präfix antwortet TANSS auf jede
    /// Anfrage mit 403 — ununterscheidbar von einem abgelaufenen Token, und deshalb ein
    /// Fehler, der stundenlang in die falsche Richtung führt. Die Vereinheitlichung geschieht
    /// an dieser einen Stelle, weil sie sonst an jeder Aufrufstelle vergessen werden kann.
    /// </remarks>
    public static string Normalize(string token)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(token);
        string trimmed = token.Trim();
        return trimmed.StartsWith(BearerPrefix, StringComparison.OrdinalIgnoreCase)
            ? BearerPrefix + trimmed[BearerPrefix.Length..].Trim()
            : BearerPrefix + trimmed;
    }

    private string ReadFrom(string path, bool isBackup)
    {
        if (!File.Exists(path))
        {
            throw new TokenStoreException(isBackup
                ? $"Es liegt keine Sicherung unter {path}. Eine entsteht erst, wenn das "
                  + "Token mindestens einmal erneuert wurde."
                : $"Es ist noch kein TANSS-Token hinterlegt (erwartet unter {path}). "
                  + "Das Werkzeug ist auf diesem Rechner noch nicht eingerichtet. "
                  + "Bitte einmalig ausführen: tanss-logwatch setup");
        }

        byte[] cipher = File.ReadAllBytes(path);
        if (cipher.Length == 0)
        {
            throw new TokenStoreException(
                $"{path} ist leer. Das Token ist verloren. "
                + (isBackup ? string.Empty : $"Eine Sicherung läge unter {Path + BackupSuffix}. ")
                + "Andernfalls neu einrichten mit: tanss-logwatch setup");
        }

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new TokenStoreException(
                $"{path} lässt sich nicht entschlüsseln. DPAPI bindet das Token an das "
                + "Windows-Anmeldekonto: Die Datei stammt von einem anderen Benutzer oder "
                + "aus einem anderen Profil, oder sie wurde beschädigt. Ein erneuter Versuch "
                + "ändert daran nichts. Bitte neu einrichten mit: tanss-logwatch setup",
                exception);
        }

        try
        {
            string token = Encoding.UTF8.GetString(plain).Trim();
            return token.Length == 0
                ? throw new TokenStoreException(
                    $"{path} enthält ein leeres Token. Bitte neu einrichten mit: tanss-logwatch setup")
                : token;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
