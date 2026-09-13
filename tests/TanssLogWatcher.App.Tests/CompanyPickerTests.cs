using System.Globalization;
using System.Runtime.Versioning;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.ViewModels;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Firmenauswahl — und vor allem die eine Unwahrheit, die sie nicht sagen darf.
/// </summary>
/// <remarks>
/// <para><b>Worum es hier geht.</b> <c>maxResults</c> der TANSS-Suche ist nachgemessen eine
/// <i>Schwelle</i> und keine Begrenzung: Übersteigt die Trefferzahl den Wert, kommt eine leere
/// Liste statt einer gekürzten (<c>Gmb</c> hat 540 Treffer; mit 539 kommen null, mit 540 kommen
/// 540). Eine leere Antwort heißt deshalb entweder „es gibt nichts“ oder „es sind zu viele“.
/// <see cref="ICompanyRepository"/> trennt beides, so weit es sich beweisen lässt — und die
/// Auswahl darf diese Trennung nicht wieder einreißen.</para>
///
/// <para><b>Warum das ohne Oberfläche prüfbar ist.</b> Der ganze Zustand steht im
/// Ansichtsmodell und nicht im Fenster, und <see cref="CompanyPickerViewModel.SearchNowAsync"/>
/// ist derselbe Weg, den auch die Entprellung geht. Ein Test braucht deshalb weder eine
/// Nachrichtenschleife noch eine Wartezeit. Läge die Entprellung in der <c>xaml.cs</c>, hinge
/// jede dieser Zusagen an der Aufmerksamkeit dessen, der sie liest — und genau so sind in diesem
/// Werkzeug schon drei dokumentierte Zusagen ungebaut geblieben.</para>
///
/// <para><b>Kein Netz ist im Spiel.</b> Die Attrappe antwortet aus dem Gedächtnis und zählt
/// dabei mit, wie oft sie gefragt wurde — das ist der Nachweis der Entprellung.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class CompanyPickerTests
{
    // ---- Entprellen -------------------------------------------------------------------

    /// <summary>
    /// Der Nachweis der Entprellung: Tippen allein fragt nicht.
    /// </summary>
    /// <remarks>
    /// Ohne sie schickte „Mustermann“ zehn Anfragen los, von denen neun schon überholt sind,
    /// wenn sie ankommen. Geprüft wird am Zählwerk der Attrappe und nicht an einer Stoppuhr:
    /// Eine Zusage, die an einer Wartezeit hängt, scheitert auf einem langsamen Bauläufer.
    /// </remarks>
    [Fact]
    public void Tippen_fragt_TANSS_noch_nicht()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "P";
        modell.Query = "Pr";
        modell.Query = "Pro";
        modell.Query = "ProN";

        Assert.Equal(0, suche.Calls);
        Assert.Equal(CompanyPickerState.Pending, modell.State);
        Assert.True(modell.IsWorking);
    }

    /// <summary>Vier Tastendrücke, eine Anfrage — und die trägt den ganzen Begriff.</summary>
    [Fact]
    public async Task Vier_Tastendruecke_ergeben_eine_einzige_Anfrage()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "P";
        modell.Query = "Pr";
        modell.Query = "Pro";
        modell.Query = "ProN";

        await modell.SearchNowAsync();

        Assert.Equal(1, suche.Calls);
        Assert.Equal("ProN", Assert.Single(suche.Queries));
    }

    // ---- Unter drei Zeichen -----------------------------------------------------------

    /// <summary>
    /// Unter drei Zeichen wird gar nicht gefragt — und die Auswahl sagt das ausdrücklich.
    /// </summary>
    /// <remarks>
    /// „Keine Firma gefunden“ wäre hier die bequemste und zugleich falscheste Antwort: Es wurde
    /// niemand gefragt. Der Text muss das benennen, sonst sucht der Techniker nach einer Firma,
    /// die nie gesucht wurde.
    /// </remarks>
    [Fact]
    public void Unter_drei_Zeichen_wird_nicht_gefragt_und_die_Auswahl_sagt_es()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pr";

        Assert.Equal(0, suche.Calls);
        Assert.Equal(CompanyPickerState.TooShort, modell.State);
        Assert.Contains("nicht gefragt", modell.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("keine Firma gefunden", modell.Message,
                              StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Auch der ausdrückliche Befehl fragt unter drei Zeichen nicht.</summary>
    [Fact]
    public async Task Auch_die_Eingabetaste_fragt_unter_drei_Zeichen_nicht()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pr";
        await modell.SearchNowAsync();

        Assert.Equal(0, suche.Calls);
        Assert.Equal(CompanyPickerState.TooShort, modell.State);
    }

    // ---- Die Zeile ---------------------------------------------------------------------

    /// <summary>
    /// Namensdubletten sind gemessen der Normalfall; die Zeile muss sie auseinanderhalten.
    /// </summary>
    /// <remarks>
    /// Nachgemessen stand derselbe Firmenname <b>fünfmal</b> mit verschiedenen Kennungen in einer
    /// einzigen Trefferliste. Eine Auswahl, die nur den Namen zeigt, ist damit unbrauchbar — und
    /// eine auf die falsche Firma gebuchte Leistung fällt niemandem auf.
    /// </remarks>
    [Fact]
    public async Task Die_Zeile_zeigt_Name_Kundennummer_und_Ort()
    {
        FakeCompanies suche = new(Treffer(
            Firma(1, "Müller GmbH", nummer: "MUE-1000", plz: "55743", ort: "Idar-Oberstein"),
            Firma(2, "Müller GmbH", nummer: "MUE-2000", plz: "10115", ort: "Berlin")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Müller";
        await modell.SearchNowAsync();

        Assert.Equal(2, modell.Companies.Count);

        CompanyRow erste = modell.Companies[0];
        CompanyRow zweite = modell.Companies[1];

        Assert.Equal(erste.Name, zweite.Name);
        Assert.NotEqual(erste.CustomerNumber, zweite.CustomerNumber);
        Assert.NotEqual(erste.Place, zweite.Place);
        Assert.Equal("MUE-1000", erste.CustomerNumber);
        Assert.Equal("55743 Idar-Oberstein", erste.Place);
    }

    /// <summary>Fehlt eine Angabe, steht der Hinweis darauf da und kein leeres Feld.</summary>
    /// <remarks>
    /// Hausregel 2 in klein: „ohne Kundennummer“ ist eine Auskunft. Ein leeres Feld ist
    /// mehrdeutig — fehlt der Wert, oder hat die Anzeige ihn verschluckt?
    /// </remarks>
    [Fact]
    public async Task Fehlende_Angaben_werden_benannt_und_nicht_verschwiegen()
    {
        FakeCompanies suche = new(Treffer(Firma(7, "Ohne alles", nummer: null, plz: null, ort: null)));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Ohne";
        await modell.SearchNowAsync();

        CompanyRow zeile = Assert.Single(modell.Companies);

        Assert.Equal("ohne Kundennummer", zeile.CustomerNumber);
        Assert.Equal("ohne Ort", zeile.Place);
        Assert.False(zeile.HasCustomerNumber);
        Assert.False(zeile.HasPlace);
    }

    // ---- Inaktiv und gesperrt ----------------------------------------------------------

    /// <summary>
    /// Die Auswahl zeigt, was sie bekommt — das Ausblenden geschieht eine Schicht tiefer.
    /// </summary>
    /// <remarks>
    /// <para>Gemessen kommen inaktive Firmen in der Antwort von TANSS mit: 5 von 11 Treffern
    /// trugen <c>inactive=true</c>. Ausgeblendet werden sie im <c>CompanyRepository</c>, weil
    /// dort auch der Satz darüber entsteht — filterte die Auswahl selbst, stünden sechs Zeilen
    /// unter einem Satz, der von elf redet.</para>
    /// <para>Dieser Test hält deshalb <b>nicht</b> mehr fest, dass eine inaktive Zeile in der
    /// Liste erscheint — im Betrieb tut sie das nicht. Er hält fest, dass die Auswahl eine
    /// vorgelegte Zeile unverändert durchreicht und nicht heimlich ein zweites Mal filtert.
    /// Zwei Filter an zwei Stellen wären zwei Wahrheiten.</para>
    /// </remarks>
    [Fact]
    public async Task Eine_vorgelegte_Zeile_wird_unveraendert_durchgereicht()
    {
        FakeCompanies suche = new(Treffer(Firma(3, "Alte Firma", inaktiv: true)));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Alte";
        await modell.SearchNowAsync();

        CompanyRow zeile = Assert.Single(modell.Companies);

        Assert.True(zeile.IsInactive);
        Assert.Equal("inaktiv", zeile.StatusTag);
        Assert.True(zeile.IsSelectable);

        modell.SelectCommand.Execute(zeile);

        Assert.Same(zeile, modell.Selected);
        Assert.Equal(3, modell.SelectedCompany?.Id);
    }

    /// <summary>
    /// Eine gesperrte Firma kommt in die Liste, aber nicht in die Auswahl.
    /// </summary>
    /// <remarks>
    /// <para>Die Beschreibung sagt zu <c>lockout</c> wörtlich „no service may be entered“. Sie
    /// wegzulassen wäre trotzdem falsch: Wer eine gesperrte Firma sucht, soll erfahren, <i>dass</i>
    /// sie gesperrt ist, und nicht „nicht gefunden“ lesen.</para>
    /// <para><b>Die Prüfung sitzt im Setzer und nicht in der Vorlage.</b> Eine Schaltfläche mit
    /// <c>IsEnabled</c> hält den Mausklick auf; sie hält keine Tastaturauswahl auf, keinen
    /// Bindungsfehler und keinen späteren Aufrufer. Deshalb wird hier die Zuweisung selbst
    /// geprüft.</para>
    /// </remarks>
    [Fact]
    public async Task Eine_gesperrte_Firma_ist_nicht_waehlbar_und_sagt_warum()
    {
        FakeCompanies suche = new(Treffer(Firma(4, "Gesperrte GmbH", gesperrt: true)));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Gesperrt";
        await modell.SearchNowAsync();

        CompanyRow zeile = Assert.Single(modell.Companies);
        Assert.False(zeile.IsSelectable);
        Assert.Equal("gesperrt", zeile.StatusTag);

        modell.Selected = zeile;

        Assert.Null(modell.Selected);
        Assert.Null(modell.SelectedCompany);
        Assert.False(modell.HasSelection);
        Assert.True(modell.HasSelectionProblem);
        Assert.Contains("gesperrt", modell.SelectionProblem!, StringComparison.Ordinal);
        Assert.Contains("Gesperrte GmbH", modell.SelectionProblem!, StringComparison.Ordinal);
    }

    // ---- Der wichtigste Fall: leere Antwort ist nicht gleich "gibt es nicht" -------------

    /// <summary>
    /// Eine leere Antwort ohne Beweis heißt <b>nicht ermittelt</b> — und niemals „keine Firma“.
    /// </summary>
    /// <remarks>
    /// Das ist der Fall, für den dieser ganze Vertrag gebaut wurde: Der Suchbegriff <c>Gmb</c>
    /// hat 540 Treffer, und eine zu niedrig angesetzte Schwelle beantwortet ihn mit einer leeren
    /// Liste. „Keine Firma gefunden“ wäre hier eine Unwahrheit über 540 Firmen.
    /// </remarks>
    [Fact]
    public async Task Eine_unklare_leere_Antwort_wird_nicht_zu_keine_Firma()
    {
        const string satz = "Zu „Gmb“ hat TANSS nichts geliefert, auch nicht mit einer "
            + "Ergebnisgrenze von 2000000. Ob es keine Firma gibt oder zu viele, ist damit nicht "
            + "ermittelt — bitte den Suchbegriff verlängern.";

        FakeCompanies suche = new(_ => new CompanySearchResult
        {
            Outcome = CompanySearchOutcome.Undetermined,
            Explanation = satz,
        });
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Gmb";
        await modell.SearchNowAsync();

        Assert.Equal(CompanyPickerState.Undetermined, modell.State);
        Assert.True(modell.IsUncertain);
        Assert.False(modell.HasResults);

        // WOERTLICH. Ein selbstgebauter Text an dieser Stelle waere genau die Luege, die der
        // Vertrag verhindert.
        Assert.Equal(satz, modell.Message);
        Assert.DoesNotContain("keine Firma gefunden", modell.Message,
                              StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Nachgewiesen leer heißt dagegen sehr wohl „es gibt keine“ — und ist nicht unsicher.
    /// </summary>
    /// <remarks>
    /// Der Unterschied zum Fall darüber ist der Beweis, den die Suche über eine Obermengen-
    /// Gegenprobe führt. Beide Fälle gleich zu behandeln wäre die andere Hälfte desselben
    /// Fehlers: Dann sagte die Auswahl nie etwas Verbindliches.
    /// </remarks>
    [Fact]
    public async Task Eine_belegte_leere_Antwort_darf_keine_Firma_sagen()
    {
        const string satz = "Keine Firma enthält „xyzq“. Gegenprobe: „xyz“ ergibt 4 Firmen — die "
            + "leere Antwort liegt also nicht an der Ergebnisgrenze.";

        FakeCompanies suche = new(_ => new CompanySearchResult
        {
            Outcome = CompanySearchOutcome.NoMatch,
            Explanation = satz,
        });
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "xyzq";
        await modell.SearchNowAsync();

        Assert.Equal(CompanyPickerState.NoMatch, modell.State);
        Assert.False(modell.IsUncertain);
        Assert.Equal(satz, modell.Message);
        Assert.Empty(modell.Companies);
    }

    /// <summary>Bei Treffern steht der Satz der Suche da, samt seiner Zusätze.</summary>
    [Fact]
    public async Task Bei_Treffern_steht_der_Satz_der_Suche_da()
    {
        const string satz = "540 Firmen gefunden, die ersten 200 werden angezeigt. Suchbegriff "
            + "verlängern, um die Liste zu verkleinern.";

        FakeCompanies suche = new(_ => new CompanySearchResult
        {
            Outcome = CompanySearchOutcome.Found,
            Companies = [Firma(1, "Erste GmbH")],
            TotalFound = 540,
            Explanation = satz,
        });
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Gmb";
        await modell.SearchNowAsync();

        Assert.Equal(CompanyPickerState.Found, modell.State);
        Assert.True(modell.HasResults);
        Assert.Equal(satz, modell.Message);
    }

    // ---- Netzfehler --------------------------------------------------------------------

    /// <summary>
    /// Ein Netzfehler wird gesagt und sperrt nichts.
    /// </summary>
    /// <remarks>
    /// Hausregel 5: Ein Fehler kostet den Vorgang, nie den Dienst. Das Feld behält seinen Inhalt,
    /// der Suchbefehl bleibt ausführbar, und im Text steht ausdrücklich, dass damit <b>nichts</b>
    /// über die Firma feststeht — ein Fehlschlag ist kein „gibt es nicht“.
    /// </remarks>
    [Fact]
    public async Task Ein_Netzfehler_wird_gesagt_und_sperrt_nicht()
    {
        FakeCompanies suche = new(_ =>
            throw new TanssUnreachableException("TANSS ist nicht erreichbar."));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pro";
        await modell.SearchNowAsync();

        Assert.Equal(CompanyPickerState.Failed, modell.State);
        Assert.True(modell.IsUncertain);
        Assert.Contains("TANSS ist nicht erreichbar.", modell.Message, StringComparison.Ordinal);
        Assert.Contains("nicht ermittelt", modell.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("keine Firma gefunden", modell.Message,
                              StringComparison.OrdinalIgnoreCase);

        // Nichts ist gesperrt: derselbe Begriff steht noch da, und der Befehl geht wieder.
        Assert.Equal("Pro", modell.Query);
        Assert.True(modell.SearchCommand.CanExecute(null));
        Assert.True(modell.ClearCommand.CanExecute(null));
    }

    /// <summary>Nach einem Fehlschlag führt derselbe Befehl zu einem neuen Versuch.</summary>
    [Fact]
    public async Task Nach_einem_Fehlschlag_geht_ein_zweiter_Versuch()
    {
        int versuche = 0;
        FakeCompanies suche = new(_ =>
        {
            versuche++;
            return versuche == 1
                ? throw new TanssUnreachableException("Zeitüberschreitung.")
                : new CompanySearchResult
                {
                    Outcome = CompanySearchOutcome.Found,
                    Companies = [Firma(9, "Zweiter Anlauf GmbH")],
                    TotalFound = 1,
                    Explanation = "1 Firmen gefunden.",
                };
        });
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Zwei";
        await modell.SearchNowAsync();
        Assert.Equal(CompanyPickerState.Failed, modell.State);

        await modell.SearchNowAsync();

        Assert.Equal(CompanyPickerState.Found, modell.State);
        Assert.Single(modell.Companies);
    }

    // ---- Auswahl und Eingabe bleiben zusammen -------------------------------------------

    /// <summary>
    /// Wer weitertippt, hat nichts mehr gewählt.
    /// </summary>
    /// <remarks>
    /// Eine Auswahl, die nicht mehr zu dem passt, was im Feld steht, behauptet etwas Falsches —
    /// und zwar genau in dem Augenblick, in dem gebucht wird.
    /// </remarks>
    [Fact]
    public async Task Weitertippen_nimmt_die_Wahl_zurueck()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pro";
        await modell.SearchNowAsync();
        modell.SelectCommand.Execute(modell.Companies[0]);
        Assert.True(modell.HasSelection);

        modell.Query = "Pron";

        Assert.False(modell.HasSelection);
        Assert.Null(modell.SelectedCompany);
    }

    /// <summary>
    /// Steht die gewählte Firma auch im neuen Treffersatz, bleibt sie gewählt.
    /// </summary>
    /// <remarks>
    /// Sie stillschweigend fallen zu lassen hieße, beim Buchen ohne Firma dazustehen — obwohl
    /// dieselbe Firma noch in der Liste steht.
    /// </remarks>
    [Fact]
    public async Task Eine_noch_vorhandene_Firma_bleibt_ueber_eine_neue_Suche_gewaehlt()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems"), Firma(2, "Pronto GmbH")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pro";
        await modell.SearchNowAsync();
        modell.SelectCommand.Execute(modell.Companies[0]);

        // Nicht ueber Query gehen: Das nimmt die Wahl bewusst zurueck. Hier wird der Fall
        // geprueft, dass DIESELBE Eingabe noch einmal gesucht wird - etwa ueber "Erneut suchen".
        await modell.SearchNowAsync();

        Assert.True(modell.HasSelection);
        Assert.Equal(1, modell.SelectedCompany?.Id);
        Assert.True(modell.Companies[0].IsChosen);
        Assert.False(modell.Companies[1].IsChosen);
    }

    /// <summary>Die Marke „gewählt“ sitzt auf genau einer Zeile.</summary>
    [Fact]
    public async Task Die_Marke_sitzt_auf_genau_einer_Zeile()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "Erste GmbH"), Firma(2, "Zweite GmbH")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "GmbH";
        await modell.SearchNowAsync();

        modell.SelectCommand.Execute(modell.Companies[0]);
        Assert.True(modell.Companies[0].IsChosen);
        Assert.False(modell.Companies[1].IsChosen);

        modell.SelectCommand.Execute(modell.Companies[1]);
        Assert.False(modell.Companies[0].IsChosen);
        Assert.True(modell.Companies[1].IsChosen);
    }

    /// <summary>Zurücksetzen leert Feld, Liste, Auswahl und Meldung.</summary>
    [Fact]
    public async Task Zuruecksetzen_raeumt_alles_ab()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pro";
        await modell.SearchNowAsync();
        modell.SelectCommand.Execute(modell.Companies[0]);

        modell.ClearCommand.Execute(null);

        Assert.Equal(string.Empty, modell.Query);
        Assert.Empty(modell.Companies);
        Assert.False(modell.HasSelection);
        Assert.Equal(CompanyPickerState.Idle, modell.State);
    }

    // ---- Ohne Einrichtung ---------------------------------------------------------------

    /// <summary>
    /// Ohne Verbindung wird nichts behauptet.
    /// </summary>
    /// <remarks>
    /// Eine leere Liste ohne Erklärung sähe aus wie „keine Firma“, obwohl niemand gefragt wurde.
    /// Das ist derselbe Fehler wie bei der zu kurzen Eingabe, nur mit anderer Ursache.
    /// </remarks>
    [Fact]
    public async Task Ohne_eingerichtete_Suche_wird_nichts_behauptet()
    {
        using CompanyPickerViewModel modell = new(companies: null);

        Assert.Equal(CompanyPickerState.Unavailable, modell.State);

        modell.Query = "ProNet";
        await modell.SearchNowAsync();

        Assert.Equal(CompanyPickerState.Unavailable, modell.State);
        Assert.Empty(modell.Companies);
        Assert.DoesNotContain("keine Firma gefunden", modell.Message,
                              StringComparison.OrdinalIgnoreCase);
        Assert.Contains("nicht ermittelt", modell.Message, StringComparison.Ordinal);
    }

    /// <summary>Die Mindestlänge wird nicht zweitverwaltet.</summary>
    /// <remarks>
    /// Eine zweite Zahl liefe irgendwann auseinander, und dann sagte die Oberfläche „ab drei“,
    /// während die Suche vier verlangt.
    /// </remarks>
    [Fact]
    public void Die_Mindestlaenge_kommt_aus_dem_Vertrag()
    {
        Assert.Equal(CompanyRepositoryDefaults.MinimumQueryLength,
                     CompanyPickerViewModel.MinimumQueryLength);
    }

    // ---- Beschreibbares Auswahlfeld: Text ist nicht Auswahl -----------------------------

    /// <summary>
    /// Getippter Text ist <b>keine</b> gewählte Firma — auch dann nicht, wenn er genau passt.
    /// </summary>
    /// <remarks>
    /// <para>Der Fall, den das beschreibbare Auswahlfeld mitbringt: Es hat einen Text und eine
    /// Auswahl, und beide sehen im Feld gleich aus. Gälte der Text als Auswahl, bucht jemand auf
    /// eine Firma, die er nur angetippt hat — bei fünf gleichnamigen Firmen auf die falsche, und
    /// es fällt niemandem auf.</para>
    /// <para>Deshalb sind es hier zwei Eigenschaften: <c>Query</c> ist Text,
    /// <c>Picked</c>/<c>Selected</c> ist die Wahl. Getippt wird nur das eine.</para>
    /// </remarks>
    [Fact]
    public async Task Getippter_Text_gilt_nicht_als_gewaehlte_Firma()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        // Woertlich der Name der einzigen Firma - naeher kann eine Eingabe nicht herankommen.
        modell.Query = "ProNet Systems";
        await modell.SearchNowAsync();

        Assert.Single(modell.Companies);
        Assert.Equal("ProNet Systems", modell.Companies[0].Name);

        Assert.False(modell.HasSelection);
        Assert.Null(modell.Selected);
        Assert.Null(modell.SelectedCompany);
        Assert.Null(modell.Picked);
    }

    /// <summary>
    /// Die Wahl schreibt die Firma ins Feld — und löst dabei weder Suche noch Rücknahme aus.
    /// </summary>
    /// <remarks>
    /// Ginge der Name über denselben Weg wie ein Tastendruck, nähme er die eben getroffene Wahl
    /// im selben Atemzug wieder zurück und schickte eine Suche nach dem eigenen Namen los.
    /// </remarks>
    [Fact]
    public async Task Die_Wahl_steht_danach_im_Feld_und_sucht_nicht_neu()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "Müller GmbH")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Mül";
        await modell.SearchNowAsync();
        int gefragt = suche.Calls;

        modell.SelectCommand.Execute(modell.Companies[0]);

        Assert.Equal("Müller GmbH", modell.Query);
        Assert.True(modell.HasSelection);
        Assert.Equal(1, modell.SelectedCompany?.Id);

        // Weder eine neue Anfrage noch ein Rückfall in "es wird gesucht".
        Assert.Equal(gefragt, suche.Calls);
        Assert.Equal(CompanyPickerState.Found, modell.State);
    }

    /// <summary>
    /// Ohne Namen steht die Kennung im Feld — nicht nichts.
    /// </summary>
    /// <remarks>
    /// Die am Gerät erkannte Firma kommt ohne Namen an; TANSS liefert ihn erst mit der
    /// Ticketabfrage nach. Ein leeres Feld sähe in genau diesem Augenblick aus wie „keine Firma
    /// gewählt“, obwohl eine feststeht.
    /// </remarks>
    [Fact]
    public void Eine_Firma_ohne_Namen_zeigt_ihre_Kennung()
    {
        FakeCompanies suche = new(Treffer());
        using CompanyPickerViewModel modell = new(suche);

        modell.Selected = new CompanyRow(new Company { Id = 4711, Name = string.Empty });

        Assert.True(modell.HasSelection);
        Assert.Contains("4711", modell.Query, StringComparison.Ordinal);
    }

    /// <summary>
    /// Das Feld darf den Suchbegriff nicht wegwerfen, wenn seine Zeilen getauscht werden.
    /// </summary>
    /// <remarks>
    /// <para>Ein beschreibbares Auswahlfeld schreibt den Text seiner Markierung ins Feld. Fällt
    /// die Markierung weg — und das tut sie bei jedem Zeilentausch —, schreibt es einen
    /// <b>leeren</b> Text zurück. Der käme als „der Benutzer hat das Feld geräumt“ an: Begriff
    /// weg, Treffer weg, Zustand <c>Idle</c>, und zwar im selben Augenblick, in dem die Antwort
    /// eintrifft.</para>
    /// <para>Der Handgriff am <c>CollectionChanged</c> ist genau das, was das Feld tut. Ohne das
    /// Festhalten des Begriffs steht am Ende eine leere Auswahl da, die so aussieht, als hätte
    /// niemand gesucht.</para>
    /// </remarks>
    [Fact]
    public async Task Der_Begriff_bleibt_stehen_wenn_das_Feld_ihn_beim_Zeilentausch_wegwirft()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "Müller GmbH"), Firma(2, "Müller & Co")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Müller";

        // Ab hier benimmt sich die Liste wie das Auswahlfeld: Jeder Zeilentausch raeumt das
        // Textfeld.
        modell.Companies.CollectionChanged += (_, _) => modell.Query = string.Empty;

        await modell.SearchNowAsync();

        Assert.Equal("Müller", modell.Query);
        Assert.Equal(CompanyPickerState.Found, modell.State);
        Assert.Equal(2, modell.Companies.Count);
        Assert.True(modell.HasResults);
    }

    // ---- Beschreibbares Auswahlfeld: die Markierung -------------------------------------

    /// <summary>
    /// Die Markierung des Feldes wählt — über dieselbe Prüfung wie jeder andere Weg.
    /// </summary>
    [Fact]
    public async Task Die_Markierung_des_Feldes_waehlt()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pro";
        await modell.SearchNowAsync();

        modell.Picked = modell.Companies[0];

        Assert.True(modell.HasSelection);
        Assert.Equal(1, modell.SelectedCompany?.Id);
        Assert.True(modell.Companies[0].IsChosen);
    }

    /// <summary>
    /// Verliert das Feld seine Markierung, ist das <b>keine</b> Rücknahme der Wahl.
    /// </summary>
    /// <remarks>
    /// Das Feld meldet „nichts mehr markiert“ bei jedem Zeilentausch — also bei jeder neuen
    /// Suche. Würde das als Rücknahme gelten, stünde der Techniker nach einer Suche, die er
    /// nebenbei ausgelöst hat, ohne Firma da und sähe es nicht.
    /// </remarks>
    [Fact]
    public async Task Eine_weggefallene_Markierung_nimmt_die_Wahl_nicht_zurueck()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "ProNet Systems")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pro";
        await modell.SearchNowAsync();
        modell.Picked = modell.Companies[0];
        Assert.True(modell.HasSelection);

        modell.Picked = null;

        Assert.True(modell.HasSelection);
        Assert.Equal(1, modell.SelectedCompany?.Id);
    }

    /// <summary>
    /// Eine gesperrte Zeile bleibt auch über das Auswahlfeld abgewiesen — und das Feld behält
    /// den Begriff.
    /// </summary>
    /// <remarks>
    /// Die Vorlage schaltet die gesperrte Zeile ab; das hält den Mausklick auf. Diese Prüfung
    /// hält alles andere auf. Zusätzlich darf die abgewiesene Firma nicht als Markierung des
    /// Feldes stehenbleiben: Das Feld zeigte sonst eine Firma an, auf die nicht gebucht wird.
    /// </remarks>
    [Fact]
    public async Task Eine_gesperrte_Zeile_wird_auch_ueber_das_Feld_abgewiesen()
    {
        FakeCompanies suche = new(Treffer(Firma(4, "Gesperrte GmbH", gesperrt: true)));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Gesperrt";
        await modell.SearchNowAsync();

        modell.Picked = Assert.Single(modell.Companies);

        Assert.Null(modell.Picked);
        Assert.Null(modell.Selected);
        Assert.False(modell.HasSelection);
        Assert.True(modell.HasSelectionProblem);
        Assert.Equal("Gesperrt", modell.Query);
    }

    // ---- Die Aufklappliste ---------------------------------------------------------------

    /// <summary>
    /// Treffer klappen die Liste auf; alles andere klappt sie zu.
    /// </summary>
    /// <remarks>
    /// Die Treffer stehen in der Aufklappliste des Feldes. Ginge sie nicht von selbst auf, wären
    /// sie da und trotzdem unsichtbar. Umgekehrt wäre eine offene, leere Liste die Behauptung
    /// „hier ist nichts“ — genau die Aussage, die eine leere Antwort <b>nicht</b> trägt.
    /// </remarks>
    [Fact]
    public async Task Treffer_klappen_die_Liste_auf_und_ein_Fehlschlag_zu()
    {
        int versuche = 0;
        FakeCompanies suche = new(_ =>
        {
            versuche++;
            return versuche == 1
                ? new CompanySearchResult
                {
                    Outcome = CompanySearchOutcome.Found,
                    Companies = [Firma(1, "ProNet Systems")],
                    TotalFound = 1,
                    Explanation = "1 Firmen gefunden.",
                }
                : throw new TanssUnreachableException("TANSS ist nicht erreichbar.");
        });
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pro";
        await modell.SearchNowAsync();
        Assert.True(modell.IsDropDownOpen);

        await modell.SearchNowAsync();
        Assert.False(modell.IsDropDownOpen);
    }

    // ---- Die Meldezeile: nur, wenn es etwas zu melden gibt -------------------------------

    /// <summary>
    /// Eine vollzählige Trefferliste hat nichts zu melden.
    /// </summary>
    /// <remarks>
    /// „2 Firmen gefunden“ steht schon in der Aufklappliste; zweimal dasselbe ist keine
    /// Auskunft, kostet aber zwei Zeilen im Fenster.
    /// </remarks>
    [Fact]
    public async Task Eine_vollzaehlige_Trefferliste_meldet_nichts()
    {
        FakeCompanies suche = new(Treffer(Firma(1, "Erste GmbH"), Firma(2, "Zweite GmbH")));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "GmbH";
        await modell.SearchNowAsync();

        Assert.Equal(CompanyPickerState.Found, modell.State);
        Assert.True(modell.HasResults);
        Assert.False(modell.HasMessage);
    }

    /// <summary>
    /// Eine <b>gekürzte</b> Trefferliste meldet sehr wohl — sonst hielte der Techniker die
    /// ersten 200 für alle.
    /// </summary>
    [Fact]
    public async Task Eine_gekuerzte_Trefferliste_meldet_es()
    {
        FakeCompanies suche = new(_ => new CompanySearchResult
        {
            Outcome = CompanySearchOutcome.Found,
            Companies = [Firma(1, "Erste GmbH")],
            TotalFound = 540,
            Explanation = "540 Firmen gefunden, die ersten 200 werden angezeigt.",
        });
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Gmb";
        await modell.SearchNowAsync();

        Assert.True(modell.HasMessage);
        Assert.Contains("540", modell.Message, StringComparison.Ordinal);
    }

    /// <summary>
    /// Die drei Fälle, in denen die Liste <b>nichts</b> beweist, melden sich immer.
    /// </summary>
    /// <remarks>
    /// Das ist die Grenze der Sparsamkeit von oben: Eine leere Aufklappliste sieht in allen
    /// dreien gleich aus. Bliebe der Satz weg, stünde dort „nichts gefunden“ — und das wäre bei
    /// „zu viele“ und bei „Netz weg“ eine Unwahrheit.
    /// </remarks>
    [Theory]
    [InlineData(CompanySearchOutcome.Undetermined)]
    [InlineData(CompanySearchOutcome.NoMatch)]
    public async Task Eine_leere_Antwort_meldet_sich_immer(CompanySearchOutcome ausgang)
    {
        const string satz = "Der Satz, der den Unterschied trägt.";

        FakeCompanies suche = new(_ => new CompanySearchResult
        {
            Outcome = ausgang,
            Explanation = satz,
        });
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Gmb";
        await modell.SearchNowAsync();

        Assert.True(modell.HasMessage);
        Assert.Equal(satz, modell.Message);
        Assert.False(modell.IsDropDownOpen);
    }

    /// <summary>Auch „zu kurz“ und „gescheitert“ melden sich.</summary>
    [Fact]
    public async Task Zu_kurz_und_gescheitert_melden_sich()
    {
        FakeCompanies suche = new(_ =>
            throw new TanssUnreachableException("TANSS ist nicht erreichbar."));
        using CompanyPickerViewModel modell = new(suche);

        modell.Query = "Pr";
        Assert.True(modell.HasMessage);

        modell.Query = "Pro";
        await modell.SearchNowAsync();
        Assert.True(modell.HasMessage);
        Assert.True(modell.IsUncertain);
    }

    // ---- Werkzeug -----------------------------------------------------------------------

    /// <summary>Eine Antwort mit Treffern, wie die Suche sie liefern würde.</summary>
    private static Func<string, CompanySearchResult> Treffer(params Company[] firmen) =>
        _ => new CompanySearchResult
        {
            Outcome = CompanySearchOutcome.Found,
            Companies = firmen,
            TotalFound = firmen.Length,
            Explanation = string.Create(CultureInfo.CurrentCulture,
                                        $"{firmen.Length} Firmen gefunden."),
        };

    /// <summary>Eine Firma, wie TANSS sie in der Suche liefert.</summary>
    private static Company Firma(int id, string name, string? nummer = "PRO-1000",
                                 string? plz = "55743", string? ort = "Idar-Oberstein",
                                 bool inaktiv = false, bool gesperrt = false) =>
        new()
        {
            Id = id,
            Name = name,
            DisplayId = nummer,
            PostCode = plz,
            City = ort,
            Inactive = inaktiv,
            Lockout = gesperrt,
        };

    /// <summary>
    /// Eine Firmensuche aus dem Gedächtnis, die mitzählt, wie oft sie gefragt wurde.
    /// </summary>
    /// <remarks>
    /// Das Zählwerk ist der ganze Punkt: Die Entprellung ist nur daran nachzuweisen, dass
    /// Tastendrücke <b>keine</b> Anfragen erzeugen. Eine Attrappe, die nur Werte liefert, könnte
    /// das nicht zeigen.
    /// </remarks>
    private sealed class FakeCompanies : ICompanyRepository
    {
        private readonly Func<string, CompanySearchResult> _answer;

        public FakeCompanies(Func<string, CompanySearchResult> answer) => _answer = answer;

        /// <summary>Jede Anfrage, in der Reihenfolge ihres Eintreffens.</summary>
        public List<string> Queries { get; } = [];

        /// <summary>Wie oft TANSS gefragt worden wäre.</summary>
        public int Calls => Queries.Count;

        public Task<CompanySearchResult> SearchAsync(string query, CancellationToken ct = default)
        {
            Queries.Add(query);
            return Task.FromResult(_answer(query));
        }
    }
}
