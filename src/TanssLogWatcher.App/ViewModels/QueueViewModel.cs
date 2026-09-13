using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Storage.Queue;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Warteschlangenseite: was noch nicht bei TANSS ist und warum.
/// </summary>
/// <remarks>
/// <para><b>Ausstehende und aufgegebene Einträge stehen in einer Liste.</b> Getrennt
/// aufgeführt, müsste der Techniker zwei Stellen im Blick behalten, um dieselbe Frage zu
/// beantworten — „ist etwas liegengeblieben?“. Die Plakette am Ende der Zeile sagt, welcher
/// Art ein Eintrag ist.</para>
///
/// <para><b>Die Zahlen kommen aus SQL, die Zeilen aus den Nutzlasten.</b> Beides steht
/// nebeneinander, weil es auseinanderfallen kann: Eine Zeile, deren Nutzlast sich nicht mehr
/// lesen lässt, wird gezählt, aber nicht angezeigt. Ohne den Hinweis dazu wäre „ausstehend: 4“
/// neben drei Zeilen nicht zu erklären.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class QueueViewModel : RuntimeViewModel
{
    private readonly SystemLookup _systems;

    /// <summary>Baut die Seite und meldet sich am Sendedienst an.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="systems">Der gemeinsame Nachschlag für Typnamen.</param>
    public QueueViewModel(AppHost host, SystemLookup systems) : base(host)
    {
        ArgumentNullException.ThrowIfNull(systems);

        _systems = systems;
        host.Uploads.QueueChanged += OnQueueChanged;

        Apply(host.Uploads.Queue);
        _ = InitializeAsync();
    }

    /// <summary>Die ausstehenden und die aufgegebenen Einträge, jüngste zuerst.</summary>
    public ObservableCollection<QueueRow> Queue { get; } = [];

    /// <summary>Wartet auf den nächsten fälligen Versuch.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _pending;

    /// <summary>Aufgegeben; wird nicht mehr von selbst versucht.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _failed;

    /// <summary>Bei TANSS angekommen und als Beleg aufbewahrt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private int _done;

    /// <summary>Liess sich die Warteschlange überhaupt lesen?</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountsText))]
    private bool _isAvailable = true;

    /// <summary>Der Satz der letzten Handlung; leer, solange nichts geschah.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastRun))]
    private string _lastRun = string.Empty;

    /// <summary>Gibt es eine Rückmeldung zum letzten Sendelauf?</summary>
    public bool HasLastRun => !string.IsNullOrEmpty(LastRun);

    /// <summary>Der Hinweis zu unlesbaren Zeilen; <c>null</c>, wenn es keine gibt.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnreadableNotice))]
    private string? _unreadableNotice;

    /// <summary>Gibt es unlesbare Zeilen?</summary>
    public bool HasUnreadableNotice => !string.IsNullOrEmpty(UnreadableNotice);

    /// <summary>
    /// Gibt es einen Eintrag, dessen Ausgang offen ist?
    /// </summary>
    /// <remarks>
    /// Der wichtigste Hinweis dieser Seite. „Unbekannt“ heisst nicht „nicht vorhanden“: Vor
    /// der Wiederholung wird bei TANSS nachgefragt, statt die Dublette in Kauf zu nehmen, die
    /// TANSS nicht abfängt und die nur per direktem Datenbankzugriff zu entfernen wäre.
    /// </remarks>
    public bool HasUnknownOutcome => Queue.Any(row =>
        string.Equals(row.StateText, "Ausgang unbekannt", StringComparison.Ordinal));

    /// <summary>Ist gerade nichts zu tun?</summary>
    public bool IsEmpty => Queue.Count == 0;

    /// <summary>Sendet die fälligen Einträge sofort, statt auf den Takt zu warten.</summary>
    [RelayCommand]
    private async Task FlushAsync()
    {
        if (!IsConfigured)
        {
            LastRun = "Ohne Einrichtung geht nichts hinaus. Die Einrichtung steht unter „Verbindung“.";
            return;
        }

        UploadRunResult result = await Host.Uploads.FlushNowAsync().ConfigureAwait(true);
        LastRun = result.Summary;
        Apply(Host.Uploads.RefreshQueue());
    }

    /// <summary>Liest den Stand der Warteschlange neu.</summary>
    [RelayCommand]
    private void Refresh() => Apply(Host.Uploads.RefreshQueue());

    /// <inheritdoc />
    protected override void OnStatusUpdated(AppStatus status) => _ = InitializeAsync();

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Host.Uploads.QueueChanged -= OnQueueChanged;
        }

        base.Dispose(disposing);
    }

    private async Task InitializeAsync()
    {
        if (await _systems.RefreshAsync(Host.Composition).ConfigureAwait(true))
        {
            Apply(Host.Uploads.Queue);
        }
    }

    private void OnQueueChanged(object? sender, QueueSnapshot snapshot) => Apply(snapshot);

    private void Apply(QueueSnapshot snapshot)
    {
        DateTimeOffset now = Host.Clock.GetLocalNow();

        IsAvailable = snapshot.IsAvailable;
        Pending = snapshot.Pending;
        Failed = snapshot.Failed;
        Done = snapshot.Done;
        UnreadableNotice = snapshot.UnreadableNotice;

        Queue.Clear();

        // Erst die ausstehenden, dann die aufgegebenen: Was noch von selbst hinausgeht, steht
        // oben; was liegenbleibt und einen Handgriff braucht, darunter.
        foreach (QueuedUpload item in snapshot.PendingItems.Concat(snapshot.FailedItems))
        {
            Queue.Add(new QueueRow(item, _systems.NameFor(item.Payload.TypeId), now));
        }

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(HasUnknownOutcome));
    }

    /// <summary>Die Zahlen in einem Satz, für die Kopfzeile der Seite.</summary>
    public string CountsText => IsAvailable
        ? string.Create(CultureInfo.CurrentCulture,
            $"{Pending} ausstehend, {Failed} aufgegeben, {Done} gesendet.")
        : "Die Warteschlange lässt sich gerade nicht lesen.";
}
