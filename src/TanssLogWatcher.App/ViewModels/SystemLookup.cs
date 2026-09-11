using System.Runtime.Versioning;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.App.Runtime;

namespace TanssLogWatcher.App.ViewModels;

/// <summary>
/// Der Nachschlag „Fernwartungstyp 1003 heißt PuTTY und ist schwarz“.
/// </summary>
/// <remarks>
/// <para><b>Gemeinsam geführt, weil vier Seiten dieselbe Antwort brauchen.</b> Sitzungen,
/// Warteschlange, Überwachung und Verbindung zeigen alle den Typ — jede für sich abzufragen
/// hieße, beim Blättern durch die Navigation viermal dieselbe Liste zu holen.</para>
///
/// <para><b>Ein Fehlschlag ist kein Fehler.</b> Die Anbindungen sind Beiwerk: Ohne sie steht
/// „Typ 1003“ statt „PuTTY“, und der Farbfleck bleibt grau. Eine Seite, die deswegen eine
/// Fehlermeldung zeigte, verstellte den Blick auf das, worum es geht — die Sitzungen selbst
/// stehen auch ohne diese Liste da.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class SystemLookup
{
    private readonly Dictionary<int, SystemRow> _byId = [];

    /// <summary>Die Anbindungen, nach Namen sortiert; leer, solange nichts geladen wurde.</summary>
    public IReadOnlyList<SystemRow> Systems { get; private set; } = [];

    /// <summary>Wurde die Liste schon einmal erfolgreich geholt?</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>Der Name eines Fernwartungstyps, oder <c>null</c>, wenn er unbekannt ist.</summary>
    /// <param name="typeId">Die Kennung des Typs.</param>
    public string? NameFor(int typeId) =>
        _byId.TryGetValue(typeId, out SystemRow? row) ? row.Name : null;

    /// <summary>Die Farbe eines Fernwartungstyps als Hexwert, oder <c>null</c>.</summary>
    /// <param name="typeId">Die Kennung des Typs.</param>
    public string? ColorFor(int typeId) =>
        _byId.TryGetValue(typeId, out SystemRow? row) ? row.Color : null;

    /// <summary>
    /// Holt die Anbindungen, wenn ein Zusammenbau vorliegt.
    /// </summary>
    /// <remarks>
    /// Ein Fehlschlag wird geschluckt und mit <see cref="IsLoaded"/> gemeldet, nicht geworfen:
    /// Der Aufrufer ist eine Seite, die gerade aufgeht, und die soll dabei nicht scheitern.
    /// </remarks>
    /// <param name="composition">Der Zusammenbau, oder <c>null</c>, wenn nichts eingerichtet ist.</param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns><c>true</c>, wenn danach eine Liste vorliegt.</returns>
    public async Task<bool> RefreshAsync(RuntimeComposition? composition, CancellationToken ct = default)
    {
        if (composition is null)
        {
            return false;
        }

        try
        {
            IReadOnlyList<RemoteSupportSystem> systems =
                await composition.RemoteSupports.ListSystemsAsync(ct).ConfigureAwait(true);

            List<SystemRow> rows = [.. systems
                .Select(SystemRow.From)
                .OrderBy(row => row.Name, StringComparer.CurrentCulture)];

            _byId.Clear();
            foreach (SystemRow row in rows)
            {
                _byId[row.Id] = row;
            }

            Systems = rows;
            IsLoaded = true;
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Beiwerk. Die Seite zeigt dann Kennungen statt Namen - siehe Klassenkommentar.
            return false;
        }
    }
}
