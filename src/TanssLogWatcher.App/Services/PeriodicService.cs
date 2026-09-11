using System.Runtime.Versioning;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Das gemeinsame Gerüst der drei Hintergrunddienste: Takt, Abschaltbarkeit, Fehlerriegel.
/// </summary>
/// <remarks>
/// <para><b>Der Fehlerriegel steht hier und nicht in den Diensten.</b> Er ist die Umsetzung von
/// Hausregel 5 und darf nicht davon abhängen, dass drei Stellen ihn gleich gut schreiben: Ein
/// Takt, der wirft, wird gemeldet und der Dienst läuft weiter. Ohne diesen Riegel hielte ein
/// einziger kaputter Eintrag den Dienst an, der ihn hätte hinausschaffen sollen.</para>
///
/// <para><b>Der Takt läuft zuerst und wartet danach.</b> Umgekehrt geschähe nach dem Start
/// eine Viertelstunde lang nichts — beim Tokendienst wäre das ein ganzer Tag, und ein
/// abgelaufenes Token bliebe abgelaufen.</para>
///
/// <para><b>Der Zusammenbau wird bei jedem Takt neu erfragt.</b> Er ist <c>null</c>, solange
/// nichts eingerichtet ist, und nach einem erneuten Laden ein anderer. Ein Dienst, der ihn
/// sich einmal merkte, arbeitete nach dem Berichtigen der <c>config.json</c> weiter gegen die
/// alte Instanz — und schriebe womöglich in die Datenbank, die niemand mehr liest.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public abstract class PeriodicService : IBackgroundService, IDisposable
{
    private readonly object _gate = new();
    private CancellationTokenSource? _shutdown;
    private Task? _loop;
    private RuntimeComposition? _primed;
    private long _cycles;
    private bool _enabled = true;
    private bool _disposed;

    /// <summary>Nimmt die gemeinsamen Bausteine auf.</summary>
    /// <param name="context">Der Zugang zu Zustand und Zusammenbau.</param>
    protected PeriodicService(IRuntimeContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Activity = new ServiceActivity
        {
            State = ServiceState.Stopped,
            Message = "Noch nicht gestartet.",
        };
    }

    /// <inheritdoc />
    public event EventHandler<ServiceActivity>? ActivityChanged;

    /// <inheritdoc />
    public abstract string Name { get; }

    /// <inheritdoc />
    public abstract string Description { get; }

    /// <inheritdoc />
    public ServiceActivity Activity { get; private set; }

    /// <inheritdoc />
    public bool IsEnabled
    {
        get => _enabled;
        set
        {
            if (_enabled == value)
            {
                return;
            }

            _enabled = value;

            // Sofort melden und nicht erst beim naechsten Takt: Ein Schalter, der bis zu zehn
            // Sekunden lang nichts sichtbar bewirkt, wird ein zweites Mal betaetigt.
            Report(value
                ? new ServiceActivity
                {
                    State = ServiceState.Idle,
                    Message = "Eingeschaltet; arbeitet ab dem nächsten Takt.",
                    Cycles = Interlocked.Read(ref _cycles),
                }
                : new ServiceActivity
                {
                    State = ServiceState.Paused,
                    Message = "Von Hand abgeschaltet. Ein laufender Takt wird noch zu Ende "
                        + "geführt; danach ruht dieser Dienst, bis er wieder eingeschaltet wird.",
                    Cycles = Interlocked.Read(ref _cycles),
                });
        }
    }

    /// <summary>Der Zugang zu Zustand und Zusammenbau.</summary>
    protected IRuntimeContext Context { get; }

    /// <summary>Der Abstand zwischen zwei Takten.</summary>
    protected abstract TimeSpan Interval { get; }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            if (_loop is not null)
            {
                return Task.CompletedTask;
            }

            _shutdown = CancellationTokenSource.CreateLinkedTokenSource(ct);
            CancellationToken token = _shutdown.Token;

            // Task.Run und nicht einfach aufrufen: Der erste Takt laeuft sonst auf dem Strang
            // der Oberflaeche bis zum ersten echten await - und der erste Takt des
            // Sitzungsdienstes mustert die gesamte Prozessliste durch.
            _loop = Task.Run(() => LoopAsync(token), CancellationToken.None);
        }

        Report(new ServiceActivity
        {
            State = ServiceState.Idle,
            Message = "Gestartet.",
        });

        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task StopAsync(CancellationToken ct = default)
    {
        Task? loop;
        CancellationTokenSource? shutdown;

        lock (_gate)
        {
            loop = _loop;
            shutdown = _shutdown;
            _loop = null;
            _shutdown = null;
        }

        if (shutdown is not null)
        {
            await shutdown.CancelAsync().ConfigureAwait(false);
        }

        if (loop is not null)
        {
            try
            {
                await loop.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Der Aufrufer wartet nicht laenger. Das Aufraeumen unten laeuft trotzdem -
                // es ist der Punkt, an dem laufende Sitzungen gesichert werden.
            }
        }

        shutdown?.Dispose();

        // Ausdruecklich AUSSERHALB der Abbruchmarke: Das geordnete Ende ist der vorgesehene
        // Weg hinaus und nicht der Notausgang. Wer hier abbrechen liesse, verlöre genau die
        // laufenden Sitzungen, die dieses Werkzeug aufbewahren soll.
        if (Context.Composition is { } composition)
        {
            try
            {
                await ShutdownAsync(composition).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                Report(new ServiceActivity
                {
                    State = ServiceState.Faulted,
                    Message = "Beim geordneten Beenden ist etwas schiefgegangen. Was bis dahin "
                        + "gesichert wurde, liegt in der Warteschlange.",
                    LastError = Redaction.Scrub(ex.Message),
                    Cycles = Interlocked.Read(ref _cycles),
                });

                return;
            }
        }

        Report(new ServiceActivity
        {
            State = ServiceState.Stopped,
            Message = "Beendet.",
            Cycles = Interlocked.Read(ref _cycles),
        });
    }

    /// <summary>Gibt die Abbruchmarke frei.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Ein einzelner Takt.
    /// </summary>
    /// <remarks>
    /// Gibt den Satz zurück, der danach als Stand angezeigt wird. Ausdrücklich ein Rückgabewert
    /// und kein Seiteneffekt: So kann kein Dienst vergessen, sich zu melden, und keiner bleibt
    /// nach getaner Arbeit auf „Arbeitet“ stehen.
    /// </remarks>
    /// <param name="composition">Die Bausteine; niemals <c>null</c>, wenn diese Stelle läuft.</param>
    /// <param name="ct">Abbruchmarke.</param>
    protected abstract Task<string> RunCycleAsync(RuntimeComposition composition,
                                                  CancellationToken ct);

    /// <summary>
    /// Einmalig vor dem ersten Takt mit einem bestimmten Zusammenbau.
    /// </summary>
    /// <remarks>
    /// Hier steht, was ein früherer Lauf hinterlassen hat: offene Sitzungen, hängengebliebene
    /// Warteschlangeneinträge. Läuft erneut, sobald ein <b>anderer</b> Zusammenbau in Kraft
    /// tritt — nach dem Berichtigen der Konfiguration ist das eine andere Datenbank und
    /// womöglich eine andere Instanz.
    /// </remarks>
    /// <param name="composition">Die Bausteine.</param>
    /// <param name="ct">Abbruchmarke.</param>
    protected virtual Task PrimeAsync(RuntimeComposition composition, CancellationToken ct) =>
        Task.CompletedTask;

    /// <summary>
    /// Das geordnete Ende. Läuft ohne Abbruchmarke.
    /// </summary>
    /// <param name="composition">Die Bausteine.</param>
    protected virtual Task ShutdownAsync(RuntimeComposition composition) => Task.CompletedTask;

    /// <summary>Was gemeldet wird, wenn ein Takt mit einer Ausnahme abbricht.</summary>
    /// <remarks>
    /// Überschreibbar, damit die Meldung sagt, was dieser eine Fehlschlag bedeutet — beim
    /// Sitzungsdienst steht eine erkannte Sitzung auf dem Spiel, beim Sendedienst nur ein
    /// verspäteter Versuch.
    /// </remarks>
    protected virtual string FaultMessage =>
        "Der letzte Takt ist mit einem unerwarteten Fehler abgebrochen. Der Dienst läuft "
        + "weiter; der nächste Takt kann gelingen. Bleibt es dabei, gehört die Meldung in eine "
        + "Fehlermeldung an den Hersteller.";

    /// <summary>Was gemeldet wird, wenn nichts eingerichtet ist.</summary>
    /// <remarks>
    /// Überschreibbar, weil jeder Dienst etwas anderes zu sagen hat: Der Sitzungsdienst
    /// beobachtet dann gar nicht, der Sendedienst hat nichts zu senden, der Tokendienst kennt
    /// keine Instanz. Ein für alle gleicher Satz wäre für keinen richtig.
    /// </remarks>
    protected virtual string NotConfiguredMessage =>
        "Ruht: Es liegt keine gültige Konfiguration vor. Der Betriebszustand nennt, was fehlt.";

    /// <summary>Gibt die Abbruchmarke frei.</summary>
    /// <param name="disposing">Wird von <see cref="Dispose()"/> aufgerufen?</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            _shutdown?.Dispose();
            _shutdown = null;
        }
    }

    /// <summary>Setzt den Stand und meldet ihn auf dem Strang der Oberfläche.</summary>
    /// <param name="activity">Der neue Stand.</param>
    protected void Report(ServiceActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        Activity = activity;
        Context.Notifier.Raise(ActivityChanged, this, activity);
    }

    /// <summary>Der Takt selbst: arbeiten, warten, wiederholen — und niemals daran sterben.</summary>
    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await StepAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Der letzte Riegel. Was hier ankommt, ist ein Programmfehler - er kostet
                // diesen einen Takt und nicht den Dienst.
                Report(new ServiceActivity
                {
                    State = ServiceState.Faulted,
                    Message = FaultMessage,
                    LastError = Redaction.Scrub(ex.Message),
                    Cycles = Interlocked.Read(ref _cycles),
                    NextRunAt = Context.Clock.GetLocalNow() + Interval,
                });
            }

            try
            {
                await Task.Delay(Interval, Context.Clock, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Ein Takt samt Vorbedingungen.</summary>
    private async Task StepAsync(CancellationToken ct)
    {
        long cycles = Interlocked.Read(ref _cycles);

        if (!_enabled)
        {
            return;
        }

        if (Context.Composition is not { } composition)
        {
            // Kein Fehler, sondern der Normalfall auf einem frisch aufgesetzten Rechner. Der
            // Dienst laeuft weiter und nimmt seine Arbeit auf, sobald eine Konfiguration da ist.
            _primed = null;
            Report(new ServiceActivity
            {
                State = ServiceState.Paused,
                Message = NotConfiguredMessage,
                Cycles = cycles,
                NextRunAt = Context.Clock.GetLocalNow() + Interval,
            });

            return;
        }

        if (!ReferenceEquals(_primed, composition))
        {
            await PrimeAsync(composition, ct).ConfigureAwait(false);
            _primed = composition;
        }

        Report(new ServiceActivity
        {
            State = ServiceState.Working,
            Message = "Arbeitet.",
            Cycles = cycles,
        });

        string summary = await RunCycleAsync(composition, ct).ConfigureAwait(false);
        long done = Interlocked.Increment(ref _cycles);

        Report(new ServiceActivity
        {
            State = ServiceState.Idle,
            Message = summary,
            Cycles = done,
            NextRunAt = Context.Clock.GetLocalNow() + Interval,
        });
    }
}
