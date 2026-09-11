namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Bringt Meldungen der Hintergrunddienste auf den Strang der Oberfläche.
/// </summary>
/// <remarks>
/// <para><b>Warum das hier steht und nicht in jedem Ansichtsmodell.</b> Die drei
/// Hintergrunddienste laufen auf Strängen des Vorrats. Ein Ereignis, das von dort unmittelbar
/// eine <c>ObservableCollection</c> anfasst, führt zu einer <c>NotSupportedException</c> aus
/// dem Bindungssystem — sichtbar aber erst dann, wenn der Techniker gerade auf die Seite
/// schaut. Die Umlenkung gehört deshalb an die Quelle und nicht an jeden der Dutzend
/// Anmeldepunkte, die es sonst zu bedenken gälte.</para>
///
/// <para><b>Ein Fehler im Empfänger darf den Dienst nicht anhalten</b> (Hausregel 5). Wirft
/// ein Ansichtsmodell beim Verarbeiten eines Ereignisses, ist das ein Fehler der Anzeige —
/// die Beobachtung läuft trotzdem weiter, und die Sitzung geht nicht verloren.</para>
/// </remarks>
public sealed class RuntimeNotifier
{
    private readonly SynchronizationContext? _context;

    /// <summary>
    /// Merkt sich den Strang, auf dem dieses Stück entstanden ist.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich im Konstruktor und nicht bei jedem Ereignis: Zur Zeit des Ereignisses ist
    /// <see cref="SynchronizationContext.Current"/> der Strang des Vorrats, also gerade nicht
    /// der gesuchte. Erzeugt werden muss dieses Stück folglich auf dem Strang der Oberfläche.
    /// </remarks>
    /// <param name="context">
    /// Der Zielstrang; ohne Angabe der gerade laufende. <c>null</c> bedeutet „unmittelbar
    /// ausführen“ — so laufen Tests ohne Fenster.
    /// </param>
    public RuntimeNotifier(SynchronizationContext? context = null) =>
        _context = context ?? SynchronizationContext.Current;

    /// <summary>Führt etwas auf dem Strang der Oberfläche aus, ohne zu warten.</summary>
    /// <param name="action">Was zu tun ist; üblicherweise das Auslösen eines Ereignisses.</param>
    public void Post(Action action)
    {
        ArgumentNullException.ThrowIfNull(action);

        if (_context is null || _context == SynchronizationContext.Current)
        {
            Guarded(action);
            return;
        }

        _context.Post(static state => Guarded((Action)state!), action);
    }

    /// <summary>
    /// Löst ein Ereignis auf dem Strang der Oberfläche aus.
    /// </summary>
    /// <typeparam name="T">Die Art der Ereignisdaten.</typeparam>
    /// <param name="handler">Die angemeldeten Empfänger; <c>null</c>, wenn keiner da ist.</param>
    /// <param name="sender">Der Absender.</param>
    /// <param name="args">Die Ereignisdaten.</param>
    public void Raise<T>(EventHandler<T>? handler, object sender, T args)
    {
        if (handler is null)
        {
            return;
        }

        Post(() => handler(sender, args));
    }

    /// <summary>
    /// Führt aus und schluckt, was der Empfänger wirft.
    /// </summary>
    /// <remarks>
    /// Geschluckt wird hier tatsächlich: Es gibt keinen sinnvollen Ort, an den dieser Fehler
    /// gemeldet werden könnte — der Dienst, der das Ereignis auslöste, ist längst weiter, und
    /// eine Fehlermeldung über einen missglückten Anzeigevorgang wäre für den Techniker ohne
    /// jeden Handlungswert. Die Alternative wäre ein Absturz der ganzen Anwendung.
    /// </remarks>
    private static void Guarded(Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            System.Diagnostics.Debug.WriteLine(
                "Ein Empfänger eines Laufzeitereignisses hat geworfen: " + ex.Message);
        }
    }
}
