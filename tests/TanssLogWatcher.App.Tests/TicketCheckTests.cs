using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.App.ViewModels;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Ticketprüfung — als Anzeiger und im Leistungsdialog.
/// </summary>
/// <remarks>
/// <para><b>Der wichtigste Fall steht nicht im Gelingen, sondern im Misslingen.</b> Eine
/// Prüfung, die den Techniker ausbremst, sobald die Leitung aussetzt, ist schlimmer als gar
/// keine: „nicht ermittelt“ ist keine Auskunft über das Ticket, und ein gesperrter Knopf
/// machte daraus eine. Deshalb prüfen mehrere Fälle hier ausdrücklich, dass die Anzeige
/// <i>zeigt</i> und nichts <i>anhält</i>.</para>
///
/// <para><b>Der Abschlussdialog kommt hier nicht mehr vor.</b> Er fragt nicht mehr nach einem
/// Ticket: Daran hängt in TANSS der Weg zur Leistung und damit zur Rechnung, und dort greift
/// dieses Werkzeug nicht mehr hinein.</para>
///
/// <para><b>Kein Netz.</b> Die Prüfstelle ist eine Attrappe, die genau die Antworten gibt, die
/// am 13.09.2026 gegen die Version 10.10.0 gemessen wurden: 200 mit Ticket, 404 mit
/// <c>OBJECT_NOT_FOUND</c>, und alles andere als „nicht ermittelt“.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TicketCheckTests
{
    // --- Der Anzeiger für sich -------------------------------------------------

    [Theory]
    [InlineData("24911", 24911)]
    [InlineData("#24911", 24911)]
    [InlineData("  #24911  ", 24911)]
    // Die Zeile aus der Auswahlliste, so wie sie im Textteil des Feldes landet.
    [InlineData("#24911 — Serverstörung", 24911)]
    public void Eine_Nummer_wird_gelesen(string eingabe, int erwartet)
    {
        Assert.True(TicketCheckPanel.TryReadNumber(eingabe, out int gelesen));
        Assert.Equal(erwartet, gelesen);
    }

    /// <summary>
    /// Was keine Nummer ist, wird nicht zu einer gemacht.
    /// </summary>
    /// <remarks>
    /// <c>24911x</c> ist der Fall, um den es geht: Die Ziffern herauszuschneiden hiesse zu
    /// raten, welche Nummer gemeint war (Hausregel 2). Die 0 ist keine Ticketnummer, sondern
    /// das, was TANSS selbst als „kein Ticket“ führt.
    /// </remarks>
    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("abc")]
    [InlineData("24911x")]
    [InlineData("#")]
    [InlineData("0")]
    [InlineData("-5")]
    [InlineData("99999999999")]
    public void Was_keine_Nummer_ist_wird_abgelehnt(string eingabe)
    {
        Assert.False(TicketCheckPanel.TryReadNumber(eingabe, out int gelesen));
        Assert.Equal(0, gelesen);
    }

    /// <summary>
    /// Ein vorhandenes Ticket zeigt Titel UND Firma.
    /// </summary>
    /// <remarks>
    /// Der häufigste Fehler ist nicht die erfundene Nummer, sondern der Zahlendreher auf ein
    /// <i>anderes vorhandenes</i> Ticket. Nur die Firma lässt ihn auffallen.
    /// </remarks>
    [Fact]
    public async Task Ein_vorhandenes_Ticket_zeigt_Titel_und_Firma()
    {
        TicketCheckPanel anzeige = new(Pruefstelle(Gefunden));

        await anzeige.CheckAsync(24911);

        Assert.Equal(TicketCheckOutcome.Exists, anzeige.Outcome);
        Assert.True(anzeige.ShowNote);
        Assert.False(anzeige.ShowWarning);
        Assert.Contains("24911", anzeige.Note, StringComparison.Ordinal);
        Assert.Contains("Serverstörung", anzeige.Note, StringComparison.Ordinal);
        Assert.Contains("Müller GmbH", anzeige.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Ein_nicht_vorhandenes_Ticket_wird_auffaellig_gezeigt()
    {
        TicketCheckPanel anzeige = new(Pruefstelle(GibtEsNicht));

        await anzeige.CheckAsync(999999999);

        Assert.Equal(TicketCheckOutcome.DoesNotExist, anzeige.Outcome);
        Assert.True(anzeige.ShowWarning);
        Assert.False(anzeige.ShowNote);
    }

    /// <summary>
    /// „Nicht ermittelt“ ist keine Warnung.
    /// </summary>
    /// <remarks>
    /// Es rot zu färben hiesse, aus einem Aussetzer der Leitung einen Vorwurf gegen den
    /// Techniker zu machen. Der Satz steht da, unauffällig, und sagt, dass es offen ist.
    /// </remarks>
    [Fact]
    public async Task Eine_offene_Frage_wird_gezeigt_aber_nicht_als_Warnung()
    {
        TicketCheckPanel anzeige = new(Pruefstelle(NichtErmittelt));

        await anzeige.CheckAsync(24911);

        Assert.Equal(TicketCheckOutcome.Undetermined, anzeige.Outcome);
        Assert.True(anzeige.ShowNote);
        Assert.False(anzeige.ShowWarning);
    }

    /// <summary>
    /// Hausregel 5: Eine werfende Prüfstelle kostet die Prüfung, nicht den Dialog.
    /// </summary>
    /// <remarks>
    /// <see cref="ITicketVerification"/> sagt zu, nicht zu werfen. Gerufen wird sie aber aus
    /// einer Eigenschaftsänderung heraus — ohne Fänger hier risse eine Ausnahme die ganze
    /// Anwendung mit, und darauf soll sich der Dialog nicht verlassen müssen.
    /// </remarks>
    [Fact]
    public async Task Eine_werfende_Pruefstelle_reisst_nichts_mit()
    {
        TicketCheckPanel anzeige = new(new WerfendePruefstelle());

        await anzeige.CheckAsync(24911);

        Assert.Equal(TicketCheckOutcome.Undetermined, anzeige.Outcome);
        Assert.False(anzeige.IsChecking);
        Assert.True(anzeige.ShowNote);
        Assert.False(anzeige.ShowWarning);
    }

    /// <summary>
    /// Ohne Prüfstelle wird nichts behauptet.
    /// </summary>
    /// <remarks>
    /// Weder „gibt es“ noch „liess sich nicht prüfen“: Gefragt wurde niemand. Der Dialog sieht
    /// dann aus wie vorher (Hausregel 2).
    /// </remarks>
    [Fact]
    public async Task Ohne_Pruefstelle_steht_da_nichts()
    {
        TicketCheckPanel anzeige = new(verification: null);

        Assert.False(anzeige.CanCheck);

        await anzeige.CheckAsync(24911);

        Assert.False(anzeige.HasNote);
        Assert.Null(anzeige.Outcome);
    }

    // Hier standen drei Faelle zum ABSCHLUSSDIALOG. Sie sind mit dem Ticketfeld dieses
    // Dialogs entfallen: Er fragt nicht mehr nach einem Ticket, weil daran in TANSS der
    // Weg zur Leistung und damit zur Rechnung haengt - und dort greift dieses Werkzeug
    // nicht mehr hinein. Die Ticketpruefung selbst bleibt geprueft, oben als Anzeiger und
    // unten im Leistungsdialog, wo sie weiterhin sitzt.

    // --- Der Leistungsdialog ---------------------------------------------------

    /// <summary>
    /// <b>Das stille <c>TicketId = 0</c>.</b> Eine getippte Nummer kommt jetzt an.
    /// </summary>
    /// <remarks>
    /// Das Auswahlfeld ist <c>IsEditable=True</c>. Gebunden war nur <c>SelectedItem</c>, und
    /// <c>SelectedTicket?.Id ?? 0</c> liess alles Getippte lautlos fallen — die Leistung landete
    /// ohne Ticket in TANSS, und niemand erfuhr davon.
    /// </remarks>
    [Fact]
    public async Task Eine_getippte_Ticketnummer_faellt_nicht_mehr_auf_null()
    {
        CreateSupportViewModel model = Leistung(Gefunden);

        Assert.Null(model.SelectedTicket);

        model.TicketText = "24911";
        await model.TicketCheckPending;

        Assert.Equal(24911, model.TicketId);
        Assert.False(model.TicketIsUnreadable);
        Assert.Null(model.SelectedTicket);
        Assert.Contains("Müller GmbH", model.TicketCheck.Note, StringComparison.Ordinal);
    }

    /// <summary>Eine Zeile aus der Auswahl wird über ihren Anzeigetext wiedererkannt.</summary>
    [Fact]
    public async Task Eine_gewaehlte_Zeile_wird_uebernommen()
    {
        TicketRow zeile = new(new Ticket { Id = 4711, Title = "Serverumzug" });

        CreateSupportViewModel model = Leistung(Gefunden, zeile);

        model.TicketText = zeile.Display;
        await model.TicketCheckPending;

        Assert.Equal(4711, model.TicketId);
        Assert.False(model.TicketIsUnreadable);
    }

    /// <summary>
    /// Eine unlesbare Eingabe hält den Leistungsdialog an — und sagt es.
    /// </summary>
    /// <remarks>
    /// Anhalten ist hier gefahrlos: Dieser Dialog kennt keine Schonfrist, es geht nichts
    /// verloren, und der Satz unter dem Feld sagt, was zu tun ist. Der Unterschied zum
    /// Abschlussdialog ist damit nicht Willkür, sondern der Unterschied zwischen „nichts
    /// verlierbar“ und „Bericht verlierbar“.
    /// </remarks>
    [Fact]
    public async Task Eine_unlesbare_Eingabe_haelt_die_Leistung_an()
    {
        FakePruefstelle pruefstelle = new(Gefunden);
        CreateSupportViewModel model = Leistung(pruefstelle);

        model.TicketText = "Serverumzug";
        await model.TicketCheckPending;

        Assert.Empty(pruefstelle.Asked);
        Assert.Equal(0, model.TicketId);
        Assert.True(model.TicketIsUnreadable);
        Assert.False(model.CanBook);
        Assert.True(model.TicketCheck.ShowWarning);
    }

    /// <summary>Dasselbe wie im Abschlussdialog: Eine offene Frage hält nichts an.</summary>
    [Fact]
    public async Task Eine_misslungene_Pruefung_haelt_die_Leistung_nicht_an()
    {
        CreateSupportViewModel model = Leistung(NichtErmittelt);

        model.TicketText = "24911";
        await model.TicketCheckPending;

        Assert.Equal(24911, model.TicketId);
        Assert.False(model.TicketIsUnreadable);
        Assert.False(model.TicketCheck.ShowWarning);
    }

    /// <summary>Ein leeres Feld heisst „ohne Ticket“ und ist erlaubt.</summary>
    [Fact]
    public async Task Ein_leeres_Feld_bleibt_erlaubt()
    {
        FakePruefstelle pruefstelle = new(Gefunden);
        CreateSupportViewModel model = Leistung(pruefstelle);

        model.TicketText = "24911";
        await model.TicketCheckPending;

        model.TicketText = string.Empty;
        await model.TicketCheckPending;

        Assert.Equal(0, model.TicketId);
        Assert.False(model.TicketIsUnreadable);
        Assert.False(model.TicketCheck.HasNote);
    }

    // --- Werkzeug --------------------------------------------------------------

    /// <summary>
    /// Der Leistungsdialog ohne Einrichtung: <c>Composition</c> ist <c>null</c>, also holt er
    /// beim Bauen keine Vorbereitung und rührt TANSS nicht an.
    /// </summary>
    private static CreateSupportViewModel Leistung(Func<int, TicketCheck> antwort,
                                                   params TicketRow[] tickets) =>
        Leistung(Pruefstelle(antwort), tickets);

    private static CreateSupportViewModel Leistung(ITicketVerification pruefstelle,
                                                   params TicketRow[] tickets)
    {
        AppHost host = new(loggers: NullLoggerFactory.Instance,
                           notifier: new RuntimeNotifier(null));

        Assert.Null(host.Composition);

        TimerRow timer = new(
            new TanssTimer { Id = 42, Title = "Fernwartung", TicketId = 0, Duration = 600 },
            new DateTimeOffset(2026, 9, 13, 14, 0, 0, TimeSpan.Zero));

        return new CreateSupportViewModel(host, timer, tickets, pruefstelle);
    }

    private static FakePruefstelle Pruefstelle(Func<int, TicketCheck> antwort) =>
        new FakePruefstelle(antwort);

    /// <summary>
    /// Ein Buchungsweg, der nichts sendet.
    /// </summary>
    /// <remarks>
    /// In diesen Fällen geht es um die Ticketprüfung und nicht um das Senden. Ohne diese
    /// Attrappe führe jeder von ihnen einen echten Namen auf und buchte im besten Fall nichts —
    /// im schlechtesten etwas. Was das Senden selbst betrifft, steht in
    /// <c>AbschlussdialogTests</c>.
    /// </remarks>
    private sealed class OhneNetz : IImmediateBooking
    {
        public Task<UploadReport?> BookNowAsync(string remoteMaintenanceId,
                                                CancellationToken ct = default) =>
            Task.FromResult<UploadReport?>(new UploadReport(remoteMaintenanceId,
                UploadOutcome.Deferred,
                "Keine Verbindung zum Server. Zurückgestellt; ein späterer Versuch kann das "
                + "heilen."));
    }

    /// <summary>Die gemessene 200-Antwort: Ticket samt Firma aus <c>meta.linkedEntities</c>.</summary>
    private static TicketCheck Gefunden(int id) => new()
    {
        Outcome = TicketCheckOutcome.Exists,
        TicketId = id,
        Ticket = new Ticket { Id = id, Title = "Serverstörung", CompanyId = 886 },
        CompanyName = "Müller GmbH",
        Status = 200,
        Explanation = "Ticket gibt es.",
    };

    /// <summary>Die gemessene 404-Antwort mit <c>OBJECT_NOT_FOUND</c>.</summary>
    private static TicketCheck GibtEsNicht(int id) => new()
    {
        Outcome = TicketCheckOutcome.DoesNotExist,
        TicketId = id,
        Status = 404,
        Failure = "OBJECT_NOT_FOUND",
        Explanation = "Das Ticket gibt es in TANSS nicht — TANSS meldet OBJECT_NOT_FOUND.",
    };

    /// <summary>Alles, was keine Antwort ist: Netzfehler, 5xx, 403.</summary>
    private static TicketCheck NichtErmittelt(int id) => new()
    {
        Outcome = TicketCheckOutcome.Undetermined,
        TicketId = id,
        Explanation = "Ob es das Ticket gibt, liess sich nicht ermitteln: TANSS war nicht "
            + "erreichbar. Gebucht werden kann trotzdem.",
    };
}

/// <summary>
/// Eine Prüfstelle, die genau die eingestellte Antwort gibt — und mitschreibt, wonach gefragt
/// wurde.
/// </summary>
/// <remarks>
/// <b>Das Mitschreiben ist die halbe Zusage:</b> Dass eine Eingabe ohne Nummer TANSS gar nicht
/// erst behelligt, lässt sich nur an <see cref="Asked"/> zeigen.
/// </remarks>
internal sealed class FakePruefstelle(Func<int, TicketCheck> answer) : ITicketVerification
{
    private readonly Func<int, TicketCheck> _answer = answer;

    /// <summary>Wonach gefragt wurde, in der Reihenfolge der Fragen.</summary>
    public List<int> Asked { get; } = [];

    public Task<TicketCheck> CheckAsync(int ticketId, CancellationToken ct = default)
    {
        Asked.Add(ticketId);
        return Task.FromResult(_answer(ticketId));
    }
}

/// <summary>Eine Prüfstelle, die ihre Zusage bricht und wirft.</summary>
/// <remarks>
/// <see cref="ITicketVerification"/> sagt zu, nicht zu werfen. Eine Attrappe oder eine spätere
/// Version könnte es anders halten, und der Dialog hängt an einer Eigenschaftsänderung — ohne
/// eigenen Fänger risse die Ausnahme die Anwendung mit (Hausregel 5).
/// </remarks>
internal sealed class WerfendePruefstelle : ITicketVerification
{
    public Task<TicketCheck> CheckAsync(int ticketId, CancellationToken ct = default) =>
        throw new InvalidOperationException("Die Prüfstelle ist kaputt.");
}
