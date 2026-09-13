namespace TanssLogWatcher.Api.Http;

/// <summary>
/// Löschen mit Rumpf.
/// </summary>
/// <remarks>
/// <para>TANSS erwartet auf <c>DELETE /api/v1/timers</c> die Kennung im <b>Rumpf</b>, nicht im
/// Pfad und nicht in der Abfragezeichenkette. Das ist ungewöhnlich genug, dass der allgemeine
/// Vertrag <c>ITanssClient.DeleteAsync</c> es nicht vorsieht — und er soll dafür auch nicht
/// aufgebohrt werden, weil das jede andere Umsetzung zu einem Feld zwingen würde, das nur eine
/// einzige Route braucht.</para>
/// <para>Deshalb diese Zusatzschnittstelle: <see cref="TanssClient"/> erfüllt sie, und ein
/// Aufrufer, der sie vorfindet, benutzt den genauen Weg.</para>
/// <para><b>Einen Rückfallweg gibt es nicht.</b> <c>DELETE /api/v1/timers</c> nimmt die Kennung
/// ausschließlich im Rumpf; eine Route, die sie als Abfrageparameter nähme, gibt es in 10.10.0
/// nicht — nachgemessen gegen eine Instanz dieser Version. Ein Aufruf mit
/// <c>?id=</c> würde stillschweigend nichts löschen oder mit 400 scheitern. Ein Zugang, der
/// diese Schnittstelle nicht erfüllt, kann also keine Timer löschen — das ist ein
/// Programmierfehler und wird als solcher gemeldet.</para>
/// </remarks>
public interface ITanssBodyDelete
{
    /// <summary>Sendet ein DELETE mit JSON-Rumpf.</summary>
    Task DeleteWithBodyAsync(string path, object? body,
                             IDictionary<string, string?>? query = null,
                             CancellationToken ct = default);
}
