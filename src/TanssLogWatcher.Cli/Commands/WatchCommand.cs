using System.Globalization;
using System.Runtime.Versioning;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Cli.Output;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.Config;
using TanssLogWatcher.Storage.Logging;
using TanssLogWatcher.Storage.Queue;
using TanssLogWatcher.Storage.Secrets;

namespace TanssLogWatcher.Cli.Commands;

/// <summary>Was <c>watch</c> braucht.</summary>
public sealed record WatchInputs
{
    /// <summary>Die geprüfte Konfiguration.</summary>
    public required AppConfig Config { get; init; }

    /// <summary>Die Zustandsmaschine der Sitzungserkennung.</summary>
    public required SessionEngine Engine { get; init; }

    /// <summary>Die Warteschlange — hier landet jede abgeschlossene Sitzung zuerst.</summary>
    public required IUploadQueue Queue { get; init; }

    /// <summary>Die abgeglichenen Beobachtungseinstellungen.</summary>
    public required IReadOnlyList<MonitoringSetting> Settings { get; init; }

    /// <summary>Das Änderungsprotokoll; <c>null</c> schaltet es ab.</summary>
    public SessionLog? Log { get; init; }

    /// <summary>Der Schlüsselspeicher für Abdrücke; <c>null</c> lässt die Meldung dazu weg.</summary>
    public FingerprintKeyStore? Keys { get; init; }

    /// <summary>Die Uhr; für Tests einsetzbar.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>Was ein Beobachtungstakt bewirkt hat.</summary>
/// <param name="Started">Neu erkannte Sitzungen.</param>
/// <param name="Enqueued">Abgeschlossene Sitzungen, die in die Warteschlange gingen.</param>
/// <param name="Skipped">
/// Abgeschlossene Sitzungen ohne Zuordnung oder im Trockenlauf — sie gehen nirgendwohin.
/// </param>
public sealed record WatchCycleResult(int Started, int Enqueued, int Skipped);

/// <summary>
/// Beobachtet laufend und legt fertige Sitzungen in die Warteschlange.
/// </summary>
/// <remarks>
/// <para><b>Der Trockenlauf ist der Standard, solange keine Zuordnung vorliegt.</b> Ein
/// Werkzeug, das ungefragt Fenstertitel von Kundenrechnern in ein Ticketsystem schreibt, wäre
/// schon beim ersten Start ein Datenschutzvorfall. Gebucht wird erst, wenn jemand ein Profil
/// ausdrücklich auf einen Fernwartungstyp abgebildet hat.</para>
///
/// <para><b>Strg-C beendet geordnet.</b> Laufende Sitzungen werden abgeschlossen und
/// eingereiht, nicht verworfen — der Mangel der Vorlage, der bares Geld kostete. Der
/// Abbruchweg ist deshalb kein Notausgang, sondern der vorgesehene Weg hinaus.</para>
///
/// <para><b>Erst auf die Platte, dann ins Netz.</b> Eine abgeschlossene Sitzung geht in die
/// Warteschlange und wird von dort gesendet. Dieser Befehl selbst spricht nie mit TANSS; das
/// tut <c>queue --flush</c>. So kostet ein Netzfehler keine Sitzung.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public static class WatchCommand
{
    /// <summary>Beobachtet, bis abgebrochen wird.</summary>
    /// <param name="inputs">Die Bausteine.</param>
    /// <param name="dryRunRequested">Ob <c>--dry-run</c> angegeben wurde.</param>
    /// <param name="writer">Wohin die fortlaufende Anzeige geht.</param>
    /// <param name="ct">Abbruchmarke; sie kommt üblicherweise von Strg-C.</param>
    /// <returns>0 nach geordnetem Ende.</returns>
    public static async Task<int> RunAsync(WatchInputs inputs, bool dryRunRequested,
                                           TextWriter writer, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(writer);

        IReadOnlyList<MonitoringSetting> active =
            [.. inputs.Settings.Where(setting => setting.IsActive)];

        // Ohne Zuordnung ist der Trockenlauf nicht eine Vorsichtsmassnahme, sondern die
        // einzige moegliche Betriebsart: Es gaebe keinen Fernwartungstyp, auf den zu buchen
        // waere.
        bool dryRun = dryRunRequested || active.Count == 0;

        WriteHeader(inputs, writer, dryRun, active, dryRunRequested);
        ReportFingerprintKey(inputs, writer);

        if (!dryRun)
        {
            RecoverOpenSessions(inputs, writer);
        }

        TimeSpan interval = TimeSpan.FromSeconds(inputs.Config.Watcher.PollIntervalSeconds);

        try
        {
            while (!ct.IsCancellationRequested)
            {
                _ = RunCycle(inputs, dryRun, writer);
                await Task.Delay(interval, inputs.Clock, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // Der vorgesehene Weg hinaus, kein Fehler.
        }

        Shutdown(inputs, dryRun, writer);
        return ExitCode.Healthy;
    }

    /// <summary>
    /// Ein einzelner Beobachtungstakt.
    /// </summary>
    /// <remarks>
    /// Öffentlich, damit sich das Verhalten ohne Uhrläufe prüfen lässt: Ein Takt ist die
    /// Einheit, in der dieser Befehl arbeitet, und ein Test, der dafür sekundenlang warten
    /// müsste, würde nicht geschrieben.
    /// </remarks>
    /// <param name="inputs">Die Bausteine.</param>
    /// <param name="dryRun">Nichts schreiben, nur zeigen.</param>
    /// <param name="writer">Wohin die Anzeige geht.</param>
    public static WatchCycleResult RunCycle(WatchInputs inputs, bool dryRun, TextWriter writer)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(writer);

        DateTimeOffset now = inputs.Clock.GetUtcNow();
        SessionRunResult run = inputs.Engine.Run(inputs.Settings);

        foreach (WatchedSession session in run.Started)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Report.Clock(now)}  BEGONNEN  {session.ProfileKey} (PID {session.ProcessId})  "
                + $"{Report.Ellipsis(session.Destination, 60)}"));

            if (!dryRun)
            {
                // Fortgeschrieben mit jedem Takt: Nach einem Absturz ist StartedAt sonst
                // verloren - aus der Prozessliste ist der Beginn rueckwirkend nicht zu holen.
                inputs.Queue.SaveOpenSession(ToOpenSession(inputs, session, now));
            }

            Log(inputs, session, dryRun ? SessionOutcome.DryRun : SessionOutcome.Ok,
                "Sitzung erkannt.", SessionTrigger.Watcher);
        }

        // Laufende Sitzungen mitschreiben, damit LastSeenAt stimmt. Es ist nach einem Absturz
        // die einzige belastbare Aussage ueber das Ende - die aktuelle Uhrzeit zu nehmen hiesse,
        // dem Kunden die Zeit zu berechnen, in der der Rechner aus war.
        if (!dryRun)
        {
            foreach (WatchedSession session in inputs.Engine.ActiveSessions)
            {
                inputs.Queue.SaveOpenSession(ToOpenSession(inputs, session, now));
            }
        }

        int enqueued = 0;
        int skipped = 0;
        foreach (WatchedSession session in run.Ended)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Report.Clock(now)}  BEENDET   {session.ProfileKey} (PID {session.ProcessId})  "
                + $"{Report.Elapsed(session.Duration(now))}  "
                + $"{Report.Ellipsis(session.Destination, 48)}"));

            if (Finish(inputs, session, dryRun, now, writer, SessionTrigger.Watcher))
            {
                enqueued++;
            }
            else
            {
                skipped++;
            }
        }

        return new WatchCycleResult(run.Started.Count, enqueued, skipped);
    }

    /// <summary>Die Kopfzeilen: Betriebsart, Takt, überwachte Profile.</summary>
    private static void WriteHeader(WatchInputs inputs, TextWriter writer, bool dryRun,
                                    IReadOnlyList<MonitoringSetting> active, bool requested)
    {
        writer.WriteLine("Beobachtung läuft. Strg-C beendet geordnet — laufende Sitzungen werden");
        writer.WriteLine("abgeschlossen und gesichert, nicht verworfen.");
        writer.WriteLine();
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Takt: {inputs.Config.Watcher.PollIntervalSeconds} s   "
            + $"Schonfrist: {inputs.Config.Watcher.IpSessionGraceSeconds} s"));

        if (dryRun)
        {
            writer.WriteLine(requested
                ? "Betriebsart: TROCKENLAUF (--dry-run). Es wird nichts eingereiht und nichts "
                  + "nach TANSS geschrieben."
                : "Betriebsart: TROCKENLAUF. Keine Beobachtungsregel trägt eine Zuordnung auf "
                  + "einen Fernwartungstyp — es gäbe nichts, worauf gebucht werden könnte. "
                  + "Zuzuordnen ist sie in monitoring[].remote_support_type_id.");
        }
        else
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"Betriebsart: SCHARF. {active.Count} Profil(e) sind zugeordnet; abgeschlossene "
                + $"Sitzungen gehen in die Warteschlange und von dort mit „queue --flush“ nach "
                + $"TANSS."));

            foreach (MonitoringSetting setting in active)
            {
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"  {setting.Key} → Fernwartungstyp {setting.RemoteSupportTypeId}"));
            }
        }

        writer.WriteLine();
    }

    /// <summary>
    /// Meldet einen ersetzten Abdruckschlüssel.
    /// </summary>
    /// <remarks>
    /// Ab diesem Punkt passen die Abdrücke im Änderungsprotokoll nicht mehr zu den bisherigen
    /// Einträgen. Wer das nicht weiss, hält zwei Einträge desselben Fensters für zwei Fenster —
    /// deshalb wird es genau einmal beim Start gesagt.
    /// <para>Gefragt wird nur, wenn bereits eine Schlüsseldatei besteht. Sonst legte dieser
    /// Befehl sie an, obwohl vielleicht nie eine Beschriftung protokolliert wird — und das
    /// Anlegen beim ersten Bedarf ist ausdrücklich so gewollt.</para>
    /// </remarks>
    private static void ReportFingerprintKey(WatchInputs inputs, TextWriter writer)
    {
        if (inputs.Keys is not { } keys || !keys.Exists())
        {
            return;
        }

        _ = keys.Load(out FingerprintKeyOrigin origin);
        if (origin != FingerprintKeyOrigin.Replaced)
        {
            return;
        }

        string notice =
            "Der Schlüssel für die Abdrücke von Fensterbeschriftungen war unlesbar und wurde "
            + "ersetzt (der bisherige liegt als .bak daneben). Ab jetzt ergeben dieselben "
            + "Fenster andere Abdrücke als in den bisherigen Protokolleinträgen; zwei Einträge "
            + "desselben Fensters sehen darin aus wie zwei verschiedene Fenster.";

        foreach (string line in Report.Wrap(notice, 92))
        {
            writer.WriteLine(line);
        }

        writer.WriteLine();

        _ = inputs.Log?.Append(new SessionLogEntry
        {
            Operation = "watch.start",
            Outcome = SessionOutcome.Skipped,
            Reason = notice,
            Trigger = SessionTrigger.Startup,
        });
    }

    /// <summary>
    /// Schliesst Sitzungen ab, die ein früherer Lauf offen zurückgelassen hat.
    /// </summary>
    /// <remarks>
    /// Als Ende gilt <see cref="OpenSession.LastSeenAt"/> und nicht jetzt: Der letzte
    /// beobachtete Augenblick ist nach einem Absturz die einzige belastbare Aussage. Sie
    /// fortzusetzen wäre die Alternative — aber der Prozess von damals ist längst fort, und
    /// eine wiederbelebte Sitzung berechnete dem Kunden die Zeit, in der der Rechner aus war.
    /// </remarks>
    private static void RecoverOpenSessions(WatchInputs inputs, TextWriter writer)
    {
        IReadOnlyList<OpenSession> open = inputs.Queue.LoadOpenSessions();
        if (open.Count == 0)
        {
            return;
        }

        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"{open.Count} Sitzung(en) hat ein früherer Lauf offen zurückgelassen. Sie werden "
            + $"mit dem letzten beobachteten Zeitpunkt als Ende abgeschlossen und eingereiht."));

        foreach (OpenSession session in open)
        {
            RemoteSupportWrite payload = new()
            {
                TypeId = session.RemoteSupportTypeId,
                EmployeeId = inputs.Config.Tanss.EmployeeId,
                StartTime = TanssTime.ToUnixSeconds(session.StartedAt),
                EndTime = TanssTime.ToUnixSeconds(session.LastSeenAt),
                RemoteMaintenanceId = session.RemoteMaintenanceId,
                Comment = session.Comment,
                DeviceName = session.DeviceName ?? string.Empty,
                UserName = session.UserName ?? string.Empty,
                TicketId = session.TicketId,
            };

            bool added = inputs.Queue.Enqueue(payload);
            _ = inputs.Queue.RemoveOpenSession(session.RemoteMaintenanceId);

            _ = inputs.Log?.Append(new SessionLogEntry
            {
                Operation = "watch.recover",
                Outcome = added ? SessionOutcome.Deferred : SessionOutcome.Skipped,
                Reason = added
                    ? "Aus einem früheren Lauf übernommen und eingereiht; Ende ist der letzte "
                      + "beobachtete Zeitpunkt."
                    : "Aus einem früheren Lauf übernommen; die Kennung stand bereits in der "
                      + "Warteschlange und wurde nicht erneut aufgenommen.",
                Trigger = SessionTrigger.Startup,
                RemoteMaintenanceId = session.RemoteMaintenanceId,
            });
        }

        writer.WriteLine();
    }

    /// <summary>Beendet alle laufenden Sitzungen und reiht sie ein.</summary>
    private static void Shutdown(WatchInputs inputs, bool dryRun, TextWriter writer)
    {
        DateTimeOffset now = inputs.Clock.GetUtcNow();
        IReadOnlyList<WatchedSession> ended = inputs.Engine.EndAll();

        writer.WriteLine();
        writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"Beendet. {ended.Count} laufende Sitzung(en) werden abgeschlossen."));

        foreach (WatchedSession session in ended)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"{Report.Clock(now)}  BEENDET   {session.ProfileKey} (PID {session.ProcessId})  "
                + $"{Report.Elapsed(session.Duration(now))}  "
                + $"{Report.Ellipsis(session.Destination, 48)}"));

            _ = Finish(inputs, session, dryRun, now, writer, SessionTrigger.Shutdown);
        }

        if (!dryRun && ended.Count > 0)
        {
            writer.WriteLine("Die Einträge liegen in der Warteschlange. Sie gehen mit "
                + "„tanss-logwatch queue --flush“ nach TANSS.");
        }
    }

    /// <summary>
    /// Reiht eine abgeschlossene Sitzung ein — oder sagt, warum nicht.
    /// </summary>
    /// <returns><c>true</c>, wenn sie in die Warteschlange ging.</returns>
    private static bool Finish(WatchInputs inputs, WatchedSession session, bool dryRun,
                               DateTimeOffset now, TextWriter writer, SessionTrigger trigger)
    {
        MonitoringSetting? setting = FindSetting(inputs, session.ProfileKey);
        string id = session.SessionId.ToString();

        if (setting is null || !setting.IsActive)
        {
            Log(inputs, session, SessionOutcome.Skipped,
                "Keine Zuordnung auf einen Fernwartungstyp; nichts eingereiht.", trigger);
            return false;
        }

        if (dryRun)
        {
            writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                $"            Trockenlauf: würde Fernwartungstyp {setting.RemoteSupportTypeId} "
                + $"buchen, {Report.Elapsed(session.Duration(now))}, Kennung {id}."));
            Log(inputs, session, SessionOutcome.DryRun,
                "Trockenlauf; nichts eingereiht und nichts nach TANSS geschrieben.", trigger);
            return false;
        }

        try
        {
            bool added = inputs.Queue.Enqueue(ToPayload(inputs, session, setting, now));
            _ = inputs.Queue.RemoveOpenSession(id);

            Log(inputs, session, added ? SessionOutcome.Deferred : SessionOutcome.Skipped,
                added
                    ? "Eingereiht; geht mit dem nächsten Sendelauf nach TANSS."
                    : "Die Kennung stand bereits in der Warteschlange; nicht erneut aufgenommen.",
                trigger);

            return added;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Diese eine Sitzung geht schief, der Beobachtungslauf nicht.
            writer.WriteLine("            Konnte nicht eingereiht werden: " + ex.Message);
            Log(inputs, session, SessionOutcome.Error,
                "Einreihen fehlgeschlagen: " + ex.Message, trigger);
            return false;
        }
    }

    /// <summary>Baut die Nutzlast, die später unverändert an TANSS geht.</summary>
    /// <remarks>
    /// Sie entsteht genau einmal, beim Abschluss der Sitzung. Würde sie beim Senden
    /// nachgerechnet, hinge sie an Einstellungen, die sich seither geändert haben könnten —
    /// und die hochgeladene Fernwartung wäre nicht mehr die beobachtete.
    /// </remarks>
    private static RemoteSupportWrite ToPayload(WatchInputs inputs, WatchedSession session,
                                                MonitoringSetting setting, DateTimeOffset now) =>
        new()
        {
            TypeId = setting.RemoteSupportTypeId,
            EmployeeId = inputs.Config.Tanss.EmployeeId,
            StartTime = TanssTime.ToUnixSeconds(session.StartedAt),
            EndTime = TanssTime.ToUnixSeconds(session.EndedAt ?? now),
            RemoteMaintenanceId = session.SessionId.ToString(),
            Comment = Describe(session),
            DeviceName = Report.Ellipsis(session.Destination, 120),
        };

    /// <summary>Die laufende Sitzung, so wie sie auf der Platte liegt.</summary>
    private static OpenSession ToOpenSession(WatchInputs inputs, WatchedSession session,
                                             DateTimeOffset now)
    {
        MonitoringSetting? setting = FindSetting(inputs, session.ProfileKey);

        return new OpenSession
        {
            RemoteMaintenanceId = session.SessionId.ToString(),
            MonitorKey = session.ProfileKey,
            RemoteSupportTypeId = setting?.RemoteSupportTypeId ?? MonitoringSetting.NotMonitored,
            StartedAt = session.StartedAt,
            LastSeenAt = now,
            ProcessId = session.ProcessId,
            Target = session.Destination,
            DeviceName = Report.Ellipsis(session.Destination, 120),
            Comment = Describe(session),
        };
    }

    /// <summary>Der Kommentar, den TANSS an der Fernwartung zeigt.</summary>
    private static string Describe(WatchedSession session)
    {
        MonitoringProfile? profile = MonitoringProfiles.Find(session.ProfileKey);
        string what = profile?.TypeDescription ?? session.ProfileKey;
        return $"{what}: {session.Destination}";
    }

    private static MonitoringSetting? FindSetting(WatchInputs inputs, string profileKey) =>
        inputs.Settings.FirstOrDefault(
            setting => string.Equals(setting.Key, profileKey, StringComparison.Ordinal));

    /// <summary>
    /// Schreibt einen Protokolleintrag samt Fensterbeschriftung.
    /// </summary>
    /// <remarks>
    /// Die Beschriftung wandert ausdrücklich in <see cref="SessionLogEntry.WindowTitle"/> und
    /// nicht in den Begründungstext: Nur dieses Feld unterliegt
    /// <c>logging.redact_window_titles</c>. Im Begründungstext stünde sie im Klartext in
    /// <c>state.db</c>, und zwar unabhängig davon, was eingestellt ist.
    /// </remarks>
    private static void Log(WatchInputs inputs, WatchedSession session, SessionOutcome outcome,
                            string reason, SessionTrigger trigger)
    {
        if (inputs.Log is not { } log)
        {
            return;
        }

        _ = log.Append(new SessionLogEntry
        {
            Operation = "watch",
            Outcome = outcome,
            Reason = reason,
            Trigger = trigger,
            RemoteMaintenanceId = session.SessionId.ToString(),
            WindowTitle = session.Destination,
        });
    }
}
