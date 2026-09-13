using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.App.Views;
using Xunit;

// Das Testprojekt zieht WinForms mit (echte Fenster als Aufnahmeziel), und dort heissen diese
// Typen genauso. Kurzname statt vollem Pfad an jeder Stelle: WPF ist hier gemeint, immer.
using WpfComboBox = System.Windows.Controls.ComboBox;
using WpfComboBoxItem = System.Windows.Controls.ComboBoxItem;
using WpfSize = System.Windows.Size;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Die Ansicht der Firmenauswahl wird wirklich geladen — mit allen Vorlagen.
/// </summary>
/// <remarks>
/// <para><b>Warum dieser Test da ist.</b> In diesem Werkzeug hat ein
/// <c>BasedOn="{StaticResource {x:Type ui:InfoBar}}"</c> die Navigation schon einmal
/// <b>still</b> scheitern lassen: Die Seite ging nicht auf, und es stand nichts da. Ein
/// fehlender <c>StaticResource</c> wirft beim Laden der Vorlage, nicht beim Übersetzen — ein
/// grüner Bau beweist über eine XAML-Datei also gar nichts.</para>
///
/// <para><b>Was hier tatsächlich passiert.</b> Auf dem gemeinsamen Oberflächenstrang der
/// Sammlung — dort liegen die Wörterbücher, die auch <c>App.xaml</c> zusammenlegt — wird das
/// Steuerelement gebaut, mit Treffern gefüttert und vermessen. Das baut die Zeile mit dem
/// Auswahlfeld, die Meldezeile und die Karte der gewählten Firma.</para>
///
/// <para>Die <b>Trefferzeilen</b> stehen seit dem Umbau auf ein beschreibbares Auswahlfeld in
/// dessen Aufklappliste und entstehen erst, wenn die aufgeht — dafür gibt es den zweiten Fall.
/// Ohne ihn bliebe genau der Teil ungeprüft, in dem Kundennummer, Ort und die Sperrmarke
/// stehen.</para>
///
/// <para>Ein Bildschirm wird dafür nicht gebraucht: Es wird nichts angezeigt, nur gebaut,
/// gebunden und gerechnet.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Collection(Ladefaelle.Name)]
public sealed class CompanyPickerViewLoadTests(UiThreadFixture oberflaeche)
{
    private readonly UiThreadFixture _oberflaeche = oberflaeche;

    [Fact]
    public void Die_Ansicht_laedt_samt_Trefferzeile()
    {
        int zeilen = 0;

        // Der Strang und die eine erlaubte Application gehoeren der Sammlung: Ein eigener
        // Strang je Ladefall ging sich mit dem naechsten ueber deren Woerterbuecher an, und
        // beide Faelle fielen dann WECHSELND um.
        _oberflaeche.Run(() =>
        {
            CompanyPickerViewModel modell = new(new Immer(
                Firma(1, "Müller GmbH", "MUE-1000", "55743", "Idar-Oberstein", false),
                Firma(2, "Müller GmbH", "MUE-2000", "10115", "Berlin", true)))
            {
                Query = "Müller",
            };

            // Die Attrappe antwortet aus dem Gedaechtnis; hier wartet nichts auf ein Netz.
            modell.SearchNowAsync().GetAwaiter().GetResult();
            zeilen = modell.Companies.Count;

            CompanyPicker sicht = new() { DataContext = modell };

            // Erst Messen und Anordnen baut die Vorlagen. Ohne das bliebe ein fehlender
            // StaticResource in der Zeilenvorlage unentdeckt.
            sicht.Measure(new WpfSize(800, 600));
            sicht.Arrange(new Rect(0, 0, 800, 600));
            sicht.UpdateLayout();
            Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

            modell.Dispose();
        });

        Assert.Equal(2, zeilen);
    }

    /// <summary>
    /// Die Zeilenvorlage des Auswahlfeldes entsteht wirklich — samt Stil der gesperrten Zeile.
    /// </summary>
    /// <remarks>
    /// <para><b>Warum das ein eigener Fall ist.</b> Seit die Auswahl ein beschreibbares
    /// Auswahlfeld ist, stehen die Treffer in dessen <b>Aufklappliste</b>. Messen und Anordnen
    /// des Steuerelements bauen deren Zeilen <b>nicht</b> mehr — ohne diesen Fall bliebe genau
    /// der Teil ungeprüft, in dem Kundennummer, Ort und die Sperrmarke stehen, und ein fehlender
    /// <c>StaticResource</c> dort wirft erst beim Laden.</para>
    ///
    /// <para><b>Die Zeile wird gebaut, die Liste nicht aufgeklappt.</b> Das Aufklappen selbst
    /// ist hier nicht zu haben: Die Liste ist ein eigenes Fenster und entsteht nur an einem
    /// angezeigten Steuerelement — gemessen bleibt sie an einem bloß vermessenen leer, ohne
    /// Fehler und ohne Zeilen. Gebaut wird deshalb genau das, was die Liste bauen würde: ein
    /// Zeilenbehälter mit der <c>ItemContainerStyle</c> und der <c>ItemTemplate</c> des Feldes,
    /// mit derselben Zeile als Inhalt. Das inflationiert die Vorlage wirklich und wertet die
    /// Setzer wirklich aus — nur ohne Fenster.</para>
    ///
    /// <para>Geprüft wird zugleich die zweite Sperre: Die gesperrte Zeile ist abgeschaltet und
    /// nimmt weder Klick noch Tastenweg an. Die erste sitzt im Ansichtsmodell und wird ohne
    /// Oberfläche geprüft.</para>
    /// </remarks>
    [Fact]
    public void Die_Zeilenvorlage_der_Aufklappliste_entsteht()
    {
        int teileDerZeile = 0;
        bool waehlbar = false;
        bool gesperrteAbgeschaltet = false;

        _oberflaeche.Run(() =>
        {
            CompanyPickerViewModel modell = new(new Immer(
                Firma(1, "Müller GmbH", "MUE-1000", "55743", "Idar-Oberstein", false),
                Firma(2, "Müller GmbH", "MUE-2000", "10115", "Berlin", true)))
            {
                Query = "Müller",
            };

            modell.SearchNowAsync().GetAwaiter().GetResult();

            CompanyPicker sicht = new() { DataContext = modell };

            sicht.Measure(new WpfSize(800, 600));
            sicht.Arrange(new Rect(0, 0, 800, 600));
            sicht.UpdateLayout();

            WpfComboBox feld = Suche(sicht)
                ?? throw new InvalidOperationException("Kein Auswahlfeld in der Ansicht.");

            WpfComboBoxItem offene = Zeile(feld, modell.Companies[0]);
            WpfComboBoxItem gesperrte = Zeile(feld, modell.Companies[1]);

            // Kinder heisst: Die Vorlage ist wirklich entstanden. Waere ein StaticResource
            // darin nicht aufzuloesen, waere es hier schon geworfen.
            teileDerZeile = VisualTreeHelper.GetChildrenCount(offene);
            waehlbar = offene.IsEnabled;
            gesperrteAbgeschaltet = !gesperrte.IsEnabled;

            modell.Dispose();
        });

        Assert.True(teileDerZeile > 0, "Die Zeilenvorlage ist nicht entstanden.");
        Assert.True(waehlbar, "Die wählbare Zeile ist abgeschaltet.");
        Assert.True(gesperrteAbgeschaltet, "Die gesperrte Zeile ist in der Liste anklickbar.");
    }

    /// <summary>Baut einen Zeilenbehälter so, wie die Aufklappliste ihn bauen würde.</summary>
    /// <param name="feld">Das Auswahlfeld, dessen Stil und Vorlage gelten.</param>
    /// <param name="zeile">Die Firma, die in der Zeile steht.</param>
    /// <returns>Der gebaute und vermessene Behälter.</returns>
    private static WpfComboBoxItem Zeile(WpfComboBox feld, CompanyRow zeile)
    {
        WpfComboBoxItem behaelter = new()
        {
            // Genau das, was die Liste setzt: Inhalt, Datenzusammenhang, Vorlage, Stil.
            Content = zeile,
            DataContext = zeile,
            ContentTemplate = feld.ItemTemplate,
            Style = feld.ItemContainerStyle,
        };

        behaelter.Measure(new WpfSize(600, 200));
        behaelter.Arrange(new Rect(0, 0, 600, 200));
        behaelter.UpdateLayout();
        Leerlauf();

        return behaelter;
    }

    /// <summary>Arbeitet die Warteschlange des Oberflächenstranges leer.</summary>
    /// <remarks>
    /// Alles, was WPF aufschiebt — Laden, Anordnen, Zeichnen, das Erzeugen der Zeilen einer
    /// aufgeklappten Liste —, hängt in dieser Warteschlange. Ohne das Leerlaufen sähe der Fall
    /// einen Zwischenstand und meldete „nicht entstanden“, obwohl nur noch nichts geschehen ist.
    /// </remarks>
    private static void Leerlauf()
    {
        foreach (DispatcherPriority rang in new[]
                 {
                     DispatcherPriority.Loaded,
                     DispatcherPriority.Render,
                     DispatcherPriority.Background,
                     DispatcherPriority.ApplicationIdle,
                 })
        {
            Dispatcher.CurrentDispatcher.Invoke(() => { }, rang);
        }
    }

    /// <summary>Findet das Auswahlfeld im aufgebauten Baum.</summary>
    private static WpfComboBox? Suche(DependencyObject wurzel)
    {
        if (wurzel is WpfComboBox treffer)
        {
            return treffer;
        }

        int kinder = VisualTreeHelper.GetChildrenCount(wurzel);
        for (int i = 0; i < kinder; i++)
        {
            if (Suche(VisualTreeHelper.GetChild(wurzel, i)) is { } gefunden)
            {
                return gefunden;
            }
        }

        return null;
    }

    private static Company Firma(int id, string name, string nummer, string plz, string ort,
                                 bool gesperrt) =>
        new()
        {
            Id = id,
            Name = name,
            DisplayId = nummer,
            PostCode = plz,
            City = ort,
            Lockout = gesperrt,
        };

    /// <summary>Eine Suche, die immer dieselben Firmen liefert.</summary>
    private sealed class Immer : ICompanyRepository
    {
        private readonly Company[] _firmen;

        public Immer(params Company[] firmen) => _firmen = firmen;

        public Task<CompanySearchResult> SearchAsync(string query, CancellationToken ct = default) =>
            Task.FromResult(new CompanySearchResult
            {
                Outcome = CompanySearchOutcome.Found,
                Companies = _firmen,
                TotalFound = _firmen.Length,
                Explanation = "2 Firmen gefunden. Davon 1 gesperrt und nicht wählbar.",
            });
    }
}
