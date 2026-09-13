using System.Runtime.Versioning;
using TanssLogWatcher.App.Runtime;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Ein laufender Ladevorgang überlebt den Seitenwechsel.
/// </summary>
/// <remarks>
/// <para><b>Der Befund des Technikers, wörtlich:</b> „wenn ich den download eines updates
/// starte, dann die page wechsel und wieder zurück zu den einstellungen, dann kann ich den
/// status des downloads nicht mehr sehen. ganz im gegenteil kann ich versuchen den download
/// erneut zu starten, was aber fehlschlägt, da die datei von einem anderen prozess bereits
/// belegt ist.“</para>
///
/// <para><b>Die Ursache war der Ort des Zustands.</b> „Läuft gerade“, „wie weit“ und „wo liegt
/// die Datei“ standen im Ansichtsmodell der Seite — und das wird beim Verlassen der Seite
/// verworfen. Beim Zurückkommen entstand ein neues, das von nichts wusste: kein Fortschritt zu
/// sehen, die Schaltfläche wieder anklickbar, und der zweite Vorgang scheiterte an der Datei,
/// die der erste noch offen hatte.</para>
///
/// <para><b>Was hier geprüft wird.</b> Der Dienst, denn dort liegt der Zustand jetzt. Ein
/// Prüfstand, der Seiten wechselt, prüfte am Ende WPF; entscheidend ist, dass der Dienst einen
/// zweiten Vorgang verweigert und seinen Stand behält. <b>Ohne Netz</b> — der Ladevorgang
/// scheitert hier sofort, und genau das ist brauchbar: Auch ein Fehlschlag muss den Riegel
/// wieder öffnen, sonst liesse sich nie wieder laden.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class LadevorgangUeberlebtTests
{
    /// <summary>
    /// Ein zweiter Anstoß während eines laufenden Vorgangs wird abgelehnt.
    /// </summary>
    /// <remarks>
    /// Der Kern des Befundes: Der zweite Klick darf keinen zweiten Vorgang auf dieselbe Datei
    /// werfen.
    /// </remarks>
    [Fact]
    public void Ein_zweiter_Anstoss_wird_abgelehnt()
    {
        using UpdateService dienst = Ruhend();
        AvailableUpdate fassung = Fassung();

        Assert.True(dienst.StartDownload(fassung), "Der erste Vorgang hat nicht begonnen.");

        // Solange der erste laeuft, gibt es keinen zweiten. Laeuft er schon nicht mehr, ist
        // auch nichts zu beweisen - dann ist dieser Fall stumm und der naechste zustaendig.
        if (dienst.IsDownloading)
        {
            Assert.False(dienst.StartDownload(fassung),
                         "Ein zweiter Vorgang wurde angestossen, obwohl einer lief.");
        }
    }

    /// <summary>
    /// Der Dienst meldet den Stand — und behält ihn, gleich wer zusieht.
    /// </summary>
    /// <remarks>
    /// Das ist der Ersatz für den Seitenwechsel: Der Zustand hängt an keinem Zuschauer. Wer
    /// sich abmeldet und später wieder anmeldet, liest denselben Stand.
    /// </remarks>
    [Fact]
    public async Task Der_Stand_haengt_an_keinem_Zuschauer()
    {
        using UpdateService dienst = Ruhend();

        int meldungen = 0;
        void Zusehen(object? s, EventArgs e) => Interlocked.Increment(ref meldungen);

        dienst.DownloadChanged += Zusehen;
        _ = dienst.StartDownload(Fassung());

        // So verlaesst der Techniker die Seite: Das Ansichtsmodell meldet sich ab.
        dienst.DownloadChanged -= Zusehen;

        await Abgeschlossen(dienst).ConfigureAwait(true);

        // Und so kommt er zurueck: Der Stand steht beim Dienst, nicht beim Zuschauer.
        Assert.False(dienst.IsDownloading);
        Assert.True(meldungen > 0, "Der Dienst hat den Beginn nicht gemeldet.");
    }

    /// <summary>
    /// Auch ein Fehlschlag öffnet den Riegel wieder.
    /// </summary>
    /// <remarks>
    /// Ohne das bliebe nach dem ersten misslungenen Versuch jeder weitere gesperrt — und die
    /// Schaltfläche wäre für immer tot, ohne dass jemand sagen könnte warum.
    /// </remarks>
    [Fact]
    public async Task Nach_einem_Fehlschlag_laesst_sich_erneut_laden()
    {
        using UpdateService dienst = Ruhend();

        _ = dienst.StartDownload(Fassung());
        await Abgeschlossen(dienst).ConfigureAwait(true);

        Assert.False(dienst.IsDownloading);
        Assert.Null(dienst.Downloaded);
        Assert.NotNull(dienst.DownloadProblem);

        // Der Riegel ist wieder offen.
        Assert.True(dienst.StartDownload(Fassung()),
                    "Nach einem Fehlschlag liess sich kein neuer Vorgang beginnen.");
    }

    /// <summary>Ohne Fassung gibt es nichts zu laden.</summary>
    [Fact]
    public void Ohne_Fassung_wird_abgewiesen()
    {
        using UpdateService dienst = Ruhend();
        _ = Assert.Throws<ArgumentNullException>(() => dienst.StartDownload(null!));
    }

    /// <summary>
    /// Ein Dienst, dessen Zeitgeber in dieser Prüfung nicht zuschlägt.
    /// </summary>
    /// <remarks>
    /// Eine Stunde Anlaufzeit: Die selbsttätige Prüfung soll hier nicht ins Netz greifen. Der
    /// Ladevorgang wird ausdrücklich von Hand angestossen.
    /// </remarks>
    private static UpdateService Ruhend() => new(TimeSpan.FromHours(1));

    /// <summary>Eine Fassung, deren Adresse ins Leere zeigt.</summary>
    /// <remarks>
    /// <c>.invalid</c> ist nach RFC 2606 dauerhaft unauflösbar — der Versuch scheitert also
    /// ohne Netzzugang und ohne fremden Rechner zu behelligen.
    /// </remarks>
    private static AvailableUpdate Fassung() => new(
        new Version(9, 9, 9),
        "v9.9.9",
        new Uri("https://example.invalid/setup.exe"),
        "setup.exe",
        ChecksumUrl: null,
        Bytes: 1024,
        new Uri("https://example.invalid/release"));

    /// <summary>Wartet, bis der Vorgang durch ist — mit Obergrenze.</summary>
    private static async Task Abgeschlossen(UpdateService dienst)
    {
        for (int i = 0; i < 200 && dienst.IsDownloading; i++)
        {
            await Task.Delay(50).ConfigureAwait(false);
        }

        Assert.False(dienst.IsDownloading, "Der Vorgang ist nach zehn Sekunden noch gelaufen.");
    }
}
