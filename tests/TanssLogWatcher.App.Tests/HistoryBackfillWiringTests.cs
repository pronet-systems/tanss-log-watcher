using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Storage;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.History;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Der einmalige Nachtrag des Verlaufs — und die Frage, ob ihn überhaupt jemand ruft.
/// </summary>
/// <remarks>
/// <para><b>Geprüft wird die Verdrahtung, nicht der Nachtrag selbst.</b> Was der Nachtrag aus
/// Warteschlange und Protokoll herausliest, steht im Ablageprojekt unter Prüfung. Hier geht es
/// um die eine Frage, die dort niemand beantworten kann: Läuft er im fertigen Programm an?
/// Eine Klasse, die alles richtig macht und die nie gerufen wird, ist von einer fehlenden
/// nicht zu unterscheiden — und genau das war sie eine Zeit lang.</para>
///
/// <para>Echt ist alles bis auf zwei Orte: Konfiguration und Zustandsdatenbank liegen in einem
/// Wegwerfordner statt im Benutzerprofil. Zusammenbau, Warteschlange, Protokoll und Verlauf
/// sind dieselben wie im Betrieb.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class HistoryBackfillWiringTests
{
    /// <summary>
    /// Eine Datei vom vorigen Stand: Der erste Zugriff auf den Verlauf trägt nach und
    /// hinterlässt den Eintrag, der die Herkunft erklärt.
    /// </summary>
    /// <remarks>
    /// Der Eintrag ist kein Beiwerk. Die nachgetragenen Zeilen tragen ein geschätztes Ende und
    /// unvollständige Angaben; ohne den Satz im Protokoll stünde die Seite voller Zeilen, deren
    /// Herkunft niemand mehr feststellen kann.
    /// </remarks>
    [Fact]
    public void Der_erste_Zugriff_auf_den_Verlauf_traegt_nach()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");
        string id = Guid.NewGuid().ToString();

        // Der Stand VOR dem Verlauf: eine Warteschlangenzeile, aber keine Tabelle dafuer.
        using (StateDatabase before = new(path))
        {
            using UploadQueue queue = new(before);
            Assert.True(queue.Enqueue(Recent(id)));
        }

        Downgrade(path);

        using AppHost host = Host(temp, path);
        RuntimeComposition composition = Assert.IsType<RuntimeComposition>(host.Composition);

        // DIES ist der Zugriff, der den Nachtrag ausloest - vorher ist die Datei nicht
        // einmal geoeffnet. Genau darum haengt der Nachtrag hier und nicht am Programmstart.
        SessionHistoryEntry entry =
            Assert.IsType<SessionHistoryEntry>(composition.History.Find(id));

        Assert.Equal(HistoryOrigin.Backfill, entry.Origin);

        SessionLogEntry note = Assert.Single(composition.Log.Recent(),
                                             e => e.Operation == "history.backfill");

        Assert.Equal(SessionOutcome.Ok, note.Outcome);
        Assert.Equal(SessionTrigger.Startup, note.Trigger);
        Assert.Contains("nachgetragen", note.Reason, StringComparison.Ordinal);
    }

    /// <summary>
    /// Der Nachtrag läuft genau einmal: Ein zweiter Zusammenbau auf derselben Datei rührt
    /// nichts mehr an.
    /// </summary>
    /// <remarks>
    /// Der Auslöser ist der Stand, den die Datei beim <b>Öffnen</b> trug. Nach dem ersten Lauf
    /// steht sie auf dem laufenden Stand; ein zweiter Nachtrag dürfte weder eine Zeile noch
    /// einen zweiten Protokolleintrag hinterlassen.
    /// </remarks>
    [Fact]
    public void Ein_zweiter_Start_traegt_nicht_noch_einmal_nach()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        using (StateDatabase before = new(path))
        {
            using UploadQueue queue = new(before);
            Assert.True(queue.Enqueue(Recent(Guid.NewGuid().ToString())));
        }

        Downgrade(path);

        using (AppHost first = Host(temp, path))
        {
            RuntimeComposition composition =
                Assert.IsType<RuntimeComposition>(first.Composition);

            Assert.Equal(1, composition.History.Count());
        }

        using AppHost second = Host(temp, path);
        RuntimeComposition again = Assert.IsType<RuntimeComposition>(second.Composition);

        Assert.Equal(1, again.History.Count());

        _ = Assert.Single(again.Log.Recent(), e => e.Operation == "history.backfill");
    }

    /// <summary>
    /// Eine frisch angelegte Datei hat nichts nachzutragen — und behauptet es auch nicht.
    /// </summary>
    /// <remarks>
    /// <c>PreviousSchemaVersion</c> ist hier <c>0</c>. Ein Nachtrag, der auch dann anliefe,
    /// schriebe auf einem frischen Rechner einen Protokolleintrag über eine Arbeit, die es
    /// nie gab.
    /// </remarks>
    [Fact]
    public void Eine_frische_Datei_traegt_nichts_nach()
    {
        using TempDirectory temp = new();

        using AppHost host = Host(temp, temp.File("state.db"));
        RuntimeComposition composition = Assert.IsType<RuntimeComposition>(host.Composition);

        Assert.Equal(0, composition.History.Count());

        Assert.DoesNotContain(composition.Log.Recent(),
                              e => e.Operation == "history.backfill");
    }

    /// <summary>
    /// Ist <c>history.enabled</c> aus, wird nichts nachgetragen — der Speicher steht trotzdem.
    /// </summary>
    /// <remarks>
    /// Der Unterschied zum Aufräumtakt: Der Nachtrag <b>schreibt</b>, und was ein
    /// abgeschalteter Verlauf nicht führen soll, soll er auch nicht nachträglich bekommen. Die
    /// Fristen bestehender Zeilen müssen dagegen unabhängig davon weiterlaufen — deshalb ist
    /// der Speicher hier keine Attrappe, sondern lesbar und leer.
    /// </remarks>
    [Fact]
    public void Ein_abgeschalteter_Verlauf_traegt_nichts_nach()
    {
        using TempDirectory temp = new();
        string path = temp.File("state.db");

        using (StateDatabase before = new(path))
        {
            using UploadQueue queue = new(before);
            Assert.True(queue.Enqueue(Recent(Guid.NewGuid().ToString())));
        }

        Downgrade(path);

        AppConfig config = Sample.Config() with
        {
            History = new HistorySection { Enabled = false },
        };

        using AppHost host = Host(temp, path, config);
        RuntimeComposition composition = Assert.IsType<RuntimeComposition>(host.Composition);

        Assert.Equal(0, composition.History.Count());

        Assert.DoesNotContain(composition.Log.Recent(),
                              e => e.Operation == "history.backfill");
    }

    /// <summary>
    /// Eine Nutzlast, deren Sitzung <b>innerhalb</b> der Aufbewahrungsfrist begann.
    /// </summary>
    /// <remarks>
    /// <para><b>Die Vorlage taugt hier nicht.</b> <c>Sample.Upload</c> trägt einen fest
    /// eingetragenen Beginn aus dem September 2025. Der Nachtrag überspringt zu Recht jede
    /// Sitzung, deren Beginn älter ist als <c>history.retention_days</c> — sonst trüge er
    /// Zeilen ein, die der nächste Aufräumlauf sofort wieder entfernt. Mit der Vorlage prüfte
    /// dieser Test also nur, dass ein überfälliger Eintrag draussen bleibt, und nicht, ob die
    /// Verdrahtung überhaupt greift; er wäre an dem Tag still grün geworden, an dem niemand
    /// mehr hinsieht.</para>
    /// <para>Zwei Stunden zurück statt „jetzt“: Der Beginn muss <b>echt</b> vor dem Einreihen
    /// liegen, sonst prüfte der Test eine Sitzung, die es so nicht geben kann.</para>
    /// </remarks>
    /// <param name="id">Die Kennung der Fernwartung.</param>
    private static RemoteSupportWrite Recent(string id)
    {
        long start = DateTimeOffset.UtcNow.AddHours(-2).ToUnixTimeSeconds();

        return Sample.Upload(id) with
        {
            StartTime = start,
            EndTime = start + 1200,
        };
    }

    /// <summary>Baut den Wirt, wie ihn die Anwendung baut — nur mit Wegwerfpfaden.</summary>
    /// <param name="temp">Der Wegwerfordner für Konfiguration und Datenbank.</param>
    /// <param name="statePath">Der Pfad der Zustandsdatenbank.</param>
    /// <param name="config">Die zu schreibende Konfiguration; ohne Angabe die Vorlage.</param>
    private static AppHost Host(TempDirectory temp, string statePath, AppConfig? config = null)
    {
        ConfigStore store = new(temp.File("config.json"));
        store.Save(config ?? Sample.Config());

        AppHost host = new(store, NullLoggerFactory.Instance, new RuntimeNotifier(null),
                           TimeProvider.System, statePath);

        Assert.True(host.Reload(), "Die Konfiguration liess sich nicht laden.");
        return host;
    }

    /// <summary>
    /// Stellt die Datei so her, wie sie vor dem Verlauf aussah: ohne <c>session_history</c>
    /// und mit <c>user_version = 3</c>.
    /// </summary>
    /// <remarks>
    /// Der Umweg über die fertige Datenbank ist Absicht — genau wie im Ablageprojekt. Ein von
    /// Hand geschriebenes Schema vom Stand 3 wäre eine zweite Wahrheit, die beim nächsten
    /// Spaltenzusatz still auseinanderliefe; so entsteht die Datei aus demselben Skript wie im
    /// Betrieb, und nur das Neue wird wieder entfernt.
    /// </remarks>
    /// <param name="path">Die Datei, die zurückgestuft wird.</param>
    private static void Downgrade(string path)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = path, Pooling = false };

        using SqliteConnection connection = new(builder.ToString());
        connection.Open();

        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            DROP TABLE IF EXISTS session_history;
            PRAGMA user_version = 3;
            """;

        _ = command.ExecuteNonQuery();
    }
}
