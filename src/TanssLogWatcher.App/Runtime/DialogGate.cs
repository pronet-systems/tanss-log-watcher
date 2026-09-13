namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Reiht die Abschlussdialoge auf: einer nach dem anderen, und niemals keiner mehr.
/// </summary>
/// <remarks>
/// <para><b>Einer nach dem anderen.</b> Enden zwei Sitzungen im selben Takt — beim Abmelden
/// durchaus der Normalfall —, stapelten sich sonst zwei Fenster übereinander, von denen der
/// Techniker nur das oberste sieht und das darunter blind wegklickt.</para>
///
/// <para><b>Der Riegel wird erst gesetzt, wenn das Fenster steht — darum gibt es dieses
/// Stück.</b> Vorher stand er in der ersten Zeile der Anzeigemethode, und wäre der Bau des
/// Fensters je gescheitert, bliebe er für die restliche Laufzeit stehen: Es ginge nie wieder
/// ein Abschlussdialog auf, und jede weitere Sitzung verlöre still ihren Bericht. Ob das je
/// geschehen ist, weiss niemand — der Weg dorthin läuft über <see cref="RuntimeNotifier"/>,
/// und der schluckt jede Ausnahme des Empfängers ohne Protokollzeile. <b>Die Ursache ist
/// unbelegt geblieben</b>; der Riegel gehört trotzdem herum, und er gehört an eine Stelle, die
/// sich ohne Fenster prüfen lässt.</para>
///
/// <para><b>Ein Fehlschlag hält die Schlange nicht an.</b> Er wird gemeldet, und der nächste
/// wartende Dialog kommt trotzdem dran. Die Sitzung ist dabei nicht verloren: Ihre Zeile
/// wartet in der Warteschlange, und beim nächsten Start wird sie erneut vorgelegt.</para>
///
/// <para><b>Nur vom Strang der Oberfläche aus zu benutzen.</b> Hier steht bewusst keine
/// Sperre: Die Aufrufe kommen über <see cref="RuntimeNotifier"/> und damit ohnehin auf einem
/// Strang, und eine Sperre täuschte eine Sicherheit vor, die das Fenster darunter — WPF —
/// nicht hat.</para>
/// </remarks>
public sealed class DialogGate
{
    private readonly Queue<SessionClosed> _waiting = new();
    private readonly Action<SessionClosed, Action> _show;
    private readonly Action<SessionClosed, Exception>? _failed;
    private readonly Action<SessionClosed>? _shown;

    private bool _open;

    /// <summary>Baut die Schlange.</summary>
    /// <param name="show">
    /// Zeigt den Dialog. Die übergebene Handlung ist beim Schliessen des Fensters aufzurufen —
    /// erst dann kommt der nächste dran. Wirft diese Handlung, gilt der Dialog als nicht
    /// gezeigt: Der Riegel bleibt offen, und der nächste Eintrag wird versucht.
    /// </param>
    /// <param name="failed">Was zu tun ist, wenn ein Dialog nicht aufging.</param>
    /// <param name="shown">Was zu tun ist, wenn einer aufgegangen ist.</param>
    public DialogGate(Action<SessionClosed, Action> show,
                      Action<SessionClosed, Exception>? failed = null,
                      Action<SessionClosed>? shown = null)
    {
        ArgumentNullException.ThrowIfNull(show);
        _show = show;
        _failed = failed;
        _shown = shown;
    }

    /// <summary>Steht gerade ein Dialog offen?</summary>
    public bool IsOpen => _open;

    /// <summary>Wie viele warten noch?</summary>
    public int Waiting => _waiting.Count;

    /// <summary>Reiht eine abgeschlossene Sitzung ein und zeigt, was dran ist.</summary>
    /// <param name="closed">Die abgeschlossene Sitzung.</param>
    public void Enqueue(SessionClosed closed)
    {
        ArgumentNullException.ThrowIfNull(closed);

        _waiting.Enqueue(closed);
        Pump();
    }

    /// <summary>Zeigt den nächsten wartenden Dialog, wenn gerade keiner offen ist.</summary>
    private void Pump()
    {
        while (!_open && _waiting.TryDequeue(out SessionClosed? next))
        {
            try
            {
                _show(next, Release);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Hausregel 5: Dieser eine Dialog geht schief, nicht der Vorgang - und
                // ausdruecklich auch nicht die uebrigen Dialoge. Der Riegel steht zu diesem
                // Zeitpunkt noch offen und kann deshalb gar nicht haengenbleiben.
                _failed?.Invoke(next, ex);
                continue;
            }

            // Erst jetzt. Ab hier gibt es ein Fenster, das den Riegel beim Schliessen wieder
            // aufhebt - und nur ab hier darf er stehen.
            _open = true;
            _shown?.Invoke(next);
        }
    }

    /// <summary>Der Dialog ist zu; der nächste darf.</summary>
    private void Release()
    {
        _open = false;
        Pump();
    }
}
