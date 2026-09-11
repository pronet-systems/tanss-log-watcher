using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die gemeinsame Grundlage aller Seitenmodelle: Zugang zur Laufzeit und der Betriebszustand.
/// </summary>
/// <remarks>
/// <para><b>Warum jede Seite den Zustand kennt.</b> Eine Seite, die ihre Liste zeigt, ohne zu
/// wissen, ob überhaupt etwas eingerichtet ist, zeigt im Normalfall des ersten Starts eine
/// leere Tabelle — und eine leere Tabelle sieht aus wie „nichts los“ und nicht wie „hier fehlt
/// die Einrichtung“. Genau diese Verwechslung hat die Vorlage dem Techniker zugemutet.</para>
///
/// <para><b>Die Anmeldung wird wieder abgemeldet.</b> Die Seiten entstehen bei jedem Wechsel
/// der Navigation neu. Ohne <see cref="Dispose()"/> hielte der Host nach zwanzig Wechseln zwanzig
/// tote Seitenmodelle am Ereignis fest, und jedes einzelne liefe bei jeder Zustandsänderung
/// mit.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public abstract partial class RuntimeViewModel : ObservableObject, IDisposable
{
    private bool _disposed;

    /// <summary>Meldet sich am Betriebszustand an und übernimmt den aktuellen Stand.</summary>
    /// <param name="host">Die Laufzeit der Anwendung.</param>
    protected RuntimeViewModel(AppHost host)
    {
        ArgumentNullException.ThrowIfNull(host);

        Host = host;
        _status = host.Status;
        host.StatusChanged += OnHostStatusChanged;
    }

    /// <summary>Die Laufzeit: Konfiguration, Dienste, Betriebszustand.</summary>
    protected AppHost Host { get; }

    /// <summary>Der Betriebszustand, wie ihn die Fußzeile und die Hinweisleisten zeigen.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsConfigured))]
    [NotifyPropertyChangedFor(nameof(NeedsSetup))]
    [NotifyPropertyChangedFor(nameof(IsDegraded))]
    private AppStatus _status;

    /// <summary>Ist das Werkzeug eingerichtet — unabhängig davon, ob TANSS gerade antwortet?</summary>
    public bool IsConfigured => Status.IsConfigured;

    /// <summary>
    /// Fehlt die Einrichtung? Die Seiten blenden daraufhin ihre Liste aus und bieten den
    /// Assistenten an, statt eine leere Tabelle zu zeigen.
    /// </summary>
    public bool NeedsSetup => !Status.IsConfigured;

    /// <summary>Läuft es, aber TANSS nimmt gerade nichts an?</summary>
    public bool IsDegraded => Status.State == RuntimeState.Degraded;

    /// <summary>Meldet sich vom Betriebszustand ab.</summary>
    public void Dispose()
    {
        Dispose(disposing: true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Wird auf dem Strang der Oberfläche gerufen, wenn sich der Betriebszustand ändert.
    /// </summary>
    /// <remarks>
    /// Die Umlenkung besorgt <see cref="RuntimeNotifier"/> an der Quelle; hier ist nichts mehr
    /// zu marshallen. Seiten überschreiben das, um daraufhin ihre Liste neu zu holen.
    /// </remarks>
    /// <param name="status">Der neue Zustand.</param>
    /// <remarks>
    /// Heisst bewusst nicht <c>OnStatusChanged</c>: Diesen Namen belegt bereits der Haken, den
    /// der Generator zu <see cref="Status"/> erzeugt, und ein <c>partial void</c> lässt sich
    /// nicht überschreiben.
    /// </remarks>
    protected virtual void OnStatusUpdated(AppStatus status)
    {
    }

    /// <summary>Gibt eigene Anmeldungen frei; abgeleitete Seiten erweitern das.</summary>
    /// <param name="disposing">Ob verwaltete Teile freizugeben sind.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (disposing)
        {
            Host.StatusChanged -= OnHostStatusChanged;
        }
    }

    private void OnHostStatusChanged(object? sender, AppStatus status)
    {
        Status = status;
        OnStatusUpdated(status);
    }
}
