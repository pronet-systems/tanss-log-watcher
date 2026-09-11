namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Was ein Hintergrunddienst vom Rest der Anwendung braucht — und mehr nicht.
/// </summary>
/// <remarks>
/// <para>Die Dienste bekommen ausdrücklich <b>nicht</b> den <see cref="AppHost"/> selbst. Sie
/// sollen ihn nicht anhalten, nicht neu laden und nicht die Konfiguration austauschen können;
/// sie sollen ihre Arbeit tun und melden, wie sie ausgegangen ist. Diese Schnittstelle ist
/// genau diese Grenze.</para>
///
/// <para><see cref="Composition"/> ist mit gutem Grund veränderlich und darf <c>null</c> sein:
/// Solange keine gültige Konfiguration vorliegt, gibt es keine Bausteine, und nach einem
/// erneuten Laden sind es andere. Ein Dienst, der sich den Zusammenbau einmal merkte, arbeitete
/// nach dem Berichtigen der <c>config.json</c> weiter gegen die alte Instanz.</para>
/// </remarks>
public interface IRuntimeContext
{
    /// <summary>Die Bausteine, oder <c>null</c>, solange nichts eingerichtet ist.</summary>
    RuntimeComposition? Composition { get; }

    /// <summary>Der aktuelle Betriebszustand.</summary>
    AppStatus Status { get; }

    /// <summary>Die Umlenkung auf den Strang der Oberfläche.</summary>
    RuntimeNotifier Notifier { get; }

    /// <summary>Die Uhr; für Tests einsetzbar.</summary>
    TimeProvider Clock { get; }

    /// <summary>
    /// Meldet eine Störung — mit Grund und Vorschlag, niemals ohne.
    /// </summary>
    /// <remarks>
    /// Kein Dienst setzt den Zustand selbst; er meldet, was ihm widerfahren ist, und der Host
    /// entscheidet. Sonst überschrieben sich drei Dienste gegenseitig, und die Anzeige zeigte
    /// den Befund dessen, der zufällig zuletzt fertig wurde.
    /// </remarks>
    /// <param name="cause">Die Art der Störung.</param>
    /// <param name="reason">Was kaputt ist und warum das üblicherweise passiert.</param>
    /// <param name="advice">Was zu tun ist.</param>
    void ReportDegraded(DegradedCause cause, string reason, string advice);

    /// <summary>
    /// Meldet einen geglückten Vorgang gegen TANSS.
    /// </summary>
    /// <remarks>
    /// Das ist der Weg aus der Störung heraus, und zwar der einzige belastbare: Ein geglückter
    /// echter Aufruf beweist, dass Netz, Adresse, Token und Recht zusammen wieder tragen. Eine
    /// Störung, die nur nach Ablauf einer Frist verschwände, verschwände auch dann, wenn nichts
    /// behoben wurde.
    /// </remarks>
    void ReportWorking();
}
