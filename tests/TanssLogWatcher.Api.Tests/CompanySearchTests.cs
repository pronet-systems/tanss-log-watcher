using System.Net;
using System.Text.Json;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Api.Repository;
using TanssLogWatcher.Api.Tests.Fakes;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>
/// Prüft die Firmensuche gegen eine Attrappe, die die <b>Schwellenlogik</b> von TANSS nachbildet.
/// </summary>
/// <remarks>
/// Der Kern ist <see cref="FakeTanss"/>: Die Attrappe sucht Teilzeichenketten ohne Rücksicht auf
/// Groß- und Kleinschreibung und antwortet — wie nachgemessen — mit einer <b>leeren</b> Liste,
/// sobald die Trefferzahl <c>maxResults</c> übersteigt. Ohne dieses Verhalten prüfte der Test
/// etwas anderes als die Wirklichkeit.
/// </remarks>
public sealed class CompanySearchTests
{
    [Fact]
    public void Firmenzeile_Zeigt_Kundennummer_Und_Ort()
    {
        Company company = new()
        {
            Id = 5,
            Name = "ProNet Systems GmbH",
            DisplayId = "PRO-1000",
            PostCode = "55743",
            City = "Idar-Oberstein",
        };

        Assert.Equal("ProNet Systems GmbH (PRO-1000 · 55743 Idar-Oberstein)", company.ToString());
    }

    [Fact]
    public void Ohne_Jedes_Merkmal_Bleibt_Die_Kennung_Als_Unterscheidung()
    {
        Company company = new() { Id = 5, Name = "Namenlos" };

        Assert.Equal("ID 5", company.Distinguisher);
    }

    [Fact]
    public async Task Unter_Drei_Zeichen_Wird_TANSS_Gar_Nicht_Erst_Gefragt()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("Pr");

        Assert.Equal(CompanySearchOutcome.QueryTooShort, result.Outcome);
        Assert.Equal(0, tanss.Handler.Calls);
        Assert.Contains("3 Zeichen", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Leerraum_Zaehlt_Nicht_Als_Zeichen()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("  Pr  ");

        Assert.Equal(CompanySearchOutcome.QueryTooShort, result.Outcome);
        Assert.Equal(0, tanss.Handler.Calls);
    }

    [Fact]
    public async Task Die_Suche_Geht_Per_PUT_Mit_Bereich_COMPANY_Und_LoggedInUserId()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        await repository.SearchAsync("Pro");

        CapturedRequest sent = tanss.Handler.Last;
        Assert.Equal("PUT", sent.Method);
        Assert.StartsWith(TestEnvironment.BaseUrl + "/api/v1/search", sent.Uri.ToString(),
                          StringComparison.Ordinal);

        // Auf /api/v1 ist der Parameter zwingend - ohne ihn antwortet TANSS mit 403.
        Assert.Equal("42", sent.Query("loggedInUserId"));

        using JsonDocument body = JsonDocument.Parse(sent.Body!);
        Assert.Equal("COMPANY", body.RootElement.GetProperty("areas")[0].GetString());
        Assert.Equal("Pro", body.RootElement.GetProperty("query").GetString());
    }

    [Fact]
    public async Task Gefragt_Wird_Mit_Einer_Hohen_Schwelle_Und_Nicht_Mit_Den_Dokumentierten_Hundert()
    {
        // Der dokumentierte Vorgabewert 100 waere bei 540 Treffern eine leere Liste - und die
        // Oberflaeche sagte "keine Firma gefunden" ueber 540 Firmen.
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        await repository.SearchAsync("Pro");

        using JsonDocument body = JsonDocument.Parse(tanss.Handler.Last.Body!);
        int max = body.RootElement.GetProperty("configs").GetProperty("company")
            .GetProperty("maxResults").GetInt32();

        Assert.True(max >= 10_000, "Die Schwelle muss weit über jeder Firmenzahl liegen.");
    }

    [Fact]
    public async Task Ein_Treffersatz_Kommt_Vollzaehlig_Mit_Einem_Einzigen_Aufruf()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("GmbH");

        Assert.Equal(CompanySearchOutcome.Found, result.Outcome);
        Assert.Equal(Sample.Count(company => company.Name.Contains("GmbH", StringComparison.Ordinal)),
                     result.TotalFound);
        Assert.False(result.Truncated);
        Assert.Equal(1, tanss.Handler.Calls);
        Assert.True(result.IsCertain);
    }

    [Fact]
    public async Task Teilzeichenkette_Und_Grossschreibung_Sind_Gleichgueltig()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("roN");

        Assert.Equal(CompanySearchOutcome.Found, result.Outcome);
        Assert.Contains(result.Companies, company => company.Name.StartsWith("ProNet",
                                                                            StringComparison.Ordinal));
    }

    /// <summary>
    /// Der Fall, um den es geht: Die Schwelle ist überschritten, TANSS schweigt — und das Ergebnis
    /// darf auf keinen Fall „keine Firma gefunden“ lauten.
    /// </summary>
    [Fact]
    public async Task Eine_Ueberschrittene_Schwelle_Wird_Nicht_Zu_Keine_Firma_Gefunden()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);

        // Schwelle 2, aber "GmbH" hat mehr Treffer: der erste Aufruf kommt leer zurueck.
        CompanyRepository repository = new(client, threshold: 2, displayLimit: 50);

        CompanySearchResult result = await repository.SearchAsync("GmbH");

        Assert.Equal(CompanySearchOutcome.Found, result.Outcome);
        Assert.NotEqual(CompanySearchOutcome.NoMatch, result.Outcome);
        Assert.Equal(Sample.Count(company => company.Name.Contains("GmbH", StringComparison.Ordinal)),
                     result.TotalFound);

        // Stufe 1 leer, Stufe 2 (Obermenge "Gmb") ebenfalls ueber der Schwelle, Stufe 3 klaert es.
        Assert.Equal(3, result.Requests);
        Assert.Equal(40, result.Threshold);
    }

    [Fact]
    public async Task Die_Erhoehte_Schwelle_Steht_Auch_Wirklich_Im_Letzten_Rumpf()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client, threshold: 2, displayLimit: 50);

        await repository.SearchAsync("GmbH");

        using JsonDocument body = JsonDocument.Parse(tanss.Handler.Last.Body!);
        Assert.Equal(40, body.RootElement.GetProperty("configs").GetProperty("company")
            .GetProperty("maxResults").GetInt32());
    }

    /// <summary>
    /// Die Leere ist hier <b>hergeleitet</b> und nicht geraten: Die Obermenge „Pro“ passt unter die
    /// Schwelle, also kann auch die Teilmenge die Schwelle nicht überschritten haben.
    /// </summary>
    [Fact]
    public async Task Keine_Treffer_Wird_Ueber_Eine_Obermenge_Bewiesen()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("Proximus");

        Assert.Equal(CompanySearchOutcome.NoMatch, result.Outcome);
        Assert.True(result.IsCertain);
        Assert.Equal(2, result.Requests);
        Assert.Equal("Pro", tanss.Queries[1]);
        Assert.Contains("Keine Firma", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Bei_Genau_Drei_Zeichen_Ohne_Treffer_Heisst_Es_Nicht_Ermittelt()
    {
        // Eine erlaubte Obermenge gaebe es nur mit zwei Zeichen - wie TANSS darauf antwortet, ist
        // nicht gemessen. Statt den Beweis auf eine Vermutung zu stellen, wird die Unkenntnis
        // gemeldet.
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("xyz");

        Assert.Equal(CompanySearchOutcome.Undetermined, result.Outcome);
        Assert.False(result.IsCertain);
        Assert.Contains("nicht ermittelt", result.Explanation, StringComparison.Ordinal);
        Assert.Equal(2, result.Requests);
    }

    [Fact]
    public async Task Eine_Gescheiterte_Gegenprobe_Kostet_Die_Gewissheit_Aber_Nicht_Den_Vorgang()
    {
        // Hausregel 5: Der erste Aufruf traegt den Vorgang, jeder weitere ist ein Zusatz.
        FakeTanss tanss = new(Sample, failFromCall: 2);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("Proximus");

        Assert.Equal(CompanySearchOutcome.Undetermined, result.Outcome);
        Assert.Contains("nicht ermittelt", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Der_Erste_Aufruf_Darf_Sehr_Wohl_Werfen()
    {
        RecordingHandler handler = new(HttpStatusCode.Forbidden,
                                       TestEnvironment.Error("NOT_ALLOWED"));
        using TanssClient client = TestEnvironment.Client(handler);
        CompanyRepository repository = new(client);

        await Assert.ThrowsAsync<TanssAuthException>(() => repository.SearchAsync("Pro"));
    }

    [Fact]
    public async Task Eine_Gesperrte_Firma_Erscheint_Sichtbar_Aber_Unwaehlbar()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("Sperrhaus");

        Company found = Assert.Single(result.Companies);
        Assert.True(found.Lockout);
        Assert.False(found.Selectable);
        Assert.Equal(0, result.SelectableCount);
        Assert.Contains("gesperrt", found.StatusNote!, StringComparison.Ordinal);
        Assert.Contains("gesperrt", result.Explanation, StringComparison.Ordinal);
    }

    /// <summary>
    /// Eine inaktive Firma wird ausgeblendet — aber nicht verschwiegen.
    /// </summary>
    /// <remarks>
    /// Der Vorgänger dieses Tests hielt die umgekehrte Zusage fest: inaktiv sichtbar und
    /// gekennzeichnet. Gemessen waren das in der Instanz des Technikers 5 von 11 Treffern, und
    /// auf eine aufgelöste Firma wird nicht gebucht — die Liste trug also mehr Altlast als
    /// Auswahl.
    /// <para>Der Rest des Tests ist der eigentliche Punkt: Der Satz muss sagen, dass es
    /// Treffer GAB. Ohne ihn läse sich diese Suche wie „es gibt keine Firma Altlast“, und das
    /// ist genau die stille Unwahrheit, gegen die dieser ganze Vertrag angelegt ist.</para>
    /// </remarks>
    [Fact]
    public async Task Eine_Inaktive_Firma_Wird_Ausgeblendet_Aber_Gezaehlt()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("Altlast");

        Assert.Empty(result.Companies);
        Assert.Equal(1, result.HiddenInactive);

        // Gefunden hat TANSS sehr wohl - das Ergebnis darf das nicht zu "nichts" verkuerzen.
        Assert.Equal(CompanySearchOutcome.Found, result.Outcome);
        Assert.Equal(1, result.TotalFound);
        Assert.Contains("inaktiv", result.Explanation, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("ausgeblendet", result.Explanation, StringComparison.Ordinal);

        // Ausgeblendet ist nicht gekuerzt: Ein laengerer Suchbegriff aendert hier nichts.
        Assert.False(result.Truncated);
    }

    /// <summary>
    /// Neben aktiven Treffern wird die ausgeblendete Zahl angehängt, nicht unterschlagen.
    /// </summary>
    [Fact]
    public async Task Neben_Aktiven_Treffern_Wird_Die_Ausgeblendete_Zahl_Genannt()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        // "a" steckt in "Altlast GmbH" wie in den uebrigen Saetzen des Musters; gesucht wird
        // ein Begriff, der beides trifft.
        CompanySearchResult result = await repository.SearchAsync("GmbH");

        Assert.DoesNotContain(result.Companies, company => company.Inactive);

        if (result.HiddenInactive > 0)
        {
            Assert.Contains("ausgeblendet", result.Explanation, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Namensdubletten_Bleiben_Unterscheidbar()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client);

        CompanySearchResult result = await repository.SearchAsync("Doppelt");

        Assert.Equal(5, result.Companies.Count);

        // Der Name allein taugt nicht - die Zeile mit Kundennummer und Ort schon.
        Assert.Single(result.Companies.Select(company => company.Name).Distinct(StringComparer.Ordinal));
        Assert.Equal(5, result.Companies.Select(company => company.ToString())
            .Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public async Task Gekuerzt_Wird_Hier_Und_Die_Gesamtzahl_Bleibt_Erhalten()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client, CompanyRepositoryDefaults.Threshold,
                                           displayLimit: 2);

        CompanySearchResult result = await repository.SearchAsync("Doppelt");

        Assert.Equal(2, result.Companies.Count);
        Assert.Equal(5, result.TotalFound);
        Assert.True(result.Truncated);
        Assert.Contains("5 Firmen gefunden", result.Explanation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Der_Genaue_Name_Steht_Vorn_Damit_Das_Kuerzen_Ihn_Nicht_Abschneidet()
    {
        FakeTanss tanss = new(Sample);
        using TanssClient client = TestEnvironment.Client(tanss.Handler);
        CompanyRepository repository = new(client, CompanyRepositoryDefaults.Threshold,
                                           displayLimit: 1);

        CompanySearchResult result = await repository.SearchAsync("Adler");

        Company first = Assert.Single(result.Companies);
        Assert.Equal("Adler", first.Name);
    }

    /// <summary>Die Beispielfirmen, an denen alle Fälle hängen.</summary>
    /// <remarks>
    /// Nachgebaut nach den gemessenen Eigenheiten: Dubletten mit gleichem Namen, eine inaktive und
    /// eine gesperrte Firma, und genug „GmbH“-Namen, um eine Schwelle zu überschreiten.
    /// </remarks>
    private static IReadOnlyList<Company> Sample { get; } =
    [
        new() { Id = 1, Name = "ProNet Systems GmbH", DisplayId = "PRO-1", PostCode = "55743", City = "Idar-Oberstein" },
        new() { Id = 2, Name = "Prometheus GmbH", DisplayId = "PRM-1", PostCode = "10115", City = "Berlin" },
        new() { Id = 3, Name = "Adler", DisplayId = "ADL-1", PostCode = "20095", City = "Hamburg" },
        new() { Id = 4, Name = "Adlershof Technik GmbH", DisplayId = "ADT-1", PostCode = "12489", City = "Berlin" },
        new() { Id = 5, Name = "Sperrhaus GmbH", DisplayId = "SPH-1", PostCode = "50667", City = "Köln", Lockout = true },
        new() { Id = 6, Name = "Altlast GmbH", DisplayId = "ALT-1", PostCode = "04109", City = "Leipzig", Inactive = true },
        new() { Id = 7, Name = "Doppelt GmbH", DisplayId = "DPL-1", PostCode = "80331", City = "München" },
        new() { Id = 8, Name = "Doppelt GmbH", DisplayId = "DPL-2", PostCode = "90402", City = "Nürnberg" },
        new() { Id = 9, Name = "Doppelt GmbH", DisplayId = "DPL-3", PostCode = "70173", City = "Stuttgart" },
        new() { Id = 10, Name = "Doppelt GmbH", DisplayId = "DPL-4", PostCode = "60311", City = "Frankfurt" },
        new() { Id = 11, Name = "Doppelt GmbH", DisplayId = "DPL-5", PostCode = "01067", City = "Dresden" },
    ];

    /// <summary>
    /// Eine Attrappe von <c>PUT /api/v1/search</c> — mit der <b>Schwellenlogik</b> von TANSS.
    /// </summary>
    /// <remarks>
    /// Der springende Punkt steht in <c>Answer</c>: Übersteigt die Trefferzahl
    /// <c>maxResults</c>, kommt eine leere Liste statt einer gekürzten zurück. Nachgemessen an
    /// einer Instanz der Version 10.10.0 mit 540 Treffern zu „Gmb“.
    /// </remarks>
    private sealed class FakeTanss
    {
        /// <summary>Einmal gebaut - der Analysator verbietet eine Instanz je Aufruf.</summary>
        private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

        private readonly IReadOnlyList<Company> _companies;
        private readonly int _failFromCall;
        private readonly List<string> _queries = [];

        public FakeTanss(IReadOnlyList<Company> companies, int failFromCall = 0)
        {
            _companies = companies;
            _failFromCall = failFromCall;
            Handler = new RecordingHandler(Answer);
        }

        public RecordingHandler Handler { get; }

        /// <summary>Die Suchbegriffe in der Reihenfolge, in der sie ankamen.</summary>
        public IReadOnlyList<string> Queries
        {
            get
            {
                lock (_queries)
                {
                    return [.. _queries];
                }
            }
        }

        private HttpResponseMessage Answer(CapturedRequest request)
        {
            if (_failFromCall > 0 && request.Attempt >= _failFromCall)
            {
                return RecordingHandler.Respond(HttpStatusCode.InternalServerError,
                                                TestEnvironment.Error("BOOM"));
            }

            using JsonDocument body = JsonDocument.Parse(request.Body!);
            string query = body.RootElement.GetProperty("query").GetString()!;
            int max = body.RootElement.GetProperty("configs").GetProperty("company")
                .GetProperty("maxResults").GetInt32();

            lock (_queries)
            {
                _queries.Add(query);
            }

            List<Company> hits = [.. _companies.Where(company =>
                company.Name.Contains(query, StringComparison.OrdinalIgnoreCase))];

            // DIE Eigenheit: ueber der Schwelle kommt nichts, nicht etwa eine gekuerzte Liste.
            if (hits.Count > max)
            {
                hits = [];
            }

            string companies = string.Join(",", hits.Select(Serialize));
            return RecordingHandler.Respond(
                HttpStatusCode.OK,
                TestEnvironment.Envelope($$"""{"companies":[{{companies}}],"employees":[],"tickets":[]}"""));
        }

        private static string Serialize(Company company) => JsonSerializer.Serialize(new
        {
            id = company.Id,
            name = company.Name,
            street = company.Street,
            postCode = company.PostCode,
            city = company.City,
            country = "Deutschland",
            displayId = company.DisplayId,
            phoneNumber = "06781/0",
            faxNumber = (string?)null,
            email = "info@example.invalid",
            website = (string?)null,
            inactive = company.Inactive,
            lockout = company.Lockout,
            centralType = "NONE",
            personalCustomer = false,
            mobileNumber = (string?)null,
            privateNumber = (string?)null,
            types = Array.Empty<object>(),
            anticipatedCallbacks = Array.Empty<object>(),
        }, Web);
    }

}
