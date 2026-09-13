using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Rückfrage vor dem Löschen eines Timers.
/// </summary>
/// <remarks>
/// <para><b>Warum es diese Fälle gibt.</b> Die Dokumentation der Timerseite sagte die Rückfrage
/// seit jeher zu — „Die Rückfrage stellt die Ansicht“ —, und die Ansicht stellte keine: Der
/// erste Klick löschte. Es war die dritte dokumentierte Zusage dieser Sitzung, die niemand
/// gebaut hatte, und alle drei hatten dasselbe gemeinsam: Nichts prüfte sie.</para>
///
/// <para><b>Prüfbar ist das nur, weil die Rückfrage in der Seite steht und nicht in einem
/// Fenster.</b> Sie ist damit ein Zustand dieses Ansichtsmodells. Ein modaler Dialog wäre ohne
/// Oberfläche nicht zu prüfen gewesen — die Zusage hinge wieder an der Aufmerksamkeit dessen,
/// der sie liest.</para>
///
/// <para>Kein Netz ist im Spiel: Ohne geladene Konfiguration ist
/// <see cref="AppHost.Composition"/> <c>null</c>, und das Ansichtsmodell holt beim Bauen
/// nichts. Was hier geprüft wird, ist ausdrücklich das Verhalten VOR jedem Aufruf an TANSS.
/// </para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TimerDeleteConfirmTests
{
    [Fact]
    public void Der_Loeschknopf_fragt_und_loescht_nicht()
    {
        using TimersViewModel model = Modell();
        TimerRow row = Zeile();

        Assert.False(model.HasPendingDelete);

        model.AskDeleteCommand.Execute(row);

        Assert.True(model.HasPendingDelete);
        Assert.Same(row, model.PendingDelete);
    }

    /// <summary>
    /// Die eigentliche Zusage, und sie steht im Bau und nicht in der Beschriftung.
    /// </summary>
    /// <remarks>
    /// <c>ConfirmDeleteAsync</c> nimmt <b>keinen</b> Übergabewert. Es kann deshalb nur löschen,
    /// was zuvor hingelegt wurde — eine Schaltfläche, die diesen Befehl aus Versehen mit einer
    /// Zeile bindet, löscht nichts. Genau so ist die Rückfrage nicht zu umgehen.
    /// </remarks>
    [Fact]
    public async Task Ohne_vorherige_Frage_loescht_der_Bestaetigungsbefehl_nichts()
    {
        using TimersViewModel model = Modell();

        Assert.False(model.HasPendingDelete);

        await model.ConfirmDeleteCommand.ExecuteAsync(null);

        Assert.False(model.HasPendingDelete);
        Assert.Null(model.PendingDelete);
    }

    [Fact]
    public void Abbrechen_nimmt_die_Frage_zurueck()
    {
        using TimersViewModel model = Modell();

        model.AskDeleteCommand.Execute(Zeile());
        Assert.True(model.HasPendingDelete);

        model.CancelDeleteCommand.Execute(null);

        Assert.False(model.HasPendingDelete);
        Assert.Null(model.PendingDelete);
    }

    /// <summary>
    /// Die Leiste muss die Frage beantwortbar machen.
    /// </summary>
    /// <remarks>
    /// „Wirklich löschen?“ allein wird weggeklickt, ohne dass jemand hinsieht. Wer zwei Timer
    /// offen hat, muss am Text erkennen, welcher von beiden gemeint ist — und dass Zeit daran
    /// hängt.
    /// </remarks>
    [Fact]
    public void Die_Rueckfrage_benennt_den_Timer_um_den_es_geht()
    {
        using TimersViewModel model = Modell();

        model.AskDeleteCommand.Execute(Zeile(titel: "Serverumzug", ticket: 4711,
                                             gelaufen: 3 * 3600 + 25 * 60));

        string text = model.PendingDeleteText;

        Assert.Contains("Serverumzug", text, StringComparison.Ordinal);
        Assert.Contains("4711", text, StringComparison.Ordinal);
        Assert.Contains("03:25", text, StringComparison.Ordinal);
        Assert.DoesNotContain("läuft gerade", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_laufender_Timer_wird_als_laufend_benannt()
    {
        using TimersViewModel model = Modell();

        model.AskDeleteCommand.Execute(Zeile(laeuft: true));

        Assert.Contains("läuft gerade", model.PendingDeleteText, StringComparison.Ordinal);
    }

    [Fact]
    public void Ohne_Frage_steht_kein_Text()
    {
        using TimersViewModel model = Modell();

        Assert.Equal(string.Empty, model.PendingDeleteText);
    }

    /// <summary>
    /// Eine Laufzeit ohne Konfiguration: <c>Composition</c> bleibt <c>null</c>, und damit holt
    /// das Ansichtsmodell beim Bauen weder Timer noch Tickets.
    /// </summary>
    private static TimersViewModel Modell()
    {
        AppHost host = new(loggers: NullLoggerFactory.Instance,
                           notifier: new RuntimeNotifier(null));

        Assert.Null(host.Composition);

        return new TimersViewModel(host, new SystemLookup());
    }

    /// <summary>Eine Zeile aus einem echten <see cref="TanssTimer"/>, wie TANSS ihn liefert.</summary>
    private static TimerRow Zeile(string titel = "Fernwartung", int ticket = 1234,
                                  long gelaufen = 600, bool laeuft = false)
    {
        DateTimeOffset jetzt = new(2026, 9, 13, 14, 0, 0, TimeSpan.Zero);

        return new TimerRow(
            new TanssTimer
            {
                Id = 42,
                Title = titel,
                TicketId = ticket,
                Duration = gelaufen,
                // Unix-Sekunden; 0 heisst angehalten.
                StartTime = laeuft ? jetzt.ToUnixTimeSeconds() : 0,
            },
            jetzt);
    }
}
