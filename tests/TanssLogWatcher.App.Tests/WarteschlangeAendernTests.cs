using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.Services;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Queue;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Ein wartender Eintrag lässt sich noch einmal ändern.
/// </summary>
/// <remarks>
/// <para><b>Warum es das gibt.</b> Solange ein Eintrag in der Warteschlange wartet, ist er
/// nicht in TANSS — der Bericht lässt sich also noch ändern. Möglich war das bisher nur beim
/// nächsten Programmstart, wenn der Dialog von selbst wieder kam. Wer vorher einen Tippfehler
/// bemerkte, konnte ihn stehen lassen oder den Eintrag verwerfen.</para>
///
/// <para><b>Der wichtigere Teil dieser Datei sind die Absagen.</b> Ein Eintrag, der gerade
/// unterwegs ist, darf nicht geändert werden — sonst ginge etwas anderes hinaus als das, was
/// auf dem Schirm steht. Einer, der schon in TANSS ist, erst recht nicht. Und beides muss
/// <b>gesagt</b> werden: Eine Schaltfläche, die nichts tut, sieht aus wie ein Fehler des
/// Werkzeugs.</para>
///
/// <para>Echt ist hier alles bis auf den Ort: Konfiguration und Zustandsdatenbank liegen in
/// einem Wegwerfordner statt im Benutzerprofil. Gesendet wird nichts — <c>Reoffer</c> legt vor
/// und schreibt nicht.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class WarteschlangeAendernTests
{
    /// <summary>
    /// Ein wartender Eintrag wird erneut vorgelegt.
    /// </summary>
    [Fact]
    public void Ein_wartender_Eintrag_wird_erneut_vorgelegt()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        RemoteSupportWrite payload = Nutzlast("Erster Text.");
        Assert.True(composition.Queue.Enqueue(payload, awaitDecision: true));

        List<SessionClosed> vorgelegt = [];
        host.Sessions.SessionEnded += (_, closed) => vorgelegt.Add(closed);

        ReofferResult result = host.Sessions.Reoffer(payload.RemoteMaintenanceId);

        Assert.True(result.Opened, result.Reason);
        Assert.Empty(result.Reason);

        SessionClosed einziger = Assert.Single(vorgelegt);
        Assert.Equal(payload.RemoteMaintenanceId, einziger.Session.SessionId);

        // Der Text der wartenden Zeile steht im Dialog - sonst faenge der Techniker von vorn an.
        Assert.Equal("Erster Text.", einziger.Session.Comment);

        // Vorgelegt heisst NICHT gesendet: Die Zeile wartet unveraendert weiter.
        QueuedUpload? zeile = composition.Queue.Find(payload.RemoteMaintenanceId);
        Assert.NotNull(zeile);
        Assert.Equal(QueueState.Pending, zeile!.State);
        Assert.Equal("Erster Text.", zeile.Payload.Comment);
    }

    /// <summary>
    /// Was gerade unterwegs ist, wird nicht geändert — und der Grund steht da.
    /// </summary>
    /// <remarks>
    /// Der gefährliche Fall: Zwischen dem Öffnen des Dialogs und dem Schreiben läge sonst ein
    /// Zeitfenster, in dem der Sendedienst eine andere Nutzlast hinausträgt als die, die der
    /// Techniker vor sich sieht.
    /// </remarks>
    [Fact]
    public void Was_unterwegs_ist_wird_nicht_geaendert()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out RuntimeComposition composition);

        RemoteSupportWrite payload = Nutzlast("Unterwegs.");
        Assert.True(composition.Queue.Enqueue(payload, awaitDecision: false));

        // So, wie der Sendedienst es tut: Der Eintrag wird geleast und steht damit auf
        // "unterwegs".
        IReadOnlyList<QueuedUpload> geleast = composition.Queue.Lease(1);
        Assert.Single(geleast);

        List<SessionClosed> vorgelegt = [];
        host.Sessions.SessionEnded += (_, closed) => vorgelegt.Add(closed);

        ReofferResult result = host.Sessions.Reoffer(payload.RemoteMaintenanceId);

        Assert.False(result.Opened);
        Assert.Empty(vorgelegt);
        Assert.Contains("unterwegs", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Einen Eintrag, den es nicht mehr gibt, kann niemand ändern — und auch das wird gesagt.
    /// </summary>
    [Fact]
    public void Einen_verschwundenen_Eintrag_meldet_es_statt_zu_schweigen()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out _);

        ReofferResult result = host.Sessions.Reoffer("gibt-es-nicht");

        Assert.False(result.Opened);
        Assert.NotEmpty(result.Reason);
    }

    /// <summary>
    /// Eine leere Kennung ist ein Fehler des Aufrufers und keine Absage.
    /// </summary>
    /// <remarks>
    /// Der Unterschied ist keine Förmlichkeit: „Es gibt ihn nicht“ wäre hier eine Auskunft über
    /// die Warteschlange, und die hat niemand eingeholt.
    /// </remarks>
    [Fact]
    public void Eine_leere_Kennung_wird_abgewiesen()
    {
        using TempDirectory temp = new();
        using AppHost host = Laufzeit(temp, out _);

        _ = Assert.Throws<ArgumentException>(() => host.Sessions.Reoffer("  "));
    }

    private static AppHost Laufzeit(TempDirectory temp, out RuntimeComposition composition)
    {
        ConfigStore store = new(temp.File("config.json"));
        store.Save(Sample.Config());

        AppHost host = new(store, NullLoggerFactory.Instance, new RuntimeNotifier(null),
                           TimeProvider.System, temp.File("state.db"));

        Assert.True(host.Reload(), "Die Konfiguration liess sich nicht laden.");

        composition = Assert.IsType<RuntimeComposition>(host.Composition);
        return host;
    }

    /// <summary>Eine Nutzlast, wie sie beim Sitzungsende entsteht.</summary>
    private static RemoteSupportWrite Nutzlast(string comment) => new()
    {
        TypeId = 1002,
        EmployeeId = 1,
        StartTime = DateTimeOffset.Now.AddMinutes(-20).ToUnixTimeSeconds(),
        EndTime = DateTimeOffset.Now.ToUnixTimeSeconds(),
        RemoteMaintenanceId = Guid.NewGuid().ToString(),
        Comment = comment,
        DeviceName = "srv-test01",
    };
}
