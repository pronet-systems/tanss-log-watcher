using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.Storage.Config;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Das Anlegen eines Tickets aus dem Leistungsdialog heraus.
/// </summary>
/// <remarks>
/// <para><b>Der Weg war gebaut und hatte keinen Aufrufer.</b> <c>TicketCreator</c>,
/// <c>CompanyRepository</c> und die Firmenauswahl standen im Werkzeug, ohne dass ein Techniker
/// sie erreichen konnte. Diese Fälle halten fest, dass die Verbindung besteht — und zwar an der
/// Stelle, an der sie gebraucht wird: unter dem Ticketfeld, das gerade „gibt es nicht“
/// meldet.</para>
///
/// <para><b>Der teuerste Fall ist die Dublette.</b> Ein Ticket trägt keine eigene Kennung, an
/// der sich ein zweites Anlegen erkennen liesse. <see cref="Ein_zweiter_Anlauf_legt_kein_zweites_Ticket_an"/>
/// und <see cref="Eine_Antwort_ohne_Nummer_schliesst_den_Riegel_trotzdem"/> zählen deshalb die
/// Aufrufe an der Attrappe und nicht nur die Anzeige.</para>
///
/// <para><b>Kein Netz.</b> Der Anlegeweg ist eine Attrappe hinter
/// <see cref="ITicketCreation"/>; gemessen ist an TANSS hier nichts und wird auch nichts
/// behauptet.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class NewTicketTests
{
    // --- Was ohne TANSS feststeht ----------------------------------------------

    /// <summary>
    /// Ohne Firma geht gar keine Anfrage hinaus.
    /// </summary>
    /// <remarks>
    /// Der Satz stammt wörtlich aus <see cref="TicketDraft.Problem"/> und wird hier nicht neu
    /// erfunden. Ein Ticket ohne Firma landete in TANSS in einer Liste, die niemand ansieht.
    /// </remarks>
    [Fact]
    public async Task Ohne_Firma_wird_nichts_gesendet()
    {
        FakeAnleger anleger = new(_ => Angelegt(5000));
        using NewTicketViewModel modell = Anleger(anleger);
        modell.Title = "Serverstörung";

        await modell.CreateAsync();

        Assert.Equal(0, anleger.Aufrufe);
        Assert.True(modell.IsWarning);
        Assert.False(modell.IsDone);
        Assert.Contains("Ohne Firma", modell.Message, StringComparison.Ordinal);
    }

    /// <summary>Ohne Betreff ebenso — das ist eine Regel dieses Werkzeugs, nicht von TANSS.</summary>
    [Fact]
    public async Task Ohne_Betreff_wird_nichts_gesendet()
    {
        FakeAnleger anleger = new(_ => Angelegt(5000));
        using NewTicketViewModel modell = Anleger(anleger);
        await WaehleFirma(modell, 886, "Müller GmbH");

        await modell.CreateAsync();

        Assert.Equal(0, anleger.Aufrufe);
        Assert.True(modell.IsWarning);
        Assert.Contains("Ohne Betreff", modell.Message, StringComparison.Ordinal);
    }

    /// <summary>Die gewählte Firma und der Betreff gehen so hinaus, wie sie dastehen.</summary>
    [Fact]
    public async Task Firma_und_Betreff_gehen_hinaus()
    {
        TicketDraft? gesendet = null;
        FakeAnleger anleger = new(draft =>
        {
            gesendet = draft;
            return Angelegt(5000);
        });

        using NewTicketViewModel modell = Anleger(anleger, assignTo: 8094);
        await WaehleFirma(modell, 886, "Müller GmbH");
        modell.Title = "  Serverstörung  ";
        modell.Content = "  Der Server antwortet nicht.  ";

        await modell.CreateAsync();

        Assert.NotNull(gesendet);
        Assert.Equal(886, gesendet.CompanyId);
        Assert.Equal("Serverstörung", gesendet.Title);
        Assert.Equal("Der Server antwortet nicht.", gesendet.Content);
        Assert.Equal(8094, gesendet.AssignedToEmployeeId);
    }

    // --- Der Riegel gegen die Dublette -----------------------------------------

    /// <summary>
    /// Ein zweiter Anlauf legt kein zweites Ticket an.
    /// </summary>
    /// <remarks>
    /// Es gibt keine Kennung, an der eine Dublette später zu erkennen wäre — der Riegel ist die
    /// einzige Sicherung, die dieses Werkzeug hat.
    /// </remarks>
    [Fact]
    public async Task Ein_zweiter_Anlauf_legt_kein_zweites_Ticket_an()
    {
        FakeAnleger anleger = new(_ => Angelegt(5000));
        using NewTicketViewModel modell = await Bereit(anleger);

        await modell.CreateAsync();
        await modell.CreateAsync();

        Assert.Equal(1, anleger.Aufrufe);
        Assert.True(modell.IsDone);
        Assert.False(modell.CanCreate);
    }

    /// <summary>
    /// Eine Antwort ohne Ticketnummer schliesst den Riegel ebenfalls.
    /// </summary>
    /// <remarks>
    /// Der Aufruf war angenommen; ein zweiter Versuch legte ein zweites Ticket an. Angezeigt
    /// wird der Satz aus dem Vertrag, und der sagt ausdrücklich <b>nicht</b> „noch einmal
    /// senden“.
    /// </remarks>
    [Fact]
    public async Task Eine_Antwort_ohne_Nummer_schliesst_den_Riegel_trotzdem()
    {
        FakeAnleger anleger = new(_ => new TicketCreateResult
        {
            TicketId = 0,
            CompanyName = "Müller GmbH",
            Warning = "TANSS hat die Anfrage angenommen, aber keine Ticketnummer genannt.",
        });

        using NewTicketViewModel modell = await Bereit(anleger);

        await modell.CreateAsync();
        await modell.CreateAsync();

        Assert.Equal(1, anleger.Aufrufe);
        Assert.True(modell.IsDone);
        Assert.True(modell.IsWarning);
        Assert.Equal(0, modell.CreatedTicketId);
    }

    // --- Fehlschläge kosten den Vorgang, nicht den Dialog ----------------------

    /// <summary>
    /// Weist TANSS ab, bleibt der Anleger bedienbar — und nennt die Ursachen als ungeprüft.
    /// </summary>
    /// <remarks>
    /// Hausregel 5, und Hausregel 2 dazu: Der Text kommt aus <c>TicketCreator.Explain</c> und
    /// behauptet keine Diagnose. Der Riegel bleibt offen, weil nichts angelegt wurde.
    /// </remarks>
    [Fact]
    public async Task Eine_Abweisung_haelt_den_Anleger_nicht_an()
    {
        FakeAnleger anleger = new(_ => throw new TanssException("403 vom Server."));
        using NewTicketViewModel modell = await Bereit(anleger);

        await modell.CreateAsync();

        Assert.True(modell.IsWarning);
        Assert.False(modell.IsDone);
        Assert.True(modell.CanCreate);
        Assert.Contains("nicht belegt", modell.Message, StringComparison.Ordinal);
    }

    /// <summary>Ohne Einrichtung wird nichts angeboten und ausdrücklich gesagt, warum.</summary>
    [Fact]
    public async Task Ohne_Einrichtung_wird_nichts_behauptet()
    {
        using NewTicketViewModel modell = new(creation: null, companies: null);

        Assert.False(modell.IsAvailable);
        Assert.False(modell.CanCreate);

        await modell.CreateAsync();

        Assert.True(modell.IsWarning);
        Assert.Contains("Ohne eingerichtete Verbindung", modell.Message, StringComparison.Ordinal);
    }

    // --- Die Verbindung zum Leistungsdialog ------------------------------------

    /// <summary>
    /// Die neue Nummer landet im Ticketfeld des Leistungsdialogs.
    /// </summary>
    /// <remarks>
    /// Das ist der Punkt der ganzen Anbindung: Ohne ihn müsste der Techniker die Nummer aus der
    /// Meldung abschreiben. Geprüft wird <c>TicketId</c> und nicht nur der Text — gebucht wird
    /// jene Zahl.
    /// </remarks>
    [Fact]
    public async Task Die_neue_Nummer_landet_im_Ticketfeld()
    {
        FakeAnleger anleger = new(_ => Angelegt(5000, "Serverstörung"));
        using NewTicketViewModel neu = await Bereit(anleger);
        using CreateSupportViewModel leistung = Leistung(neu);

        await neu.CreateAsync();
        await leistung.TicketCheckPending;

        Assert.Equal(5000, leistung.TicketId);
        Assert.False(leistung.TicketIsUnreadable);
        Assert.Contains("5000", leistung.TicketText, StringComparison.Ordinal);
        Assert.Contains(leistung.Tickets, row => row.Id == 5000);
    }

    /// <summary>
    /// Ohne Nummer wird nichts übernommen.
    /// </summary>
    /// <remarks>
    /// Eine geratene Nummer stünde sonst gleich in einer gebuchten Leistung (Hausregel 2). Das
    /// Ticketfeld bleibt, wie es war.
    /// </remarks>
    [Fact]
    public async Task Ohne_Nummer_wird_nichts_uebernommen()
    {
        FakeAnleger anleger = new(_ => new TicketCreateResult { TicketId = 0 });
        using NewTicketViewModel neu = await Bereit(anleger);
        using CreateSupportViewModel leistung = Leistung(neu);

        await neu.CreateAsync();

        Assert.Equal(0, leistung.TicketId);
        Assert.Equal(string.Empty, leistung.TicketText);
    }

    // --- Der Zusammenbau -------------------------------------------------------

    /// <summary>
    /// Firmensuche und Anlegeweg sind aus der Laufzeit erreichbar.
    /// </summary>
    /// <remarks>
    /// Ohne diese beiden Eigenschaften hinge die ganze Anbindung in der Luft: Der Dialog holt
    /// sie genau hier ab. Der Fall baut eine echte <see cref="RuntimeComposition"/> aus einer
    /// echten Konfigurationsdatei; angefragt wird dabei nichts.
    /// </remarks>
    [Fact]
    public void Die_Laufzeit_traegt_Firmensuche_und_Anlegeweg()
    {
        using TempDirectory temp = new();

        ConfigStore store = new(temp.File("config.json"));
        store.Save(Sample.Config());

        using AppHost host = new(store, NullLoggerFactory.Instance, new RuntimeNotifier(null),
                                 TimeProvider.System, temp.File("state.db"));

        Assert.True(host.Reload(), "Die Konfiguration liess sich nicht laden.");

        RuntimeComposition composition = Assert.IsType<RuntimeComposition>(host.Composition);

        Assert.NotNull(composition.Companies);
        Assert.NotNull(composition.NewTickets);
    }

    /// <summary>
    /// Der Leistungsdialog baut sich seinen Anleger selbst, wenn keiner übergeben wird.
    /// </summary>
    /// <remarks>
    /// Ohne Einrichtung bekommt er zweimal <c>null</c> — die Bindungen im Fenster zeigen
    /// trotzdem auf ein Objekt, und dieses sagt selbst, dass es nicht arbeiten kann.
    /// </remarks>
    [Fact]
    public void Der_Leistungsdialog_hat_immer_einen_Anleger()
    {
        using CreateSupportViewModel leistung = Leistung(newTicket: null);

        Assert.NotNull(leistung.NewTicket);
        Assert.False(leistung.NewTicket.IsAvailable);
        Assert.False(leistung.NewTicket.IsOpen);
    }

    // --- Werkzeug --------------------------------------------------------------

    private static NewTicketViewModel Anleger(ITicketCreation? anleger, int assignTo = 0) =>
        new(anleger, new CompanyPickerViewModel(new EineFirma()), assignTo);

    /// <summary>Ein Anleger mit gewählter Firma und Betreff — bereit zum Absenden.</summary>
    private static async Task<NewTicketViewModel> Bereit(ITicketCreation anleger)
    {
        NewTicketViewModel modell = Anleger(anleger);
        await WaehleFirma(modell, 886, "Müller GmbH");
        modell.Title = "Serverstörung";
        return modell;
    }

    /// <summary>
    /// Sucht und wählt eine Firma — über denselben Weg, den auch die Entprellung geht.
    /// </summary>
    private static async Task WaehleFirma(NewTicketViewModel modell, int id, string name)
    {
        modell.Company.Query = name;
        await modell.Company.SearchNowAsync();

        CompanyRow zeile = Assert.Single(modell.Company.Companies);
        Assert.Equal(id, zeile.Id);

        modell.Company.Select(zeile);
        Assert.True(modell.Company.HasSelection);
    }

    /// <summary>
    /// Der Leistungsdialog ohne Einrichtung: <c>Composition</c> ist <c>null</c>, also holt er
    /// beim Bauen keine Vorbereitung und rührt TANSS nicht an.
    /// </summary>
    private static CreateSupportViewModel Leistung(NewTicketViewModel? newTicket)
    {
        AppHost host = new(loggers: NullLoggerFactory.Instance,
                           notifier: new RuntimeNotifier(null));

        Assert.Null(host.Composition);

        TimerRow timer = new(
            new TanssTimer { Id = 42, Title = "Fernwartung", TicketId = 0, Duration = 600 },
            new DateTimeOffset(2026, 9, 13, 14, 0, 0, TimeSpan.Zero));

        return new CreateSupportViewModel(host, timer, [], verification: null,
                                          newTicket: newTicket);
    }

    private static TicketCreateResult Angelegt(int id, string titel = "Serverstörung") => new()
    {
        TicketId = id,
        Ticket = new Ticket { Id = id, Title = titel, CompanyId = 886 },
        CompanyName = "Müller GmbH",
    };

    /// <summary>Ein Anlegeweg, der zählt und antwortet, wie der Fall es verlangt.</summary>
    private sealed class FakeAnleger(Func<TicketDraft, TicketCreateResult> antwort)
        : ITicketCreation
    {
        private readonly Func<TicketDraft, TicketCreateResult> _antwort = antwort;

        public int Aufrufe { get; private set; }

        public Task<TicketCreateResult> CreateAsync(TicketDraft draft,
                                                    CancellationToken ct = default)
        {
            ArgumentNullException.ThrowIfNull(draft);

            Aufrufe++;
            return Task.FromResult(_antwort(draft));
        }
    }

    /// <summary>Eine Firmensuche, die genau eine Firma kennt.</summary>
    private sealed class EineFirma : ICompanyRepository
    {
        public Task<CompanySearchResult> SearchAsync(string query,
                                                     CancellationToken ct = default) =>
            Task.FromResult(new CompanySearchResult
            {
                Outcome = CompanySearchOutcome.Found,
                Companies = [new Company { Id = 886, Name = "Müller GmbH" }],
                TotalFound = 1,
                Threshold = CompanyRepositoryDefaults.Threshold,
                Requests = 1,
                Explanation = "Eine Firma gefunden.",
            });
    }
}
