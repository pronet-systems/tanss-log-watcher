using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;

namespace TanssLogWatcher.Storage.Secrets;

/// <summary>Woher der Schlüssel dieses Aufrufs stammt.</summary>
/// <remarks>
/// Der Unterschied ist für den Techniker sichtbar zu machen: Ein neuer Schlüssel bedeutet,
/// dass die Abdrücke älterer Protokolleinträge nicht mehr zu den neuen passen. Wer das nicht
/// weiß, hält zwei Einträge desselben Fensters für zwei verschiedene Fenster.
/// </remarks>
public enum FingerprintKeyOrigin
{
    /// <summary>Aus der vorhandenen Datei gelesen. Der Normalfall.</summary>
    Loaded,

    /// <summary>Erstmals erzeugt, weil noch keine Datei vorlag.</summary>
    Created,

    /// <summary>Ersetzt, weil die vorhandene Datei unlesbar war.</summary>
    Replaced,
}

/// <summary>
/// Der Schlüssel für <see cref="KeyedFingerprint"/>, DPAPI-versiegelt unter
/// <c>%LOCALAPPDATA%\ProNet Systems\TanssLogWatcher\fingerprint.key</c>.
/// </summary>
/// <remarks>
/// <para>Er liegt neben dem Token und mit denselben Vorkehrungen:
/// <see cref="DataProtectionScope.CurrentUser"/> bindet ihn an das Windows-Anmeldekonto, eine
/// feste zusätzliche Entropie hebt die Hürde von „Datei lesen“ auf „diesen Programmstand
/// kennen“. Das ist genau der Punkt, auf den es ankommt: Wer <c>state.db</c> mitnimmt, nimmt
/// den Schlüssel nicht mit — und ohne ihn lässt sich kein Abdruck nachrechnen.</para>
///
/// <para><b>Erzeugt wird beim ersten Bedarf</b>, nicht bei der Einrichtung. Eine Installation,
/// die nie eine Fensterbeschriftung protokolliert, legt auch keinen Schlüssel an.</para>
///
/// <para><b>Eine unlesbare Datei wird ersetzt, nicht zum Fehler.</b> Der Schlüssel ist kein
/// Geheimnis, das man zurückgewinnen müsste — er ist ein Salz. Geht er verloren, kostet das
/// die Wiedererkennbarkeit der <i>bisherigen</i> Einträge und sonst nichts. Ihn zur
/// Abbruchbedingung zu machen, hieße dagegen, das Änderungsprotokoll selbst aufzugeben, und
/// zwar ausgerechnet dann, wenn auf dem Rechner ohnehin etwas nicht stimmt. Der bisherige
/// Stand wandert vorher nach <c>.bak</c>, damit ein zu früh aufgegebener Schlüssel von Hand
/// zurückgeholt werden kann.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class FingerprintKeyStore
{
    /// <summary>Endung der Sicherungsdatei.</summary>
    public const string BackupSuffix = ".bak";

    /// <summary>
    /// Zusätzliche Entropie. Ändert sich dieser Wert, sind alle bisherigen Schlüsseldateien
    /// unlesbar — er ist Teil des Dateiformats.
    /// </summary>
    private static readonly byte[] Entropy =
        "ProNet Systems/TanssLogWatcher/fingerprint/v1"u8.ToArray();

    /// <summary>Legt den Speicher an einem beliebigen Pfad an.</summary>
    public FingerprintKeyStore(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        Path = System.IO.Path.GetFullPath(path);
        BackupPath = Path + BackupSuffix;
    }

    /// <summary>Legt den Speicher am vorgesehenen Ort im lokalen Profil an.</summary>
    public static FingerprintKeyStore Default() => new(StoragePaths.FingerprintKeyFile);

    /// <summary>
    /// Legt den Speicher neben eine Zustandsdatenbank.
    /// </summary>
    /// <remarks>
    /// Schlüssel und Abdrücke gehören zusammen: Wird die Datenbank an einem eigenen Pfad
    /// geführt — im Test, in einer zweiten Instanz —, muss der Schlüssel mitwandern, sonst
    /// hinge das Protokoll an einem Schlüssel, den eine andere Datenbank benutzt.
    /// </remarks>
    public static FingerprintKeyStore BesideDatabase(string databasePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        string full = System.IO.Path.GetFullPath(databasePath);
        string directory = System.IO.Path.GetDirectoryName(full)
            ?? throw new StorageException(
                $"{databasePath} hat kein Verzeichnis, in dem der Schlüssel für Abdrücke "
                + "liegen könnte. Erwartet wird ein Pfad auf eine Datei, nicht ein "
                + "Laufwerksstamm.");
        return new FingerprintKeyStore(
            System.IO.Path.Combine(directory, StoragePaths.FingerprintKeyFileName));
    }

    /// <summary>Pfad der verschlüsselten Schlüsseldatei.</summary>
    public string Path { get; }

    /// <summary>Pfad der Sicherung des vorherigen Schlüssels.</summary>
    public string BackupPath { get; }

    /// <summary>Liegt bereits ein Schlüssel vor?</summary>
    public bool Exists() => File.Exists(Path);

    /// <summary>Liest den Schlüssel und legt ihn beim ersten Bedarf an.</summary>
    public KeyedFingerprint Load() => Load(out _);

    /// <summary>Liest den Schlüssel und sagt, woher er stammt.</summary>
    /// <param name="origin">
    /// <see cref="FingerprintKeyOrigin.Replaced"/> ist die Angabe, die der Aufrufer anzeigen
    /// sollte: Ab hier passen die Abdrücke nicht mehr zu den bisherigen Einträgen.
    /// </param>
    public KeyedFingerprint Load(out FingerprintKeyOrigin origin)
    {
        if (TryRead(out KeyedFingerprint? existing))
        {
            origin = FingerprintKeyOrigin.Loaded;
            return existing;
        }

        // Erst sichern, dann ueberschreiben - wie beim Token. Ein Schluessel, der nur wegen
        // eines voruebergehenden Lesefehlers verworfen wurde, laesst sich so zurueckholen.
        origin = File.Exists(Path)
            ? FingerprintKeyOrigin.Replaced
            : FingerprintKeyOrigin.Created;

        if (origin == FingerprintKeyOrigin.Replaced)
        {
            _ = AtomicFile.Backup(Path, BackupPath);
        }

        KeyedFingerprint created = KeyedFingerprint.CreateRandom();
        Write(created);
        return created;
    }

    /// <summary>Entfernt Schlüssel und Sicherung.</summary>
    /// <remarks>
    /// Für die Abmeldung, gemeinsam mit dem Token. Danach sind die Abdrücke im bisherigen
    /// Protokoll endgültig nicht mehr zuzuordnen — das ist der Zweck.
    /// </remarks>
    public void Clear()
    {
        TryDelete(Path);
        TryDelete(BackupPath);
    }

    private void Write(KeyedFingerprint fingerprint)
    {
        byte[] plain = fingerprint.ExportKey();
        byte[] cipher;
        try
        {
            cipher = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException exception)
        {
            throw new StorageException(
                "Windows konnte den Schlüssel für Abdrücke nicht verschlüsseln. Das deutet "
                + "auf ein beschädigtes Benutzerprofil hin; eine Neuanmeldung in Windows "
                + "stellt den Schlüsselspeicher meist wieder her. Ursprüngliche Meldung: "
                + exception.Message, exception);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(plain);
        }

        AtomicFile.WriteBytes(Path, cipher);
    }

    private bool TryRead([NotNullWhen(true)] out KeyedFingerprint? fingerprint)
    {
        fingerprint = null;
        if (!File.Exists(Path))
        {
            return false;
        }

        byte[] cipher;
        try
        {
            cipher = File.ReadAllBytes(Path);
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }

        if (cipher.Length == 0)
        {
            return false;
        }

        byte[] plain;
        try
        {
            plain = ProtectedData.Unprotect(cipher, Entropy, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException)
        {
            // Datei eines anderen Benutzers, eines anderen Profils oder beschaedigt. Ein
            // erneuter Versuch aendert daran nichts - hier wird ersetzt, nicht gewartet.
            return false;
        }

        try
        {
            if (plain.Length != KeyedFingerprint.KeySizeBytes)
            {
                return false;
            }

            fingerprint = new KeyedFingerprint(plain);
            return true;
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
