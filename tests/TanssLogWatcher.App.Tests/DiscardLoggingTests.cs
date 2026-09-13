using System.Runtime.Versioning;
using Microsoft.Extensions.Logging.Abstractions;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.App.ViewModels;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Logging;
using Xunit;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Das Verwerfen einer Sitzung im Abschlussdialog.
/// </summary>
/// <remarks>
/// <para><b>Es ist der einzige Ausgang dieses Fensters, der Arbeitszeit verschwinden lässt.</b>
/// In TANSS ist dabei nie etwas angekommen, und die Zeile in der Warteschlange wird gerade
/// entfernt — ohne Protokolleintrag bliebe von der Sitzung nichts übrig. Die Anleitung sagt
/// genau das zu; eine Zeit lang stimmte es nicht.</para>
///
/// <para>Echt ist hier alles bis auf zwei Orte: Konfiguration und Zustandsdatenbank liegen in
/// einem Wegwerfordner statt im Benutzerprofil. Warteschlange, Protokoll und Ansichtsmodell
/// sind dieselben wie im Betrieb.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class DiscardLoggingTests
{
    [Fact]
    public void Das_Verwerfen_hinterlaesst_einen_Eintrag_mit_Grund()
    {
        using TempDirectory temp = new();

        ConfigStore store = new(temp.File("config.json"));

        // Die Schwaerzung ausdruecklich AN: Nur dann ist am Ergebnis zu sehen, dass die
        // Gegenstelle im schwaerzbaren Feld gelandet ist und nicht im Freitext. Voreingestellt
        // ist sie aus, und mit ihr waere der Unterschied unsichtbar.
        store.Save(Sample.Config() with
        {
            Logging = new LoggingSection { RedactWindowTitles = true },
        });

        using AppHost host = new(store, NullLoggerFactory.Instance, new RuntimeNotifier(null),
                                 TimeProvider.System, temp.File("state.db"));

        Assert.True(host.Reload(), "Die Konfiguration liess sich nicht laden.");

        RuntimeComposition composition = Assert.IsType<RuntimeComposition>(host.Composition);

        SessionSnapshot session = Sample.Session(window: 0) with
        {
            EndedAt = DateTimeOffset.Now,
        };

        Assert.True(composition.Queue.Enqueue(Sample.Upload(session.SessionId)));

        SaveSessionViewModel model = new(host,
            new SessionClosed(session, SessionDisposition.Enqueued, string.Empty),
            "Fernwartung", color: null);

        model.DiscardCommand.Execute(null);

        // Fort ist die Zeile - und genau deshalb muss der Eintrag da sein.
        Assert.Null(composition.Queue.Find(session.SessionId));

        SessionLogEntry entry = Assert.Single(composition.Log.ForSession(session.SessionId));

        Assert.Equal("queue.discard", entry.Operation);
        Assert.Equal(SessionOutcome.Skipped, entry.Outcome);
        Assert.Equal(SessionTrigger.Manual, entry.Trigger);
        Assert.Contains("verworfen", entry.Reason, StringComparison.Ordinal);
        Assert.Contains(session.ProfileName, entry.Reason, StringComparison.Ordinal);

        // Die Gegenstelle steht im Feld fuer Fensterbeschriftungen und nicht im Freitext.
        // Der erste Satz gilt immer - im Freitext hat sie nichts zu suchen, gleich wie die
        // Schwaerzung steht. Der zweite gilt nur bei eingeschalteter Schwaerzung und ist
        // deshalb oben ausdruecklich hergestellt.
        Assert.DoesNotContain(session.Destination, entry.Reason, StringComparison.Ordinal);
        Assert.NotNull(entry.WindowTitle);
        Assert.NotEqual(session.Destination, entry.WindowTitle);
    }
}
