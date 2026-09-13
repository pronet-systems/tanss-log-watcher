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

    /// <summary>
    /// Die eigenen Tickets, jüngste zuerst.
    /// </summary>
    /// <remarks>
    /// <para>Damit die Oberfläche eine Auswahl anbieten kann statt eines Zahlenfelds. Bisher
    /// musste der Techniker die Ticketnummer auswendig wissen — die Fähigkeit, sie zu holen,
    /// lag ungenutzt im Werkzeug.</para>
    /// <para>Wie bei den Anbindungen ist ein Fehlschlag kein Fehler: Ohne Liste bleibt das Feld
    /// eine freie Eingabe, und eine von Hand getippte Nummer geht genauso durch.</para>
    /// </remarks>
    public IReadOnlyList<TicketRow> Tickets { get; private set; } = [];

    /// <summary>Wurde die Liste schon einmal erfolgreich geholt?</summary>
    public bool IsLoaded { get; private set; }

    /// <summary>
    /// Der Name des eigenen Technikers, oder <c>null</c>, solange er unbekannt ist.
    /// </summary>
    /// <remarks>
    /// <para>Steht hier und nicht in einem zweiten Zwischenspeicher: Er wird aus derselben
    /// Verbindung geholt und zur selben Zeit ungültig wie die Anbindungen. Zwei Speicher mit
    /// demselben Lebenslauf laufen auseinander, sobald jemand nur einen davon auffrischt.</para>
    /// <para>Gebraucht wird er vom Abschlussdialog: Dort steht der Techniker im Bericht, der
    /// zum Kunden geht — und eine Mitarbeiterkennung wie „1“ sagt dem Kunden nichts.</para>
    /// </remarks>
    public string? OwnTechnicianName { get; private set; }

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

            await LoadOwnTechnicianAsync(composition, ct).ConfigureAwait(true);
            await LoadTicketsAsync(composition, ct).ConfigureAwait(true);
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

    /// <summary>
    /// Löst die eigene Mitarbeiterkennung in einen Namen auf.
    /// </summary>
    /// <remarks>
    /// Misslingt es, bleibt <see cref="OwnTechnicianName"/> leer — und der Bericht lässt die
    /// Zeile dann weg. Eine Kennung statt eines Namens hinzuschreiben wäre schlechter als gar
    /// nichts: Sie steht in einem Text, den der Kunde liest, und bedeutet ihm nichts.
    /// </remarks>
    private async Task LoadOwnTechnicianAsync(RuntimeComposition composition, CancellationToken ct)
    {
        int own = composition.Config.Tanss.EmployeeId;

        if (own <= 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<Technician> technicians =
                await composition.Technicians.ListAsync(ct).ConfigureAwait(true);

            Technician? mine = technicians.FirstOrDefault(t => t.Id == own);

            if (mine is null)
            {
                return;
            }

            OwnTechnicianName = !string.IsNullOrWhiteSpace(mine.Name)
                ? mine.Name.Trim()
                : string.Join(' ',
                    new[] { mine.FirstName, mine.LastName }
                        .Where(part => !string.IsNullOrWhiteSpace(part))).Trim();

            if (OwnTechnicianName.Length == 0)
            {
                OwnTechnicianName = null;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Siehe Kommentar: ohne Namen bleibt die Zeile im Bericht weg.
        }
    }

    /// <summary>
    /// Holt die offenen Tickets des eigenen Mitarbeiters.
    /// </summary>
    /// <remarks>
    /// Misslingt es, bleibt die Liste leer — die Oberfläche zeigt dann eine leere Auswahl und
    /// sagt, dass nichts geladen werden konnte. Eine eingetippte Nummer wird trotzdem geprüft,
    /// und zwar einzeln über <c>FindAsync</c>: Die Prüfung hängt nicht an dieser Liste.
    /// </remarks>
    private async Task LoadTicketsAsync(RuntimeComposition composition, CancellationToken ct)
    {
        int own = composition.Config.Tanss.EmployeeId;

        if (own <= 0)
        {
            return;
        }

        try
        {
            IReadOnlyList<Ticket> tickets =
                await composition.Tickets.SearchOpenAsync(own, ct).ConfigureAwait(true);

            Tickets = [.. tickets.Select(ticket => new TicketRow(ticket))];
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Siehe Kommentar: die Auswahl bleibt leer, die Pruefung funktioniert weiter.
        }
    }
}
