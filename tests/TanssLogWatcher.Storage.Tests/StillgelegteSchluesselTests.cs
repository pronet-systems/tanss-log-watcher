using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Stillgelegte Schlüssel halten eine bestehende Konfiguration nicht auf.
/// </summary>
/// <remarks>
/// <para><b>Der Befund, aus dem diese Datei entstanden ist.</b> <c>recording.segment_minutes</c>
/// war der Zeittakt, nach dem eine neue Videodatei begann. Er ist ersatzlos entfallen: Seit die
/// Datei bruchstückweise mit vorangestelltem Index geschrieben wird, übersteht sie einen
/// Absturz auch ohne Abschluss, und eine Sitzung ergibt genau eine Datei.</para>
///
/// <para><b>Warum das gefährlich ist.</b> Die Abschnitte lehnen unbekannte Felder ab, und das
/// ist richtig so — wer <c>exclude_ip_adresses</c> schreibt, hält seine Ausschlussliste für
/// aktiv, während sie ins Leere geht. Dieselbe Strenge träfe aber auch den stillgelegten
/// Schlüssel: Eine bestehende <c>config.json</c> liefe nach dem Update als „nicht eingerichtet“
/// auf — und dann würde auch keine Fernwartung mehr gebucht. Ein Update, das die Arbeitszeit
/// kostet, ist schlimmer als gar keins.</para>
///
/// <para>Der erste Fall ist deshalb der eigentliche: Eine Datei mit dem alten Schlüssel muss
/// laden. Der zweite hält fest, dass die Strenge dabei nicht verlorengeht.</para>
/// </remarks>
public sealed class StillgelegteSchluesselTests
{
    /// <summary>
    /// Eine Konfiguration mit <c>segment_minutes</c> lädt — der Schlüssel wird übergangen.
    /// </summary>
    [Fact]
    public void Der_alte_Zeittakt_haelt_die_Konfiguration_nicht_auf()
    {
        AppConfig config = ConfigStore.Parse(MitSegmentMinutes);

        // Der eigentliche Punkt: Es gibt keine Ausnahme, und der Rest des Abschnitts steht.
        Assert.Equal(30, config.Recording.RetentionDays);
        Assert.Equal(4, config.Recording.FramesPerSecond);
    }

    /// <summary>
    /// Beim Speichern ist der Schlüssel fort — ohne dass jemand etwas tun musste.
    /// </summary>
    /// <remarks>
    /// Das ist die Zusage an den Techniker: Er bekommt keine Aufgabe, er bekommt eine saubere
    /// Datei. Geprüft wird am geschriebenen Text und nicht am Objekt — im Objekt gibt es das
    /// Feld ohnehin nicht mehr, und genau deshalb wäre eine Prüfung dort wertlos.
    /// </remarks>
    [Fact]
    public void Nach_dem_Speichern_steht_der_Schluessel_nicht_mehr_in_der_Datei()
    {
        using TempDirectory temp = new();
        string pfad = temp.File("config.json");

        File.WriteAllText(pfad, MitSegmentMinutes);

        ConfigStore store = new(pfad);
        AppConfig geladen = store.Load();
        store.Save(geladen);

        string danach = File.ReadAllText(pfad);

        Assert.DoesNotContain("segment_minutes", danach, StringComparison.Ordinal);

        // Gegenprobe: Der Abschnitt ist noch da, es wurde nicht zu viel geräumt.
        Assert.Contains("retention_days", danach, StringComparison.Ordinal);
    }

    /// <summary>
    /// Ein <b>vertippter</b> Schlüssel fällt weiterhin auf.
    /// </summary>
    /// <remarks>
    /// Die Gegenprobe, ohne die die Räumung gefährlich wäre: Geräumt wird genau die Liste der
    /// stillgelegten Schlüssel und nichts, was bloss unbekannt ist. Eine Einstellung, die
    /// stillschweigend ins Leere geht, ist schlimmer als ein Fehler.
    /// </remarks>
    [Fact]
    public void Ein_vertippter_Schluessel_faellt_weiterhin_auf()
    {
        string vertippt = MitSegmentMinutes.Replace(
            "\"retention_days\": 30,",
            "\"retention_days\": 30,\n    \"frames_per_secnod\": 4,",
            StringComparison.Ordinal);

        _ = Assert.Throws<ConfigException>(() => ConfigStore.Parse(vertippt));
    }

    /// <summary>
    /// Auch eine <b>kommentierte</b> Konfiguration wird geräumt.
    /// </summary>
    /// <remarks>
    /// <para><b>Der Fall, an dem der erste Anlauf gescheitert ist.</b> Die Konfiguration darf
    /// Kommentare und nachgestellte Kommata tragen — die mitgelieferte
    /// <c>config.example.json</c> macht von beidem Gebrauch. Die Räumung las zuerst mit den
    /// Vorgaben von <c>JsonNode.Parse</c>, und die vertragen weder das eine noch das andere:
    /// Sie scheiterte still, reichte den Text unverändert weiter, und der stillgelegte
    /// Schlüssel schlug wieder durch.</para>
    /// <para>Aufgefallen ist das nicht beim Nachdenken, sondern an drei Prüffällen der
    /// Kommandozeile, die über die Beispieldatei laufen.</para>
    /// </remarks>
    [Fact]
    public void Auch_mit_Kommentaren_wird_geraeumt()
    {
        string kommentiert = """
        {
          // Die Instanz, gegen die gebucht wird.
          "tanss": {
            "base_url": "https://tanss.example.invalid/backend",
            "employee_id": 1,
            "token_ref": "dpapi"
          },
          "recording": {
            "retention_days": 30,

            // Eine lange Fernwartung in einer einzigen Datei ist beim ersten Fehler verloren.
            "segment_minutes": 10,
            "frames_per_second": 4,
          },
          "monitoring": [ { "key": "rdp", "remote_support_type_id": 1001 } ]
        }
        """;

        AppConfig config = ConfigStore.Parse(kommentiert);

        Assert.Equal(30, config.Recording.RetentionDays);
        Assert.Equal(4, config.Recording.FramesPerSecond);
    }

    /// <summary>
    /// Ein Text, der gar kein JSON ist, geht unverändert weiter — die Meldung kommt aus der
    /// Abbildung, wo sie Zeile und Spalte nennt.
    /// </summary>
    [Fact]
    public void Unlesbarer_Text_wird_nicht_still_verschluckt()
    {
        _ = Assert.Throws<ConfigException>(() => ConfigStore.Parse("{ das ist kein JSON"));
    }

    /// <summary>
    /// Eine Konfiguration, wie sie vor dem Wegfall des Zeittakts aussah.
    /// </summary>
    /// <remarks>
    /// Nachgebaut nach einer echten Datei dieses Arbeitsplatzes — dort stand
    /// <c>"segment_minutes": 10</c> im Abschnitt <c>recording</c>. Die Adresse und die
    /// Mitarbeiterkennung sind Beispielwerte; auf sie kommt es hier nicht an.
    /// </remarks>
    private const string MitSegmentMinutes = """
    {
      "tanss": {
        "base_url": "https://tanss.example.invalid/backend",
        "employee_id": 1,
        "token_ref": "dpapi"
      },
      "recording": {
        "retention_days": 30,
        "frames_per_second": 4,
        "segment_minutes": 10,
        "minimum_free_megabytes": 2048
      },
      "monitoring": [ { "key": "rdp", "remote_support_type_id": 1001 } ]
    }
    """;
}
