using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Die echte Fensteraufzählung — gegen echte Fenster, nicht gegen Attrappen.
/// </summary>
/// <remarks>
/// <para>Die Fenster entstehen im Testlauf selbst. Das ist der einzige Weg, diese Klasse zu
/// prüfen, ohne sich auf zufällig geöffnete Anwendungen zu verlassen — und ein Test, der davon
/// abhängt, was gerade offen ist, ist kein Test.</para>
/// <para>Ein eigener Strang mit eigener Nachrichtenschleife, weil Fenster einen brauchen. Er
/// endet mit dem Test.</para>
/// </remarks>
[System.Runtime.Versioning.SupportedOSPlatform("windows6.0.6000")]
public sealed class Win32WindowGeometrySourceTests
{
    /// <summary>
    /// Der Fall, wegen dem es diese Quelle überhaupt gibt: Ein Unterfenster OHNE TITEL muss
    /// gefunden werden. Die Quelle der Sitzungserkennung verwirft es, und damit ginge genau der
    /// Dialog verloren, um den es hinterher geht.
    /// </summary>
    [Fact]
    public void Ein_titelloses_Unterfenster_wird_gefunden()
    {
        using WindowFixture fixture = WindowFixture.Open(
            main: new System.Drawing.Rectangle(100, 100, 800, 600),
            child: new System.Drawing.Rectangle(250, 250, 300, 200),
            childTitle: string.Empty);

        Win32WindowGeometrySource source = new();
        IReadOnlyList<WindowBox> windows = source.WindowsOf(Environment.ProcessId);

        Assert.Contains(windows, w => w.Handle == fixture.MainHandle);
        Assert.Contains(windows, w => w.Handle == fixture.ChildHandle);
    }

    [Fact]
    public void Die_gemessene_Lage_stimmt_mit_der_gesetzten_ueberein()
    {
        using WindowFixture fixture = WindowFixture.Open(
            main: new System.Drawing.Rectangle(120, 140, 640, 480),
            child: null,
            childTitle: null);

        Win32WindowGeometrySource source = new();
        WindowBox box = Assert.Single(source.WindowsOf(Environment.ProcessId),
                                      w => w.Handle == fixture.MainHandle);

        // Der Fensterrahmen gehoert zum Fenster; gemessen wird das aeussere Rechteck. Deshalb
        // keine Gleichheit auf den Punkt, sondern eine Schranke: Die Lage muss stimmen, die
        // Groesse mindestens die gesetzte sein.
        Assert.Equal(120, box.Left);
        Assert.Equal(140, box.Top);
        Assert.True(box.Width >= 640, $"Breite {box.Width} war kleiner als gesetzt.");
        Assert.True(box.HasArea);
    }

    /// <summary>
    /// Ein minimiertes Fenster hat zwar eine Geometrie, aber es ist die des Symbols. Es gehört
    /// nicht auf die Leinwand — dort wäre es ein schwarzer Fleck.
    /// </summary>
    [Fact]
    public void Ein_minimiertes_Fenster_faellt_weg()
    {
        using WindowFixture fixture = WindowFixture.Open(
            main: new System.Drawing.Rectangle(100, 100, 800, 600),
            child: null,
            childTitle: null);

        Win32WindowGeometrySource source = new();
        Assert.Contains(source.WindowsOf(Environment.ProcessId),
                        w => w.Handle == fixture.MainHandle);

        fixture.MinimiseMain();

        Assert.DoesNotContain(source.WindowsOf(Environment.ProcessId),
                              w => w.Handle == fixture.MainHandle);
    }

    [Fact]
    public void Ein_fremder_Prozess_liefert_keine_eigenen_Fenster()
    {
        using WindowFixture fixture = WindowFixture.Open(
            main: new System.Drawing.Rectangle(100, 100, 400, 300),
            child: null,
            childTitle: null);

        Win32WindowGeometrySource source = new();

        // Der Leerlaufprozess hat keine Fenster - und ganz sicher nicht unsere.
        Assert.DoesNotContain(source.WindowsOf(0), w => w.Handle == fixture.MainHandle);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Eine_unsinnige_Prozesskennung_liefert_nichts(int processId)
    {
        Assert.Empty(new Win32WindowGeometrySource().WindowsOf(processId));
    }

    /// <summary>Zwei echte Fenster auf einem eigenen Strang mit Nachrichtenschleife.</summary>
    private sealed class WindowFixture : IDisposable
    {
        private readonly Thread _thread;
        private readonly ManualResetEventSlim _ready = new(false);

        private Form? _main;
        private Form? _child;

        private WindowFixture(System.Drawing.Rectangle main,
                              System.Drawing.Rectangle? child,
                              string? childTitle)
        {
            _thread = new Thread(() =>
            {
                _main = new Form
                {
                    Text = "Aufnahmeprobe",
                    StartPosition = FormStartPosition.Manual,
                    Bounds = main,
                    ShowInTaskbar = false,
                };

                _main.Shown += (_, _) =>
                {
                    if (child is { } bounds)
                    {
                        _child = new Form
                        {
                            Text = childTitle ?? string.Empty,
                            FormBorderStyle = FormBorderStyle.FixedDialog,
                            StartPosition = FormStartPosition.Manual,
                            Bounds = bounds,
                            ShowInTaskbar = false,
                            ControlBox = false,
                        };

                        _child.Show(_main);
                    }

                    _ready.Set();
                };

                Application.Run(_main);
            })
            {
                IsBackground = true,
            };

            _thread.SetApartmentState(ApartmentState.STA);
            _thread.Start();

            if (!_ready.Wait(TimeSpan.FromSeconds(10)))
            {
                throw new InvalidOperationException(
                    "Das Probefenster ist nicht aufgegangen. Ohne echtes Fenster lässt sich "
                    + "die Fensteraufzählung nicht prüfen.");
            }

            // Der Nachrichtenschleife einen Augenblick geben, damit die Fenster tatsaechlich
            // gezeichnet sind - IsWindowVisible meldet sonst gelegentlich noch false.
            Thread.Sleep(300);
        }

        public nint MainHandle => Invoke(() => _main!.Handle);

        public nint ChildHandle => Invoke(() => _child!.Handle);

        public static WindowFixture Open(System.Drawing.Rectangle main,
                                         System.Drawing.Rectangle? child,
                                         string? childTitle) =>
            new(main, child, childTitle);

        public void MinimiseMain()
        {
            _ = Invoke(() =>
            {
                _main!.WindowState = FormWindowState.Minimized;
                return 0;
            });

            Thread.Sleep(300);
        }

        public void Dispose()
        {
            try
            {
                _ = Invoke(() =>
                {
                    _child?.Close();
                    _main?.Close();
                    return 0;
                });
            }
            catch (InvalidOperationException)
            {
                // Das Fenster ist schon fort; mehr ist hier nicht aufzuraeumen.
            }

            _ready.Dispose();
        }

        private T Invoke<T>(Func<T> action) =>
            _main is { IsHandleCreated: true } form && form.InvokeRequired
                ? (T)form.Invoke(action)
                : action();
    }
}
