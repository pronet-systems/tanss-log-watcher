using System.Runtime.Versioning;
using System.Windows;
using System.Windows.Threading;
using Wpf.Ui.Appearance;
using Wpf.Ui.Markup;
using Xunit;

// Das Testprojekt zieht WinForms mit (echte Fenster als Aufnahmeziel), und dort heisst ein Typ
// genauso. Kurzname statt vollem Pfad: WPF ist hier gemeint, immer.
using WpfApplication = System.Windows.Application;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Ein einziger Oberflächenstrang für alle Ladefälle — mit genau einer Anwendung darauf.
/// </summary>
/// <remarks>
/// <para><b>Warum es das gibt.</b> WPF erlaubt je Anwendungsbereich <b>eine</b>
/// <see cref="WpfApplication"/>, und deren Wörterbücher gehören dem Strang, auf dem sie
/// entstanden ist. Zwei Ladefälle, die sich jeder einen eigenen STA-Strang bauen, liefen
/// gemessen auseinander: Der zweite fand <c>Application.Current</c> bereits gesetzt und griff
/// von seinem Strang aus auf fremde Wörterbücher zu. Beide Fälle fielen dadurch
/// <b>wechselnd</b> um — mal einer, mal beide, je nachdem, wer zuerst da war. Ein Fehlschlag,
/// der von der Reihenfolge abhängt, ist schlimmer als keiner: Er sieht beim nächsten Lauf aus
/// wie behoben.</para>
///
/// <para>Der Strang lebt so lange wie die Testsammlung, trägt die Wörterbücher aus
/// <c>App.xaml</c> und nimmt über <see cref="Run"/> Arbeit entgegen. Ein Bildschirm wird nicht
/// gebraucht: Es wird nichts angezeigt, nur gebaut, gebunden und gerechnet.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class UiThreadFixture : IDisposable
{
    private readonly Thread _thread;
    private bool _disposed;

    /// <summary>Startet den Strang und legt die Wörterbücher zusammen.</summary>
    public UiThreadFixture()
    {
        TaskCompletionSource<Dispatcher> bereit = new();

        _thread = new Thread(() =>
        {
            WpfApplication app = WpfApplication.Current ?? new WpfApplication();

            // OHNE DIESE ZEILE HAENGT DIESE SAMMLUNG AN DER REIHENFOLGE IHRER FAELLE.
            // Die Vorgabe ist OnLastWindowClose: Schliesst ein Fall sein Fenster - und jeder
            // Ladefall tut das am Ende -, faellt die Fensterzahl der Anwendung auf null, und
            // WPF faehrt das Anwendungsobjekt herunter. Der naechste Fall, der ein Fenster baut,
            // bekommt dann beim Laden der Vorlage "Das Anwendungsobjekt wird beendet" um die
            // Ohren - ein Fehlschlag, der nichts mit der Vorlage zu tun hat und sich je nach
            // Reihenfolge auf einen anderen Fall verschiebt. Gemessen ist genau das geschehen,
            // als eine neue Testklasse hinzukam und die Reihenfolge verschob.
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;

            if (app.Resources.MergedDictionaries.Count == 0)
            {
                app.Resources.MergedDictionaries.Add(
                    new ThemesDictionary { Theme = ApplicationTheme.Dark });
                app.Resources.MergedDictionaries.Add(new ControlsDictionary());
                app.Resources.MergedDictionaries.Add(Woerterbuch("Design/Tokens.xaml"));
                app.Resources.MergedDictionaries.Add(Woerterbuch("Design/Tokens.Dark.xaml"));
            }

            bereit.SetResult(Dispatcher.CurrentDispatcher);

            // Ohne laufende Schleife naehme der Strang keine Arbeit entgegen; Invoke wartete
            // dann bis zum Zeitablauf des Falles.
            Dispatcher.Run();
        })
        {
            IsBackground = true,
        };

        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();

        Dispatcher = bereit.Task.GetAwaiter().GetResult();
    }

    /// <summary>Die Umlenkung auf den Oberflächenstrang.</summary>
    public Dispatcher Dispatcher { get; }

    /// <summary>
    /// Führt die Arbeit auf dem Oberflächenstrang aus und wartet darauf.
    /// </summary>
    /// <remarks>
    /// Eine Ausnahme aus der Arbeit kommt hier wieder heraus — <c>Dispatcher.Invoke</c>
    /// reicht sie durch. Ein Ladefehler steht damit im Testbericht und nicht in einem
    /// verschluckten Feld.
    /// </remarks>
    /// <param name="work">Was auf dem Strang zu tun ist.</param>
    public void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Dispatcher.Invoke(work);
    }

    /// <summary>Hält den Strang an.</summary>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        Dispatcher.InvokeShutdown();
        _ = _thread.Join(TimeSpan.FromSeconds(10));
    }

    private static ResourceDictionary Woerterbuch(string pfad) => new()
    {
        Source = new Uri("pack://application:,,,/TanssLogWatcher;component/" + pfad),
    };
}

/// <summary>
/// Die Sammlung aller Fälle, die wirklich XAML laden.
/// </summary>
/// <remarks>
/// Sie laufen <b>nacheinander</b> und auf demselben Strang. Nebenläufig gingen sie sich über
/// die eine erlaubte <see cref="WpfApplication"/> gegenseitig an die Wörterbücher.
/// </remarks>
[CollectionDefinition(Name)]
public sealed class Ladefaelle : ICollectionFixture<UiThreadFixture>
{
    /// <summary>Der Name, unter dem die Fälle sich eintragen.</summary>
    public const string Name = "Oberfläche";
}
