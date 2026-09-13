using System.Security.Cryptography;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Secrets;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Was der Abdruck einer Fensterbeschriftung zusichert — und was er ausdrücklich nicht mehr
/// preisgibt.
/// </summary>
/// <remarks>
/// Der frühere Abdruck war ein ungesalzener SHA-256. Für ein Token mit hoher Entropie ist
/// das richtig; für eine Fensterbeschriftung war es eine bestätigbare Festlegung: Wer
/// <c>state.db</c> las und eine Rechnerliste hatte, rechnete „RDP: kunde-srv01“ durch und
/// verglich. Hier steht, dass genau dieses Durchrechnen jetzt scheitert.
/// </remarks>
public sealed class FingerprintTests
{
    private const string WindowTitle = "RDP: kunde-srv01";
    private const string KeyFile = "fingerprint.key";

    [Fact]
    public void Der_Abdruck_einer_Beschriftung_laesst_sich_nicht_nachrechnen()
    {
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"));

        _ = log.Append(Entry("sitzung-1", WindowTitle));

        string stored = log.ForSession("sitzung-1")[0].WindowTitle!;

        // Das ist der Angriff: den Kandidaten hashen und mit der Spalte vergleichen.
        Assert.DoesNotContain(SecretFingerprint.Of(WindowTitle), stored, StringComparison.Ordinal);
        Assert.DoesNotContain("kunde-srv01", stored, StringComparison.Ordinal);
    }

    [Fact]
    public void Zwei_Installationen_ergeben_verschiedene_Abdruecke_desselben_Fensters()
    {
        // Der Schluessel entsteht je Installation neu. Damit nuetzt eine anderswo erbeutete
        // Abdrucktabelle nichts - und das ist dieselbe Eigenschaft wie oben, von aussen
        // gesehen.
        using TempDirectory first = new();
        using TempDirectory second = new();
        using SessionLog left = new(first.File("state.db"));
        using SessionLog right = new(second.File("state.db"));

        _ = left.Append(Entry("sitzung-1", WindowTitle));
        _ = right.Append(Entry("sitzung-1", WindowTitle));

        Assert.NotEqual(left.ForSession("sitzung-1")[0].WindowTitle,
                        right.ForSession("sitzung-1")[0].WindowTitle);
    }

    [Fact]
    public void Derselbe_Abdruck_ueberlebt_den_Neustart()
    {
        // Der Zweck des Abdrucks ist die Wiedererkennbarkeit. Ein Schluessel, der bei jedem
        // Start neu entstuende, machte sie zunichte - zwei Eintraege desselben Fensters
        // saehen aus wie zwei Fenster.
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        string before;
        using (SessionLog log = new(path))
        {
            _ = log.Append(Entry("sitzung-1", WindowTitle));
            before = log.ForSession("sitzung-1")[0].WindowTitle!;
        }

        using SessionLog reopened = new(path);
        _ = reopened.Append(Entry("sitzung-2", WindowTitle));

        Assert.Equal(before, reopened.ForSession("sitzung-2")[0].WindowTitle);
    }

    [Fact]
    public void Der_Schluessel_liegt_versiegelt_neben_der_Datenbank()
    {
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"));

        _ = log.Append(Entry("sitzung-1", WindowTitle));

        string keyPath = temp.File(KeyFile);
        Assert.True(File.Exists(keyPath));

        byte[] protectedKey = File.ReadAllBytes(keyPath);
        Assert.NotEmpty(protectedKey);

        // Ohne die zusaetzliche Entropie entschluesselt DPAPI nicht - „Datei lesen“ allein
        // genuegt also nicht, auch nicht im selben Benutzerkontext.
        _ = Assert.Throws<CryptographicException>(
            () => ProtectedData.Unprotect(protectedKey, null, DataProtectionScope.CurrentUser));
    }

    [Fact]
    public void Ohne_Schwaerzung_entsteht_gar_kein_Schluessel()
    {
        // Erzeugt wird beim ersten Bedarf. Wer nicht schwaerzt, braucht keinen Schluessel -
        // und eine Datei, die niemand braucht, ist eine Datei, die jemand erklaeren muss.
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"), timeProvider: null,
                                   redactWindowTitles: false);

        _ = log.Append(Entry("sitzung-1", WindowTitle));

        Assert.False(File.Exists(temp.File(KeyFile)));
    }

    [Fact]
    public void Ohne_Fensterbeschriftung_entsteht_ebenfalls_kein_Schluessel()
    {
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"));

        _ = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-1",
            Operation = "upload",
            Outcome = SessionOutcome.Ok,
            Trigger = SessionTrigger.Watcher,
            Reason = "Angekommen",
        });

        Assert.False(File.Exists(temp.File(KeyFile)));
    }

    [Fact]
    public void Der_Schluesselspeicher_sagt_woher_der_Schluessel_stammt()
    {
        using TempDirectory temp = new();
        FingerprintKeyStore store = new(temp.File(KeyFile));

        Assert.False(store.Exists());

        KeyedFingerprint created = store.Load(out FingerprintKeyOrigin first);
        KeyedFingerprint loaded = store.Load(out FingerprintKeyOrigin second);

        Assert.Equal(FingerprintKeyOrigin.Created, first);
        Assert.Equal(FingerprintKeyOrigin.Loaded, second);
        Assert.Equal(created.Of(WindowTitle), loaded.Of(WindowTitle));
    }

    [Fact]
    public void Ein_unlesbarer_Schluessel_wird_ersetzt_und_vorher_gesichert()
    {
        // Der Schluessel ist ein Salz, kein Geheimnis, das zurueckgewonnen werden muesste.
        // Ihn zur Abbruchbedingung zu machen hiesse, das Aenderungsprotokoll aufzugeben -
        // ausgerechnet dann, wenn auf dem Rechner ohnehin etwas nicht stimmt.
        using TempDirectory temp = new();
        FingerprintKeyStore store = new(temp.File(KeyFile));

        KeyedFingerprint before = store.Load();
        File.WriteAllBytes(store.Path, [1, 2, 3, 4]);

        KeyedFingerprint after = store.Load(out FingerprintKeyOrigin origin);

        Assert.Equal(FingerprintKeyOrigin.Replaced, origin);
        Assert.NotEqual(before.Of(WindowTitle), after.Of(WindowTitle));
        // Der verworfene Stand bleibt greifbar, falls er doch noch gebraucht wird.
        Assert.True(File.Exists(store.BackupPath));
        Assert.Equal(FingerprintKeyOrigin.Loaded, Origin(store));
    }

    [Fact]
    public void Ein_geloeschter_Schluessel_haelt_das_Protokoll_nicht_an()
    {
        using TempDirectory temp = new();
        FingerprintKeyStore store = new(temp.File(KeyFile));

        _ = store.Load();
        store.Clear();

        Assert.False(store.Exists());
        Assert.Equal(FingerprintKeyOrigin.Created, Origin(store));
    }

    [Fact]
    public void Ein_Schluessel_falscher_Laenge_nennt_den_Ausweg()
    {
        StorageException error = Assert.Throws<StorageException>(
            () => new KeyedFingerprint(new byte[8]));

        Assert.Contains("8", error.Message, StringComparison.Ordinal);
        Assert.Contains("gelöscht", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Der_Abdruck_unterscheidet_und_meldet_Leeres()
    {
        KeyedFingerprint one = KeyedFingerprint.CreateRandom();
        KeyedFingerprint other = KeyedFingerprint.CreateRandom();

        Assert.Equal(one.Of(WindowTitle), one.Of(WindowTitle));
        Assert.NotEqual(one.Of(WindowTitle), one.Of("RDP: kunde-srv02"));
        Assert.NotEqual(one.Of(WindowTitle), other.Of(WindowTitle));
        Assert.Equal(8, one.Of(WindowTitle).Length);
        Assert.Equal("(leer)", one.Of(null));
        Assert.Equal("(leer)", one.Of(string.Empty));
    }

    /// <summary>
    /// Der Schalter steht in der Konfiguration und wirkt im Protokoll — in beide Richtungen.
    /// </summary>
    /// <remarks>
    /// Die Verdrahtung ist der Punkt, nicht die Vorgabe: Eine Einstellung, die geschrieben und
    /// gelesen, aber nirgends ausgewertet wird, ist die gefährlichste Sorte.
    /// </remarks>
    [Fact]
    public void Die_Einstellung_kommt_ueber_die_Fabrikmethode_herein()
    {
        using TempDirectory temp = new();
        using StateDatabase database = new(temp.File("state.db"));

        // Beide Seiten ausdruecklich gesetzt und keine aus der Vorgabe geholt: Sonst prueft
        // dieser Fall nach einem Wechsel der Vorgabe nur noch eine Richtung - und zwar
        // stillschweigend.
        AppConfig redacting = Sample.Config() with
        {
            Logging = new LoggingSection { RedactWindowTitles = true },
        };
        AppConfig plain = redacting with
        {
            Logging = new LoggingSection { RedactWindowTitles = false },
        };

        using SessionLog loud = SessionLog.FromConfig(plain, database);
        using SessionLog quiet = SessionLog.FromConfig(redacting, database);

        Assert.False(loud.RedactsWindowTitles);
        Assert.True(quiet.RedactsWindowTitles);
        Assert.Equal(TimeSpan.FromDays(redacting.Logging.RetentionDays),
                     SessionLog.RetentionOf(redacting));
    }

    private static FingerprintKeyOrigin Origin(FingerprintKeyStore store)
    {
        _ = store.Load(out FingerprintKeyOrigin origin);
        return origin;
    }

    private static SessionLogEntry Entry(string session, string title) => new()
    {
        RemoteMaintenanceId = session,
        Operation = "session.opened",
        Outcome = SessionOutcome.Ok,
        Trigger = SessionTrigger.Watcher,
        Reason = "Fenster erkannt",
        WindowTitle = title,
    };
}
