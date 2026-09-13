using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Secrets;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Was diese Schicht schwärzt, bevor es in <c>state.db</c> steht.
/// </summary>
/// <remarks>
/// Das Schwärzen selbst gehört nicht mehr hierher: Es gibt genau eine Version, die der
/// API-Schicht, und ihre Muster sind dort geprüft. Hier steht, dass das Protokoll sie
/// tatsächlich benutzt — und was mit einer Fensterbeschriftung geschieht.
/// </remarks>
public sealed class RedactionTests
{
    private const string WindowTitle = "Fernwartung auf kunde-srv01 über Putty, 12 Minuten, Ticket 98765";

    [Fact]
    public void Das_Token_aus_der_Anmeldung_steht_nicht_im_Protokoll()
    {
        // POST /api/v1/login liefert den apiKey als nacktes Feld - kein JWT, das die
        // eyJ-Regel faenge. Die frueher hier gepflegte, schwaechere Schwaerzung kannte das
        // Feldmuster nicht; der Schluessel lag im Klartext in state.db.
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"));

        _ = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-1",
            Operation = "login",
            Outcome = SessionOutcome.Error,
            Trigger = SessionTrigger.Startup,
            Reason = """Antwort: {"apiKey":"a1b2c3d4e5f6g7h8","employeeId":42}""",
            Detail = "apiKey: a1b2c3d4e5f6g7h8",
        });

        SessionLogEntry entry = Assert.Single(log.ForSession("sitzung-1"));

        Assert.DoesNotContain("a1b2c3d4e5f6g7h8", entry.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain("a1b2c3d4e5f6g7h8", entry.Detail!, StringComparison.Ordinal);
        // Der Rest der Meldung bleibt brauchbar, sonst waere die Schwaerzung ein Datenverlust.
        Assert.Contains("employeeId", entry.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Eine_Fensterbeschriftung_steht_nicht_im_Klartext_im_Protokoll()
    {
        // Frueher sicherte hier ein Test ausdruecklich zu, dass genau dieser Satz
        // unveraendert stehen bleibt - waehrend logging.redact_window_titles auf true stand
        // und nirgends ausgewertet wurde. Die Zusage gilt jetzt in die andere Richtung.
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"));

        Assert.True(log.RedactsWindowTitles);

        _ = log.Append(new SessionLogEntry
        {
            RemoteMaintenanceId = "sitzung-1",
            Operation = "session.opened",
            Outcome = SessionOutcome.Ok,
            Trigger = SessionTrigger.Watcher,
            Reason = "Fenster erkannt",
            WindowTitle = WindowTitle,
        });

        SessionLogEntry entry = Assert.Single(log.ForSession("sitzung-1"));

        Assert.DoesNotContain("kunde-srv01", entry.WindowTitle!, StringComparison.Ordinal);
        Assert.NotEqual(WindowTitle, entry.WindowTitle);
        Assert.Contains("geschwärzt", entry.WindowTitle!, StringComparison.Ordinal);
    }

    [Fact]
    public void Dieselbe_Beschriftung_bleibt_wiedererkennbar()
    {
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"));

        _ = log.Append(Entry("sitzung-1", WindowTitle));
        _ = log.Append(Entry("sitzung-2", WindowTitle));
        _ = log.Append(Entry("sitzung-3", "Remotedesktopverbindung — anderer-kunde"));

        string first = log.ForSession("sitzung-1")[0].WindowTitle!;
        string second = log.ForSession("sitzung-2")[0].WindowTitle!;
        string third = log.ForSession("sitzung-3")[0].WindowTitle!;

        // „Dasselbe Fenster?“ bleibt beantwortbar, ohne dass der Kundenname dasteht.
        Assert.Equal(first, second);
        Assert.NotEqual(first, third);
    }

    [Fact]
    public void Ohne_die_Einstellung_bleibt_die_Beschriftung_stehen()
    {
        // Die Einstellung wirkt in beide Richtungen - sonst waere sie wieder nur Zierde.
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"), timeProvider: null,
                                   redactWindowTitles: false);

        Assert.False(log.RedactsWindowTitles);

        _ = log.Append(Entry("sitzung-1", WindowTitle));

        Assert.Equal(WindowTitle, log.ForSession("sitzung-1")[0].WindowTitle);
    }

    [Fact]
    public void Ein_Kennwort_in_der_Beschriftung_faellt_auch_ohne_Schwaerzung_fort()
    {
        using TempDirectory temp = new();
        using SessionLog log = new(temp.File("state.db"), timeProvider: null,
                                   redactWindowTitles: false);

        _ = log.Append(Entry("sitzung-1", """Anmeldung {"apiKey":"a1b2c3d4e5f6g7h8"}"""));

        Assert.DoesNotContain("a1b2c3d4e5f6g7h8", log.ForSession("sitzung-1")[0].WindowTitle!,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_fehlendes_Detail_bleibt_leer_und_wird_nicht_zur_leeren_Zeichenkette()
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

        SessionLogEntry entry = Assert.Single(log.ForSession("sitzung-1"));

        // „Gar kein Detail erfasst“ ist etwas anderes als „nichts zu sagen“.
        Assert.Null(entry.Detail);
        Assert.Null(entry.WindowTitle);
    }

    [Fact]
    public void Der_Fingerabdruck_unterscheidet_ohne_zu_verraten()
    {
        const string jwt =
            "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIiwiZXhwIjoxOTAwMDAwMDAwfQ.KiZ_Pd7w-AbC123";

        string first = SecretFingerprint.Of("Bearer " + jwt);
        string second = SecretFingerprint.Of("Bearer anderes-token");

        Assert.Equal(first, SecretFingerprint.Of("Bearer " + jwt));
        Assert.NotEqual(first, second);
        Assert.Equal(8, first.Length);
        Assert.DoesNotContain("eyJ", first, StringComparison.Ordinal);
        Assert.Equal("(leer)", SecretFingerprint.Of(null));
        Assert.Equal("(leer)", SecretFingerprint.Of(string.Empty));
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
