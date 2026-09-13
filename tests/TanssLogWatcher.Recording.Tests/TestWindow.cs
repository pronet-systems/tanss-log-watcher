using System.Runtime.InteropServices;
using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Ein echtes Fenster mit bekannter Farbe, auf einem eigenen Strang.
/// </summary>
/// <remarks>
/// <para>Die Bildschirmaufnahme lässt sich nicht gegen eine Attrappe prüfen: Was sie liefert,
/// ist genau das, was Windows zeichnet. Also wird gezeichnet — ein Fenster in einer Farbe, die
/// sonst nirgends vorkommt, und danach wird der Bildpunkt nachgesehen.</para>
/// <para>Eigener Strang mit eigener Nachrichtenschleife, weil Fenster einen brauchen. Er endet
/// mit dem Test.</para>
/// </remarks>
internal sealed class TestWindow : IDisposable
{
    private const uint SwpNoActivate = 0x0010;
    private const int HwndTopMost = -1;

    private readonly ManualResetEventSlim _ready = new(false);

    private Form? _form;

    private TestWindow(System.Drawing.Color colour, int width, int height,
                       bool topMost, TestWindow? sameBoundsAs)
    {
        System.Drawing.Rectangle bounds = sameBoundsAs is not null
            ? sameBoundsAs.Bounds
            : new System.Drawing.Rectangle(80, 80, width, height);

        Thread thread = new(() =>
        {
            _form = new Form
            {
                Text = "Aufnahmeziel",
                FormBorderStyle = FormBorderStyle.None,
                StartPosition = FormStartPosition.Manual,
                Bounds = bounds,
                BackColor = colour,
                ShowInTaskbar = false,
                TopMost = topMost,
            };

            _form.Shown += (_, _) =>
            {
                if (topMost)
                {
                    _ = SetWindowPos(_form.Handle, HwndTopMost, bounds.X, bounds.Y,
                                     bounds.Width, bounds.Height, SwpNoActivate);
                }

                _ready.Set();
            };

            Application.Run(_form);
        })
        {
            IsBackground = true,
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();

        Assert.True(_ready.Wait(TimeSpan.FromSeconds(10)),
            "Das Probefenster ist nicht aufgegangen.");

        // Der Fensterverwaltung Zeit geben, tatsaechlich zu zeichnen - die Aufnahme liefert
        // sonst ein Bild aus der Zeit davor.
        Thread.Sleep(400);
    }

    /// <summary>Das Fensterhandle.</summary>
    public nint Handle => Invoke(() => _form!.Handle);

    /// <summary>Die Lage des Fensters.</summary>
    public System.Drawing.Rectangle Bounds => Invoke(() => _form!.Bounds);

    /// <summary>Öffnet ein Fenster.</summary>
    /// <param name="colour">Die Hintergrundfarbe.</param>
    /// <param name="width">Die Breite.</param>
    /// <param name="height">Die Höhe.</param>
    /// <param name="topMost">Soll es über allem liegen?</param>
    /// <param name="sameBoundsAs">Deckungsgleich mit diesem Fenster.</param>
    public static TestWindow Open(System.Drawing.Color colour, int width, int height,
                                  bool topMost = false, TestWindow? sameBoundsAs = null) =>
        new(colour, width, height, topMost, sameBoundsAs);

    /// <summary>Minimiert das Fenster.</summary>
    public void Minimise()
    {
        _ = Invoke(() =>
        {
            _form!.WindowState = FormWindowState.Minimized;
            return 0;
        });

        Thread.Sleep(400);
    }

    /// <summary>Schliesst das Fenster.</summary>
    public void Dispose()
    {
        try
        {
            _ = Invoke(() =>
            {
                _form?.Close();
                return 0;
            });
        }
        catch (InvalidOperationException)
        {
            // Schon fort. Mehr ist hier nicht aufzuraeumen.
        }

        _ready.Dispose();
    }

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(nint hWnd, nint after, int x, int y,
                                            int cx, int cy, uint flags);

    private T Invoke<T>(Func<T> action) =>
        _form is { IsHandleCreated: true } form && form.InvokeRequired
            ? (T)form.Invoke(action)
            : action();
}
