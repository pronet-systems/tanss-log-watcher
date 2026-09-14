using Xunit;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Ein Test, der einen Bildschirm braucht, der auch weiterzeichnet.
/// </summary>
/// <remarks>
/// <para><b>Wofür das gebraucht wird.</b> <c>CaptureFact</c> fragt nur, ob es
/// die Bildschirmaufnahme auf diesem Rechner überhaupt gibt. Das reicht für die meisten Fälle
/// und ist auf dem Bauläufer bei GitHub erfüllt — dort wurde gemessen: Sitzung 2,
/// <c>WinSta0\Default</c>, DWM-Komposition an, ein Bildschirm 1024×768,
/// <c>GraphicsCaptureSession.IsSupported()</c> wahr, und ein rotes Fenster kommt als rotes
/// Bild zurück.</para>
///
/// <para><b>Was dort fehlt.</b> Es kommt <b>genau ein</b> Bild. In derselben Messung lieferte
/// ein ruhendes Fenster in vierzig Abfragen über zwei Sekunden ein einziges Bild, und nach dem
/// Verdecken durch ein zweites Fenster in sechzig Abfragen über drei Sekunden keines mehr. Das
/// ist folgerichtig und kein Fehler: Windows.Graphics.Capture gibt ein Bild heraus, wenn die
/// Fensterverwaltung neu zeichnet, und ohne angeschlossenen Bildschirm zeichnet sie nach dem
/// ersten Mal nicht wieder. Ein Läufer ohne Bildschirm hat damit alles, was <b>ein</b> Bild
/// braucht, und nichts von dem, was ein <b>Verlauf</b> braucht.</para>
///
/// <para><b>Die Prüfung ist nicht die Zusicherung.</b> Gefragt wird nur, ob überhaupt weiter
/// Bilder kommen — nicht, was auf ihnen steht. Was auf ihnen steht, prüfen die Fälle selbst,
/// und auf einem Rechner mit Bildschirm laufen sie unverändert und können unverändert
/// scheitern. Wer <see cref="WindowCapture"/> so kaputtmacht, dass gar keine Bilder mehr
/// kommen, wird davon nicht gedeckt: <c>Ein_rotes_Fenster_ergibt_ein_rotes_Bild</c> braucht
/// nur ein einziges Bild, trägt deshalb weiterhin <c>[CaptureFact]</c> und wird überall rot.</para>
/// </remarks>
public sealed class LiveScreenFactAttribute : FactAttribute
{
    /// <summary>Baut das Kennzeichen und überspringt ohne nutzbaren Bildschirm.</summary>
    public LiveScreenFactAttribute()
    {
        if (!LiveScreen.IsAvailable)
        {
            Skip = "Kein nutzbarer Bildschirm vorhanden: Die Bildschirmaufnahme gibt auf "
                + "diesem Rechner nach dem ersten Bild kein weiteres mehr heraus, weil ohne "
                + "angeschlossenen Bildschirm niemand neu zeichnet. Üblich auf einem "
                + "Bauläufer. Dieser Fall braucht einen Bildverlauf und wird deshalb "
                + "übersprungen — er gilt nicht als bestanden.";
        }
    }
}

/// <summary>
/// Die Messung dahinter: Kommt auf diesem Rechner mehr als ein Bild?
/// </summary>
/// <remarks>
/// Einmal je Prozess und nicht je Fall: Die Messung öffnet ein Fenster und ein Grafikgerät.
/// Das je Testfall zu tun kostete Sekunden und änderte am Ergebnis nichts — ein Bildschirm
/// kommt während eines Testlaufs nicht dazu.
/// </remarks>
public static class LiveScreen
{
    /// <summary>Wie viele Bilder die Messung sehen will, bevor sie zufrieden ist.</summary>
    /// <remarks>
    /// Zwei, und nicht mehr: Das erste Bild kommt beim Beginn der Aufnahme und sagt deshalb
    /// nichts darüber, ob jemand <i>weiter</i> zeichnet. Das zweite sagt genau das. Gemessen
    /// steht zwischen den beiden Rechnerarten kein knapper Abstand, sondern ein ganzer:
    /// <b>1</b> Bild in zwei Sekunden auf dem Bauläufer gegen <b>51</b> auf dem Prüfrechner,
    /// dort das zweite bereits nach <b>36 ms</b>. Eine Schwelle, die dazwischen liegt, kann
    /// nicht knapp werden.
    /// </remarks>
    private const int Wanted = 2;

    /// <summary>Wie lange die Messung höchstens wartet.</summary>
    /// <remarks>
    /// Zwei Sekunden. Auf einem Rechner mit Bildschirm ist sie gemessen nach 36 ms fertig und
    /// bricht ab; die Frist wird nur ohne Bildschirm fällig, und dort einmal je Prozess.
    /// </remarks>
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(2);

    private static readonly Lazy<bool> Probe = new(Measure, LazyThreadSafetyMode.ExecutionAndPublication);

    /// <summary>Zeichnet auf diesem Rechner jemand weiter?</summary>
    public static bool IsAvailable => Probe.Value;

    private static bool Measure()
    {
        try
        {
            if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 19041)
                || !WindowCapture.IsSupported())
            {
                return false;
            }

            return Frames() >= Wanted;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 3: Geht die Messung schief, ist das kein nutzbarer Bildschirm.
            // Sie darf aber nicht den ganzen Lauf mitnehmen.
            return false;
        }
    }

    private static int Frames()
    {
        // Ein eigenes Fenster und nicht das des Falls: Die Messung laeuft einmal je Prozess,
        // lange bevor irgendein Fall sein Fenster oeffnet.
        using TestWindow window = TestWindow.Open(System.Drawing.Color.Red, 200, 150);
        using CaptureDevice device = CaptureDevice.Create();
        using WindowCapture capture = WindowCapture.Start(device, window.Handle);

        byte[] buffer = [];
        int frames = 0;

        for (DateTime deadline = DateTime.UtcNow + Window;
             frames < Wanted && DateTime.UtcNow < deadline;)
        {
            if (capture.TryCopyLatest(ref buffer) is not null)
            {
                frames++;
                continue;
            }

            Thread.Sleep(25);
        }

        return frames;
    }
}
