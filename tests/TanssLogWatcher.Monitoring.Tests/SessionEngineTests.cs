using TanssLogWatcher.Monitoring.Model;
using Xunit;

namespace TanssLogWatcher.Monitoring.Tests;

/// <summary>
/// Die Zustandsmaschine, vollständig gegen Attrappen.
/// </summary>
/// <remarks>
/// Geprüft werden die Fälle, die im Betrieb Geld kosten: eine Sitzung, die zu früh endet, eine,
/// die doppelt entsteht, und ein Durchlauf, der wegen eines einzigen Prozesses abbricht.
/// </remarks>
public sealed class SessionEngineTests
{
    private static readonly DateTimeOffset Start = new(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);

    private readonly FakeWindowSource windows = new();
    private readonly FakeProcessSource processes = new();
    private readonly FakeTcpConnectionSource tcp = new();
    private readonly FakeHostNameResolver dns = new();
    private readonly ManualTimeProvider clock = new(Start);

    [Fact]
    public void NeueSitzungWirdErkannt()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();

        SessionRunResult result = engine.Run(Settings("mstsc"));

        WatchedSession session = Assert.Single(result.Started);
        Assert.Empty(result.Ended);
        Assert.Equal("SRV-DC01", session.Destination);
        Assert.Equal(100, session.ProcessId);
        Assert.Equal("mstsc", session.ProcessName);
        Assert.Null(session.EndedAt);
        Assert.Single(engine.ActiveSessions);
    }

    [Fact]
    public void UnveraenderteSitzungUeberlebtMehrereDurchlaeufe()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();

        Guid id = Assert.Single(engine.Run(Settings("mstsc")).Started).SessionId;

        for (int i = 0; i < 3; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(10));
            SessionRunResult result = engine.Run(Settings("mstsc"));
            Assert.Empty(result.Started);
            Assert.Empty(result.Ended);
        }

        Assert.Equal(id, Assert.Single(engine.ActiveSessions).SessionId);
    }

    [Fact]
    public void VerschwundenerProzessBeendetDieSitzung()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();
        engine.Run(Settings("mstsc"));

        clock.Advance(TimeSpan.FromMinutes(12));
        processes.With();
        windows.With();
        SessionRunResult result = engine.Run(Settings("mstsc"));

        WatchedSession ended = Assert.Single(result.Ended);
        Assert.Empty(result.Started);
        Assert.Equal(Start + TimeSpan.FromMinutes(12), ended.EndedAt);
        Assert.Equal(TimeSpan.FromMinutes(12), ended.Duration(clock.GetUtcNow()));
        Assert.Empty(engine.ActiveSessions);
    }

    /// <summary>
    /// Der Platzhalter ist der Grund, warum die Zeit zwischen Programmstart und erstem Fenstertitel
    /// nicht verloren geht. Beim Übernehmen des echten Ziels darf der Beginn sich nicht bewegen.
    /// </summary>
    [Fact]
    public void PlatzhalterAdoptionBehaeltDieStartzeit()
    {
        DateTimeOffset processStart = Start - TimeSpan.FromSeconds(3);
        processes.With(Process(200, "putty", processStart));
        windows.With(new WindowInfo("PuTTY", 200, 1));

        // Der Takt steht hier ausdruecklich: Drei Sekunden gelten nur dann als "gerade
        // gestartet", wenn der Abstand zweier Momentaufnahmen mindestens so gross ist. Mit der
        // Voreinstellung von einer Sekunde begaenne die Sitzung jetzt - was richtig ist, aber
        // eine andere Frage prueft als diese hier.
        SessionEngine engine = Build(new SessionEngineOptions
        {
            SampleInterval = TimeSpan.FromSeconds(10),
        });

        WatchedSession session = Assert.Single(engine.Run(Settings("putty")).Started);
        Assert.True(session.HasNoDestination);
        Assert.Equal(processStart, session.StartedAt);

        clock.Advance(TimeSpan.FromMinutes(4));
        windows.With(new WindowInfo("root@srv01: ~ - PuTTY", 200, 1));
        SessionRunResult second = engine.Run(Settings("putty"));

        Assert.Empty(second.Started);
        Assert.Empty(second.Ended);
        WatchedSession adopted = Assert.Single(engine.ActiveSessions);
        Assert.Equal("root@srv01: ~", adopted.Destination);
        Assert.Equal(processStart, adopted.StartedAt);
        Assert.Equal(session.SessionId, adopted.SessionId);
    }

    [Fact]
    public void TitelwechselMitIgnoreTitleChangesBeendetDieSitzungNicht()
    {
        processes.With(Process(200, "putty", Start));
        windows.With(new WindowInfo("root@srv01: ~ - PuTTY", 200, 1));
        SessionEngine engine = Build();
        Guid id = Assert.Single(engine.Run(Settings("putty")).Started).SessionId;

        clock.Advance(TimeSpan.FromMinutes(1));
        windows.With(new WindowInfo("root@srv01: /var/log - PuTTY", 200, 1));
        SessionRunResult result = engine.Run(Settings("putty"));

        Assert.Empty(result.Started);
        Assert.Empty(result.Ended);
        WatchedSession session = Assert.Single(engine.ActiveSessions);
        Assert.Equal(id, session.SessionId);
        Assert.Equal("root@srv01: /var/log", session.Destination);
    }

    [Fact]
    public void TitelwechselOhneIgnoreTitleChangesBeendetDieSitzung()
    {
        processes.With(Process(100, "mstsc", Start));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();
        engine.Run(Settings("mstsc"));

        clock.Advance(TimeSpan.FromMinutes(1));
        windows.With(new WindowInfo("SRV-FILE02 - Remotedesktopverbindung", 100, 1));
        SessionRunResult result = engine.Run(Settings("mstsc"));

        Assert.Equal("SRV-DC01", Assert.Single(result.Ended).Destination);
        Assert.Equal("SRV-FILE02", Assert.Single(result.Started).Destination);
        Assert.Single(engine.ActiveSessions);
    }

    /// <summary>
    /// Verliert ein Fenster kurz seinen Titel, entsteht im selben Durchlauf ein Ende und ein neuer
    /// Platzhalter. Beides zusammen ist kein Ereignis, sondern Flattern — und wird verworfen.
    /// </summary>
    [Fact]
    public void KurzVerlorenerTitelBeendetDieSitzungNicht()
    {
        processes.With(Process(100, "mstsc", Start));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();
        Guid id = Assert.Single(engine.Run(Settings("mstsc")).Started).SessionId;

        clock.Advance(TimeSpan.FromSeconds(10));
        windows.With(new WindowInfo("Remotedesktopverbindung", 100, 1));
        SessionRunResult flutter = engine.Run(Settings("mstsc"));

        Assert.Empty(flutter.Started);
        Assert.Empty(flutter.Ended);

        clock.Advance(TimeSpan.FromSeconds(10));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionRunResult back = engine.Run(Settings("mstsc"));

        Assert.Empty(back.Started);
        Assert.Empty(back.Ended);
        Assert.Equal(id, Assert.Single(engine.ActiveSessions).SessionId);
    }

    /// <summary>
    /// Die Vorlage legte einen Prozess, auf den zwei Profile passen, zweimal in die Kandidatenliste
    /// — und fragte den Techniker am Ende zweimal nach demselben Kommentar.
    /// </summary>
    [Fact]
    public void ZweiPassendeProfileErzeugenGenauEineSitzung()
    {
        Dictionary<string, MonitoringProfile> catalog = new(StringComparer.Ordinal)
        {
            ["erstes"] = new MonitoringProfile
            {
                Key = "erstes",
                TypeDescription = "Erstes",
                ProcessNames = ["mstsc"],
                DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            },
            ["zweites"] = new MonitoringProfile
            {
                Key = "zweites",
                TypeDescription = "Zweites",
                ProcessNames = ["mstsc"],
                DestinationResolveMethod = DestinationResolveMethod.WindowTitle,
            },
        };

        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01", 100, 1));
        SessionEngine engine = Build(new SessionEngineOptions { Catalog = catalog });

        SessionRunResult result = engine.Run(Settings("erstes", "zweites"));

        WatchedSession session = Assert.Single(result.Started);
        Assert.Equal("erstes", session.ProfileKey);
        Assert.Single(engine.ActiveSessions);
    }

    [Fact]
    public void ZweiFensterMitGleichemTitelErgebenEineSitzung()
    {
        processes.With(Process(100, "cmd"));
        windows.With(new WindowInfo("C:\\Windows", 100, 1), new WindowInfo("C:\\Windows", 100, 2));
        SessionEngine engine = Build();

        Assert.Single(engine.Run(Settings("cmd")).Started);
    }

    /// <summary>
    /// Ein Prozess, der zwischen Momentaufnahme und Auswertung stirbt, kostet genau diesen einen
    /// Prozess. Die Vorlage riss dabei den gesamten Durchlauf mit.
    /// </summary>
    [Fact]
    public void SterbenderProzessBrichtDenDurchlaufNichtAb()
    {
        processes.With(Process(100, "mstsc"), Process(200, "WinSCP"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1),
                     new WindowInfo("WinSCP", 200, 2));
        tcp.Failing.Add(200);
        SessionEngine engine = Build();

        SessionRunResult result = engine.Run(Settings("mstsc", "WinSCP"));

        Assert.Equal("SRV-DC01", Assert.Single(result.Started).Destination);
    }

    /// <summary>
    /// Eine ausgefallene Fensterquelle darf nicht als „alle Fenster weg“ gelesen werden — das
    /// erzeugte einen Schwall falscher Fernwartungen.
    /// </summary>
    [Fact]
    public void AusgefalleneFensterquelleLaesstDenBestandUnberuehrt()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();
        engine.Run(Settings("mstsc"));

        windows.Failure = new InvalidOperationException("Die Fensterliste ist unvollständig.");
        SessionRunResult result = engine.Run(Settings("mstsc"));

        Assert.Empty(result.Started);
        Assert.Empty(result.Ended);
        Assert.Single(engine.ActiveSessions);
    }

    [Fact]
    public void AusgefalleneProzessquelleLaesstDenBestandUnberuehrt()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();
        engine.Run(Settings("mstsc"));

        processes.Failure = new InvalidOperationException("Die Prozessliste ist nicht lesbar.");
        SessionRunResult result = engine.Run(Settings("mstsc"));

        Assert.Empty(result.Ended);
        Assert.Single(engine.ActiveSessions);
    }

    [Fact]
    public void NichtZugeordnetesProfilWirdNichtBeobachtet()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();

        SessionRunResult result = engine.Run(MonitoringProfiles.Reconcile(null));

        Assert.Empty(result.Started);
        Assert.Empty(engine.ActiveSessions);
    }

    [Fact]
    public void UnbekannterProfilschluesselWirdUebergangen()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();

        SessionRunResult result = engine.Run(
            [new MonitoringSetting { Key = "gibt-es-nicht", RemoteSupportTypeId = 1001 }]);

        Assert.Empty(result.Started);
    }

    [Fact]
    public void MonitorOnlyRegexMatchedWindowOhneTrefferErzeugtKeineSitzung()
    {
        processes.With(Process(100, "TeamViewer"));
        windows.With(new WindowInfo("TeamViewer", 100, 1));
        SessionEngine engine = Build();

        Assert.Empty(engine.Run(Settings("TeamViewer")).Started);
        Assert.Empty(engine.ActiveSessions);
    }

    /// <summary>
    /// Verbindungsbasierte Profile bekommen drei Minuten Karenz: in den ersten Minuten redet eine
    /// solche Anwendung mit Lizenz- und Aktualisierungsdiensten, nicht mit dem Kunden.
    /// </summary>
    [Fact]
    public void VerbindungsprofilWartetDieKarenzzeitAb()
    {
        processes.With(Process(200, "WinSCP", Start - TimeSpan.FromMinutes(1)));
        windows.With(new WindowInfo("WinSCP", 200, 2));
        tcp.Set(200, "203.0.113.5");
        SessionEngine engine = Build();

        Assert.Empty(engine.Run(Settings("WinSCP")).Started);

        clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal("[203.0.113.5]", Assert.Single(engine.Run(Settings("WinSCP")).Started).Destination);
    }

    [Fact]
    public void TitelprofilKenntKeineKarenzzeit()
    {
        processes.With(Process(100, "mstsc", Start - TimeSpan.FromSeconds(1)));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();

        Assert.Single(engine.Run(Settings("mstsc")).Started);
    }

    /// <summary>Ohne Fenster findet nur der NoGui-Weg einen Prozess.</summary>
    [Fact]
    public void NoGuiProfilFindetProzessOhneFenster()
    {
        processes.With(Process(100, "mstsc"), Process(400, "openvpn"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        tcp.Set(400, "203.0.113.9");
        SessionEngine engine = Build();

        SessionRunResult result = engine.Run(Settings("mstsc", "OpenVPN"));

        Assert.Equal(2, result.Started.Count);
        Assert.Contains(result.Started, s => s.Destination == "[203.0.113.9]" && s.ProcessId == 400);
    }

    [Fact]
    public void ProfilOhneNoGuiFindetProzessOhneFensterNicht()
    {
        processes.With(Process(200, "WinSCP"));
        windows.With();
        tcp.Set(200, "203.0.113.5");
        SessionEngine engine = Build();

        Assert.Empty(engine.Run(Settings("WinSCP")).Started);
    }

    /// <summary>
    /// Der nachgereichte Rechnername ändert den Anzeigetext, aber nicht den Bezeichner — sonst
    /// endete die Sitzung in dem Moment, in dem der Namensdienst antwortet.
    /// </summary>
    [Fact]
    public void NachgereichterRechnernameBeendetDieSitzungNicht()
    {
        processes.With(Process(200, "WinSCP"));
        windows.With(new WindowInfo("WinSCP", 200, 2));
        tcp.Set(200, "203.0.113.5");
        SessionEngine engine = Build();

        Guid id = Assert.Single(engine.Run(Settings("WinSCP")).Started).SessionId;

        clock.Advance(TimeSpan.FromSeconds(10));
        dns.Names["203.0.113.5"] = "sftp.kunde.example";
        SessionRunResult result = engine.Run(Settings("WinSCP"));

        Assert.Empty(result.Started);
        Assert.Empty(result.Ended);
        WatchedSession session = Assert.Single(engine.ActiveSessions);
        Assert.Equal(id, session.SessionId);
        Assert.Equal("sftp.kunde.example [203.0.113.5]", session.Destination);
    }

    [Fact]
    public void JungerProzessBeginntMitSeinerEchtenStartzeit()
    {
        DateTimeOffset processStart = Start - TimeSpan.FromSeconds(4);
        processes.With(Process(100, "mstsc", processStart));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build(new SessionEngineOptions
        {
            SampleInterval = TimeSpan.FromSeconds(10),
        });

        Assert.Equal(processStart, Assert.Single(engine.Run(Settings("mstsc")).Started).StartedAt);
    }

    /// <summary>
    /// Im Sekundentakt ist „jung“ eine Sekunde. Ein Prozess, der beim ersten Blick schon vier
    /// Sekunden läuft, war beim vorigen Durchlauf noch nicht da — und trotzdem beginnt seine
    /// Sitzung jetzt und nicht rückwirkend.
    /// </summary>
    [Fact]
    public void ImSekundentaktBeginntEinAelterProzessJetzt()
    {
        processes.With(Process(100, "mstsc", Start - TimeSpan.FromSeconds(4)));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build(new SessionEngineOptions
        {
            SampleInterval = TimeSpan.FromSeconds(1),
        });

        Assert.Equal(Start, Assert.Single(engine.Run(Settings("mstsc")).Started).StartedAt);
    }

    /// <summary>
    /// Die Schwelle wächst mit dem tatsächlichen Abstand: Braucht ein Durchlauf länger als der
    /// eingestellte Takt, darf das den echten Beginn der in dieser Zeit gestarteten Sitzungen
    /// nicht kosten.
    /// </summary>
    [Fact]
    public void EinLangsamerDurchlaufVerliertDenEchtenBeginnNicht()
    {
        SessionEngine engine = Build(new SessionEngineOptions
        {
            SampleInterval = TimeSpan.FromSeconds(1),
        });

        // Erster Durchlauf: nichts zu sehen, aber er setzt den Bezugspunkt.
        Assert.Empty(engine.Run(Settings("mstsc")).Started);

        // Der naechste kommt erst acht Sekunden spaeter - ein Rechner unter Last.
        clock.Advance(TimeSpan.FromSeconds(8));

        DateTimeOffset processStart = clock.GetUtcNow() - TimeSpan.FromSeconds(5);
        processes.With(Process(100, "mstsc", processStart));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));

        Assert.Equal(processStart,
                     Assert.Single(engine.Run(Settings("mstsc")).Started).StartedAt);
    }

    /// <summary>
    /// Alles, was beim Start des Werkzeugs bereits lief, beginnt jetzt. Sonst schriebe der erste
    /// Durchlauf eine Fernwartung über die gesamte Laufzeit einer seit Tagen offenen Sitzung.
    /// </summary>
    [Fact]
    public void AlterProzessBeginntJetzt()
    {
        processes.With(Process(100, "mstsc", Start - TimeSpan.FromHours(9)));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();

        Assert.Equal(Start, Assert.Single(engine.Run(Settings("mstsc")).Started).StartedAt);
    }

    [Fact]
    public void UnbekannteStartzeitBeginntJetzt()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();

        Assert.Equal(Start, Assert.Single(engine.Run(Settings("mstsc")).Started).StartedAt);
    }

    [Fact]
    public void EndAllSchliesstAlleLaufendenSitzungen()
    {
        processes.With(Process(100, "mstsc"), Process(200, "putty"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1),
                     new WindowInfo("root@srv01 - PuTTY", 200, 2));
        SessionEngine engine = Build();
        engine.Run(Settings("mstsc", "putty"));

        clock.Advance(TimeSpan.FromMinutes(5));
        IReadOnlyList<WatchedSession> ended = engine.EndAll();

        Assert.Equal(2, ended.Count);
        Assert.All(ended, s => Assert.Equal(Start + TimeSpan.FromMinutes(5), s.EndedAt));
        Assert.Empty(engine.ActiveSessions);
    }

    /// <summary>
    /// Der Zeitgeber der Anwendung und ein Beenden von Hand können gleichzeitig laufen. Über alle
    /// nebenläufigen Durchläufe hinweg darf genau eine Sitzung entstehen.
    /// </summary>
    [Fact]
    public void NebenlaeufigeDurchlaeufeErzeugenGenauEineSitzung()
    {
        processes.With(Process(100, "mstsc"));
        windows.With(new WindowInfo("SRV-DC01 - Remotedesktopverbindung", 100, 1));
        SessionEngine engine = Build();
        MonitoringSetting[] settings = Settings("mstsc");

        int started = 0;
        Parallel.For(0, 64, _ =>
        {
            SessionRunResult result = engine.Run(settings);
            Interlocked.Add(ref started, result.Started.Count);
        });

        Assert.Equal(1, started);
        Assert.Single(engine.ActiveSessions);
    }

    /// <summary>
    /// Der Platzhalter übernimmt auch das Fensterhandle — sonst greift die handlebasierte Übernahme
    /// danach nie mehr.
    /// </summary>
    /// <remarks>
    /// Gemessener Fall mit dem Profil <c>putty</c>: der Platzhalter entsteht am Hauptfenster 5, das
    /// Ziel kommt aus Fenster 7. Bleibt das Handle bei 5 stehen, spaltet die nächste Umbenennung von
    /// Fenster 7 die Sitzung in zwei — trotz <c>IgnoreTitleChanges</c>. Aus einer Fernwartung würden
    /// zwei, und TANSS dedupliziert nicht.
    /// </remarks>
    [Fact]
    public void PlatzhalterAdoptionUebernimmtAuchDasFensterhandle()
    {
        processes.With(Process(200, "putty", Start));
        windows.With(new WindowInfo("PuTTY", 200, 5), new WindowInfo("Alt", 200, 7));
        SessionEngine engine = Build();
        WatchedSession placeholder = Assert.Single(engine.Run(Settings("putty")).Started);
        Assert.True(placeholder.HasNoDestination);
        Assert.Equal(5, placeholder.WindowHandle);

        clock.Advance(TimeSpan.FromMinutes(1));
        windows.With(new WindowInfo("PuTTY", 200, 5), new WindowInfo("root@srv01: ~ - PuTTY", 200, 7));
        engine.Run(Settings("putty"));
        Assert.Equal(7, Assert.Single(engine.ActiveSessions).WindowHandle);

        clock.Advance(TimeSpan.FromMinutes(1));
        windows.With(new WindowInfo("PuTTY", 200, 5), new WindowInfo("root@srv01: /var - PuTTY", 200, 7));
        SessionRunResult third = engine.Run(Settings("putty"));

        Assert.Empty(third.Started);
        Assert.Empty(third.Ended);
        WatchedSession session = Assert.Single(engine.ActiveSessions);
        Assert.Equal(placeholder.SessionId, session.SessionId);
        Assert.Equal("root@srv01: /var", session.Destination);
    }

    /// <summary>
    /// Hauptfenster ist das erste <b>besitzerlose</b> Fenster, nicht das oberste der Z-Reihenfolge.
    /// </summary>
    /// <remarks>
    /// Mit <c>MonitorOnlyMainWindowHandle</c> (devenv, BASEClient) machte ein obenauf liegender
    /// Dialog sonst sich selbst zum Hauptfenster und unterdrückte das echte — die Sitzung fiel weg
    /// oder begann neu.
    /// </remarks>
    [Fact]
    public void HauptfensterIstDasBesitzerloseFensterNichtDasOberste()
    {
        processes.With(Process(300, "devenv", Start));
        windows.With(new WindowInfo("Suchen und Ersetzen", 300, 11, IsOwned: true),
                     new WindowInfo("Kunde.sln - Microsoft Visual Studio", 300, 12));
        SessionEngine engine = Build();

        WatchedSession session = Assert.Single(engine.Run(Settings("devenv")).Started);

        Assert.Equal("Kunde.sln", session.Destination);
        Assert.Equal(12, session.WindowHandle);
    }

    /// <summary>
    /// Schließt der Dialog wieder, bleibt die Sitzung dieselbe. Ohne die Besitzerprüfung wanderte
    /// das vermeintliche Hauptfenster mit jedem Dialog und beendete die Sitzung.
    /// </summary>
    [Fact]
    public void EinDialogVerschiebtDasHauptfensterNicht()
    {
        processes.With(Process(300, "devenv", Start));
        windows.With(new WindowInfo("Kunde.sln - Microsoft Visual Studio", 300, 12));
        SessionEngine engine = Build();
        Guid id = Assert.Single(engine.Run(Settings("devenv")).Started).SessionId;

        clock.Advance(TimeSpan.FromMinutes(1));
        windows.With(new WindowInfo("Optionen", 300, 13, IsOwned: true),
                     new WindowInfo("Kunde.sln - Microsoft Visual Studio", 300, 12));
        SessionRunResult result = engine.Run(Settings("devenv"));

        Assert.Empty(result.Started);
        Assert.Empty(result.Ended);
        Assert.Equal(id, Assert.Single(engine.ActiveSessions).SessionId);
    }

    /// <summary>
    /// Ein unübersetzbarer eigener Ausdruck erzeugt keine Sitzung — und hält den Durchlauf nicht auf.
    /// </summary>
    [Fact]
    public void UnuebersetzbarerAusdruckErzeugtKeineSitzungUndBrichtDenDurchlaufNichtAb()
    {
        processes.With(Process(100, "TeamViewer", Start), Process(200, "putty", Start));
        windows.With(new WindowInfo("TeamViewer", 100, 1), new WindowInfo("Chat", 100, 2),
                     new WindowInfo("root@srv01: ~ - PuTTY", 200, 3));
        SessionEngine engine = Build();

        SessionRunResult result = engine.Run(
        [
            new MonitoringSetting
            {
                Key = "TeamViewer",
                RemoteSupportTypeId = 1001,
                RegexPatternOverride = "(unvollstaendig",
            },
            new MonitoringSetting { Key = "putty", RemoteSupportTypeId = 1001 },
        ]);

        WatchedSession session = Assert.Single(result.Started);
        Assert.Equal(200, session.ProcessId);
        Assert.DoesNotContain(engine.ActiveSessions, s => s.ProcessId == 100);
    }

    private SessionEngine Build(SessionEngineOptions? options = null) =>
        new(windows, processes, new DestinationResolver(tcp, dns), clock, null, options);

    private static ProcessInfo Process(int id, string name, DateTimeOffset? startedAt = null) =>
        new(id, 4, name, startedAt);

    private static MonitoringSetting[] Settings(params string[] keys) =>
        [.. keys.Select(key => new MonitoringSetting { Key = key, RemoteSupportTypeId = 1001 })];
}
