using System.Collections.ObjectModel;
using System.Globalization;
using System.Runtime.Versioning;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Monitoring;
using TanssLogWatcher.Monitoring.Model;
using TanssLogWatcher.Storage.Config;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Die Überwachungsseite: welche Anwendung auf welchen Fernwartungstyp bucht.
/// </summary>
/// <remarks>
/// <para><b>Der gesamte Katalog steht da, nicht nur das Eingerichtete.</b> Die Frage, die
/// jemand auf dieser Seite hat, lautet fast immer „warum wird X nicht erfasst?“ — und die
/// Antwort ist meistens „X steht auf ,nicht überwachen‘“. Zeigte die Liste nur die aktiven
/// Regeln, fehlte genau die Zeile, die man sucht.</para>
///
/// <para><b>Gespeichert wird ausdrücklich auf Befehl.</b> Eine Zuordnung, die beim Umstellen
/// des Auswahlfelds sofort wirkte, bearbeitete die Konfiguration bei jedem versehentlichen
/// Scrollen über ein Auswahlfeld — und das Änderungsprotokoll wäre voll von Regeln, die
/// niemand ändern wollte.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed partial class MonitoringViewModel : RuntimeViewModel
{
    private readonly SystemLookup _systems;
    private readonly IConfigStore _store;

    /// <summary>Baut die Seite aus Konfiguration und Katalog.</summary>
    /// <param name="host">Die Laufzeit.</param>
    /// <param name="systems">Der gemeinsame Nachschlag für Typnamen und Farben.</param>
    /// <param name="store">Der Konfigurationsort; ohne Angabe der vorgesehene.</param>
    public MonitoringViewModel(AppHost host, SystemLookup systems, IConfigStore? store = null)
        : base(host)
    {
        ArgumentNullException.ThrowIfNull(systems);

        _systems = systems;
        _store = store ?? ConfigStore.Default();

        _ = InitializeAsync();
    }

    /// <summary>Der Profilkatalog samt Zuordnung.</summary>
    public ObservableCollection<MappingRow> Profiles { get; } = [];

    /// <summary>Die Anbindungen, vorangestellt „Nicht überwachen“.</summary>
    public ObservableCollection<SystemRow> Systems { get; } = [];

    /// <summary>Wie viele Profile tatsächlich beobachtet werden.</summary>
    public int ActiveProfiles => Profiles.Count(p => p.IsMapped);

    /// <summary>Wie viele Profile der Katalog überhaupt kennt.</summary>
    public int TotalProfiles => Profiles.Count;

    /// <summary>Der Satz über der Liste.</summary>
    public string CountsText => string.Create(CultureInfo.CurrentCulture,
        $"{ActiveProfiles} von {TotalProfiles} Anwendungen werden beobachtet.");

    /// <summary>Die Rückmeldung der letzten Handlung.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasMessage))]
    private string _message = string.Empty;

    /// <summary>Gibt es eine Rückmeldung?</summary>
    public bool HasMessage => !string.IsNullOrEmpty(Message);

    /// <summary>Sind Änderungen offen?</summary>
    [ObservableProperty]
    private bool _isDirty;

    /// <summary>
    /// Schreibt die Zuordnungen in die Konfiguration und lädt die Laufzeit neu.
    /// </summary>
    /// <remarks>
    /// Geschrieben werden nur die aktiven Regeln: Die Prüfung lässt in der Datei ausschliesslich
    /// Fernwartungstypen ab 1000 zu. „Nicht überwachen“ ist deshalb kein Eintrag mit besonderer
    /// Kennung, sondern das Fehlen des Eintrags.
    /// </remarks>
    [RelayCommand]
    private void Save()
    {
        if (Host.Config is not { } current)
        {
            Message = "Ohne Einrichtung gibt es nichts zu speichern. "
                + "Das Zahnrad unter „Verbindung“ öffnet den Assistenten.";
            return;
        }

        // Was diese Seite nicht anfasst, bleibt erhalten: eigene Titelmuster stehen in
        // derselben Regel und wuerden sonst beim ersten Umstellen eines Auswahlfelds
        // stillschweigend verschwinden.
        Dictionary<string, MonitoringEntry> previous = current.Monitoring
            .ToDictionary(e => e.Key, e => e, StringComparer.OrdinalIgnoreCase);

        List<MonitoringEntry> entries = [];

        foreach (MappingRow profile in Profiles.Where(p => p.IsMapped))
        {
            // Geprueft wird vor dem ersten Schreibzugriff und ueber alle Zeilen hinweg: Eine
            // halb geschriebene Datei mit einer gueltigen und einer fehlerhaften Regel waere
            // beim naechsten Laden komplett ungueltig - auch die Zeilen, an denen niemand war.
            if (!TrySplitExcludes(profile, out IReadOnlyList<string> excludes, out string? problem))
            {
                Message = problem;
                return;
            }

            entries.Add(previous.TryGetValue(profile.Key, out MonitoringEntry? old)
                ? old with
                {
                    RemoteSupportTypeId = profile.SelectedSystem!.Id,
                    ExcludeIpAddresses = excludes,
                }
                : new MonitoringEntry
                {
                    Key = profile.Key,
                    RemoteSupportTypeId = profile.SelectedSystem!.Id,
                    ExcludeIpAddresses = excludes,
                });
        }

        if (entries.Count == 0)
        {
            Message = "Mindestens eine Anwendung muss zugeordnet bleiben. Ohne eine einzige "
                + "Regel beobachtet das Werkzeug nichts, und die Konfiguration wäre ungültig.";
            return;
        }

        try
        {
            // Die uebrigen Abschnitte bleiben, wie sie sind: Diese Seite verwaltet die
            // Beobachtung und nicht die Verbindung.
            _store.Save(current with { Monitoring = entries });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Message = "Das Speichern ist fehlgeschlagen: " + Redaction.Scrub(ex.Message);
            return;
        }

        IsDirty = false;
        Message = Host.Reload()
            ? string.Create(CultureInfo.CurrentCulture,
                $"Gespeichert. {entries.Count} Anwendung(en) werden ab sofort beobachtet.")
            : "Gespeichert, aber das Neuladen ist fehlgeschlagen. Die Meldung steht unter „Verbindung“.";
    }

    /// <summary>
    /// Zerlegt die Eingabe einer Zeile in geprüfte Ausschlüsse.
    /// </summary>
    /// <remarks>
    /// Geprüft wird mit <see cref="IpRange.TrySplit"/> — derselben Prüfung, die auch
    /// <see cref="ConfigValidator"/> beim Laden über jeden Eintrag laufen lässt. Täte diese
    /// Seite es lockerer, liesse sie eine Datei entstehen, die beim nächsten Start als ungültig
    /// zurückkommt: gespeichert, aber unbrauchbar — und der Fehler stünde dann an einer Stelle,
    /// an der niemand mehr an dieses Feld denkt.
    /// </remarks>
    /// <param name="row">Die Zeile mit der Eingabe.</param>
    /// <param name="excludes">Die geprüften Einträge; leer, wenn etwas nicht stimmt.</param>
    /// <param name="problem">Was nicht stimmt, oder eine leere Zeichenkette.</param>
    /// <returns><c>true</c>, wenn jeder Eintrag lesbar war.</returns>
    private static bool TrySplitExcludes(MappingRow row, out IReadOnlyList<string> excludes,
                                         out string problem)
    {
        if (IpRange.TrySplit(row.ExcludedIps, IpFilter.ExcludeSeparator, out excludes,
                             out string? invalid))
        {
            problem = string.Empty;
            return true;
        }

        problem = $"„{invalid}“ bei {row.Application} ist weder Adresse noch Netz. "
            + "Erwartet wird eine einzelne Adresse (10.0.0.5) oder ein Netz in "
            + "CIDR-Schreibweise (10.0.0.0/8), mehrere davon durch Semikolon getrennt.";
        return false;
    }

    /// <inheritdoc />
    protected override void OnStatusUpdated(AppStatus status) => _ = InitializeAsync();

    private async Task InitializeAsync()
    {
        _ = await _systems.RefreshAsync(Host.Composition).ConfigureAwait(true);

        Systems.Clear();
        Systems.Add(SystemRow.None);
        foreach (SystemRow row in _systems.Systems)
        {
            Systems.Add(row);
        }

        Build();
    }

    private void Build()
    {
        Dictionary<string, MonitoringEntry> mapped = Host.Config?.Monitoring
            .ToDictionary(e => e.Key, e => e, StringComparer.OrdinalIgnoreCase)
            ?? [];

        Profiles.Clear();

        foreach (MonitoringSetting setting in MonitoringProfiles.Reconcile([]))
        {
            MonitoringProfile? profile = MonitoringProfiles.Find(setting.Key);
            SystemRow selected = SystemRow.None;
            string excluded = string.Empty;

            if (mapped.TryGetValue(setting.Key, out MonitoringEntry? entry))
            {
                selected = Systems.FirstOrDefault(s => s.Id == entry.RemoteSupportTypeId)
                    ?? SystemRow.None;
                // Dasselbe Trennzeichen wie in der Eingabe - was hier steht, muss sich
                // unveraendert zurueckschreiben lassen.
                excluded = string.Join(
                    $"{IpFilter.ExcludeSeparator} ", entry.ExcludeIpAddresses);
            }

            MappingRow row = new(setting.Key, profile?.TypeDescription ?? setting.Key, selected,
                                 ProfileRow.MethodText(profile), excluded);
            row.PropertyChanged += OnRowChanged;
            Profiles.Add(row);
        }

        IsDirty = false;
        Notify();
    }

    private void OnRowChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        IsDirty = true;
        Notify();
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(ActiveProfiles));
        OnPropertyChanged(nameof(TotalProfiles));
        OnPropertyChanged(nameof(CountsText));
    }
}
