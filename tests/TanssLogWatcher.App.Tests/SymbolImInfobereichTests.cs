using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.App.Views;
using Xunit;

// Das Testprojekt zieht WinForms mit (echte Fenster als Aufnahmeziel), und dort heisst ein Typ
// genauso. Kurzname statt vollem Pfad: WPF ist hier gemeint, immer.
using WpfContextMenu = System.Windows.Controls.ContextMenu;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Das Symbol im Infobereich trägt <b>ohne jedes Fenster</b>.
/// </summary>
/// <remarks>
/// <para><b>Der Befund, aus dem diese Datei entstanden ist.</b> Der Autostart lief, aber das
/// Symbol kam nicht mit. Gemessen an den Fenstern des laufenden Prozesses: Mit
/// <c>--minimized</c> führte er kein einziges Fenster der Klasse <c>H.NotifyIcon_*</c>, ohne
/// den Schalter schon. Die Ursache stand in der Vorlage: Das Symbol war Teil des
/// Hauptfensters, und <c>App.OnStartup</c> rief beim Autostart <c>Hide()</c> auf einem Fenster,
/// das nie gezeigt worden war. Ein solches Fenster baut seinen Inhalt nicht auf — kein Symbol,
/// und damit kein Weg, das Werkzeug zu bedienen. Es lief unsichtbar und unerreichbar.</para>
///
/// <para><b>Was hier geprüft wird, ist genau die Umkehrung davon:</b> Symbol, Hinweistext und
/// Kontextmenü entstehen aus einer Anzeige, die der Anwendung gehört — ohne dass irgendwo ein
/// Fenster gebaut, gezeigt oder geladen worden wäre. Kein Fall in dieser Datei fasst ein
/// Fenster an; das ist der Punkt.</para>
///
/// <para><b>Warum ein Ladefall und keine reine Rechnung.</b> Eine missglückte Bindung wirft in
/// WPF nicht, sie tut stillschweigend nichts. Ein Kontextmenü ohne Datenkontext sähe im Bau
/// genauso aus wie eines mit — nur bliebe „Überwachung angehalten“ wirkungslos und
/// „Einrichten“ unsichtbar. Deshalb wird die Vorlage wirklich geladen und die Bindung wirklich
/// gemessen.</para>
///
/// <para><b>Angemeldet wird bei Windows nichts.</b> Das geschieht erst mit <c>ForceCreate</c>,
/// und das ruft hier niemand: Ein Prüfstand hat im Infobereich des Benutzers nichts
/// verloren.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Collection(Ladefaelle.Name)]
public sealed class SymbolImInfobereichTests(UiThreadFixture oberflaeche)
{
    private readonly UiThreadFixture _oberflaeche = oberflaeche;

    /// <summary>
    /// Der Hinweistext am Symbol steht, ohne dass je ein Fenster offen war.
    /// </summary>
    /// <remarks>
    /// Er ist im Normalbetrieb die einzige sichtbare Anzeige. Verglichen wird gegen die
    /// Anzeige selbst und nicht gegen einen erwarteten Satz: Geprüft wird, dass die Bindung
    /// trägt — was in ihr steht, prüfen die Fälle zur Fußzeile.
    /// </remarks>
    [Fact]
    public void Der_Hinweistext_steht_ohne_Fenster() => _oberflaeche.Run(() =>
    {
        using AppHost laufzeit = Laufzeit();
        using ShellViewModel anzeige = new(laufzeit);
        using AppTrayIcon symbol = new(anzeige);
        Gebunden();

        Assert.False(string.IsNullOrWhiteSpace(symbol.ToolTipText));
        Assert.Equal(anzeige.TrayToolTip, symbol.ToolTipText);
    });

    /// <summary>
    /// Das Kontextmenü ist bedienbar, ohne dass je ein Fenster offen war.
    /// </summary>
    /// <remarks>
    /// <para>Der Haken ist der Fall mit dem meisten Gewicht: Er geht in beide Richtungen bis in
    /// den Dienst durch. Hing das Menü an keinem Datenkontext, bliebe er ein Haken, der nichts
    /// tut — und die Überwachung liefe weiter, während der Techniker glaubt, sie stehe.</para>
    /// <para>Die Zeile mit dem Zustand und der Eintrag „Einrichten“ sind die Antwort auf die
    /// zweite Hälfte des Befundes: Beim Autostart geht kein Assistent von selbst auf. Dann muss
    /// am Symbol ablesbar sein, dass nichts eingerichtet ist, und daneben stehen, was dagegen
    /// zu tun ist.</para>
    /// </remarks>
    [Fact]
    public void Das_Kontextmenue_traegt_ohne_Fenster() => _oberflaeche.Run(() =>
    {
        using AppHost laufzeit = Laufzeit();
        using ShellViewModel anzeige = new(laufzeit);
        using AppTrayIcon symbol = new(anzeige);
        Gebunden();

        _ = Assert.IsType<WpfContextMenu>(symbol.ContextMenu);

        // Der Zustand in Worten - dieselbe Quelle wie die Plakette in der Fusszeile.
        Assert.Equal(anzeige.StateText, symbol.StateItem.Header);

        // "Einrichten" zeigt sich genau dann, wenn es gebraucht wird. Sichtbar heisst hier:
        // Der Umsetzer hat gegriffen, die Bindung also auch.
        Assert.Equal(anzeige.NeedsSetup ? Visibility.Visible : Visibility.Collapsed,
                     symbol.SetupItem.Visibility);

        // Der Weg vom Haken bis in den Dienst, in beide Richtungen.
        Assert.True(laufzeit.Sessions.IsEnabled);

        symbol.PauseItem.IsChecked = true;
        Assert.True(anzeige.IsPaused);
        Assert.False(laufzeit.Sessions.IsEnabled);

        symbol.PauseItem.IsChecked = false;
        Assert.False(anzeige.IsPaused);
        Assert.True(laufzeit.Sessions.IsEnabled);
    });

    /// <summary>Laesst das Bindungswerk einmal durchlaufen.</summary>
    /// <remarks>
    /// <b>Ohne diese Zeile misst der Fall zu frueh.</b> WPF haengt eine Bindung nicht beim
    /// Setzen des Datenkontexts an, sondern in einem eigenen Vorgang des Bindungswerks -
    /// gemessen: Unmittelbar nach dem Konstruktor ist der Hinweistext noch leer. Im Betrieb
    /// faellt das nicht auf, weil zwischen Start und dem ersten Mauszeiger auf dem Symbol
    /// Sekunden liegen; in einem Pruefstand, der beides in derselben Anweisung tut, sehr wohl.
    /// Die niedrigere Dringlichkeit wartet ab, bis das Bindungswerk fertig ist.
    /// </remarks>
    private static void Gebunden() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Loaded);

    /// <summary>Eine Laufzeit ohne Konfiguration — der Zustand des ersten Starts.</summary>
    /// <remarks>
    /// Ausdrücklich der uneingerichtete Fall: Genau er war beim Autostart bisher unsichtbar.
    /// Ein Netzaufruf entsteht nicht — <see cref="AppHost.StartAsync"/> wird nicht gerufen.
    /// </remarks>
    private static AppHost Laufzeit() =>
        new(loggers: NullLoggerFactory.Instance, notifier: new RuntimeNotifier(null));
}
