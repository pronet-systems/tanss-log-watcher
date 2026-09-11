using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;

namespace TanssLogWatcher.Storage.Secrets;

/// <summary>
/// Ein Geheimnis im lokalen Profil, an den Windows-Benutzer gebunden.
/// </summary>
/// <remarks>
/// <para><b>Bewusst nicht <see cref="DpapiTokenStore"/>.</b> Jener setzt jedem Wert zwingend
/// <c>Bearer </c> voran, weil TANSS den Kopfzeileneintrag genau so verlangt. Ein Schlüssel für
/// einen Sprachmodell-Anbieter ist kein Bearer-Token und wanderte damit verfälscht über die
/// Leitung — und der Fehler sähe aus wie ein falscher Schlüssel. Hier wird abgelegt, was
/// übergeben wurde, und nichts weiter.</para>
///
/// <para><b>DPAPI mit <see cref="DataProtectionScope.CurrentUser"/>.</b> Der Schlüssel lässt
/// sich damit auf keinem anderen Rechner und unter keinem anderen Windows-Benutzer
/// entschlüsseln — auch nicht von einem Administrator, der die Datei kopiert. Das ist der
/// Grund, aus dem er nicht in <c>config.json</c> steht.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DpapiSecretStore
{
    private readonly byte[] _entropy;

    /// <summary>Öffnet einen Speicher an einem beliebigen Pfad.</summary>
    /// <param name="path">Die Datei, in der das Geheimnis liegt.</param>
    /// <param name="purpose">
    /// Ein Zweckbezeichner, der in die zusätzliche Entropie eingeht. Damit lässt sich ein
    /// Geheimnis nicht als ein anderes entschlüsseln, selbst wenn jemand die Dateien vertauscht.
    /// </param>
    public DpapiSecretStore(string path, string purpose)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(purpose);

        Path = System.IO.Path.GetFullPath(path);
        _entropy = Encoding.UTF8.GetBytes("TanssLogWatcher/" + purpose);
    }

    /// <summary>Der Speicher für den Schlüssel des Sprachmodell-Anbieters.</summary>
    public static DpapiSecretStore ForAi() => new(StoragePaths.AiKeyFile, "ai-provider-key");

    /// <summary>Der Speicher für das Proxy-Kennwort.</summary>
    public static DpapiSecretStore ForProxy() =>
        new(StoragePaths.ProxyPasswordFile, "proxy-password");

    /// <summary>Wo das Geheimnis liegt.</summary>
    public string Path { get; }

    /// <summary>Liegt überhaupt eines vor?</summary>
    public bool Exists() => File.Exists(Path);

    /// <summary>
    /// Liest das Geheimnis.
    /// </summary>
    /// <returns><c>null</c>, wenn keines abgelegt ist oder es sich nicht entsiegeln lässt.</returns>
    /// <remarks>
    /// Wirft nicht. Ein nicht entsiegelbares Geheimnis ist für den Aufrufer dasselbe wie ein
    /// fehlendes — er kann in beiden Fällen nur dasselbe tun: neu eintragen lassen.
    /// </remarks>
    public string? Read()
    {
        if (!Exists())
        {
            return null;
        }

        try
        {
            byte[] plain = ProtectedData.Unprotect(File.ReadAllBytes(Path), _entropy,
                                                   DataProtectionScope.CurrentUser);
            try
            {
                return Encoding.UTF8.GetString(plain);
            }
            finally
            {
                CryptographicOperations.ZeroMemory(plain);
            }
        }
        catch (CryptographicException)
        {
            // Mit einem anderen Windows-Benutzer versiegelt, oder die Datei ist beschaedigt.
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    /// <summary>Legt das Geheimnis ab und überschreibt ein vorhandenes.</summary>
    /// <param name="secret">Das Geheimnis; darf nicht leer sein.</param>
    public void Write(string secret)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(secret);

        byte[] plain = Encoding.UTF8.GetBytes(secret);
        byte[] cipher;

        try
        {
            cipher = ProtectedData.Protect(plain, _entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new TokenStoreException(
                "Windows konnte den Schlüssel nicht verschlüsseln. Das deutet auf ein "
                + "beschädigtes Benutzerprofil hin. Ursprüngliche Meldung: "
                + exception.Message, exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        _ = Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        AtomicFile.WriteBytes(Path, cipher);
    }

    /// <summary>
    /// Entfernt das Geheimnis.
    /// </summary>
    /// <remarks>
    /// Der Weg, eine erteilte Einwilligung auch technisch zurückzunehmen: Ohne Schlüssel kann
    /// nichts mehr gesendet werden, gleich was in der Konfiguration steht.
    /// </remarks>
    /// <returns>
    /// <see langword="true"/>, wenn danach sicher kein Geheimnis mehr liegt.
    /// <para><b>Der Rückgabewert ist kein Beiwerk.</b> Vorher verschluckte diese Methode jeden
    /// Fehler und meldete nichts — der Widerruf der Einwilligung sagte daraufhin „Schlüssel
    /// gelöscht“, obwohl die Datei noch dalag. Ein Widerruf, der nur behauptet wird, ist
    /// schlimmer als gar keiner.</para>
    /// </returns>
    public bool Clear()
    {
        try
        {
            File.Delete(Path);
            return !File.Exists(Path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }
}
