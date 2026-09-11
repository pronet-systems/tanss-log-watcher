namespace TanssLogWatcher.App.Runtime;

/// <summary>
/// Die drei Betriebszustände des Werkzeugs.
/// </summary>
/// <remarks>
/// <para>Es sind genau drei, und sie sind nach der <b>Folge für den Techniker</b> geschnitten,
/// nicht nach der technischen Ursache: Muss ich etwas einrichten, geht mir gerade etwas
/// verloren, oder läuft es? Eine feinere Einteilung — „Token abgelaufen“ neben „Netz weg“ —
/// gehört in die Begründung, nicht in den Zustand: Beide bedeuten dasselbe, nämlich dass die
/// Warteschlange sich füllt und niemand sie leert.</para>
///
/// <para><b>Es fehlt bewusst ein vierter Zustand „Fehler“.</b> Eine unbehandelte Ausnahme darf
/// dieses Werkzeug nicht beenden und auch nicht in einen Zustand ohne Ausweg führen; alles,
/// was schiefgehen kann, ist entweder ein Einrichtungsmangel oder eine Störung — beides mit
/// Grund und Vorschlag.</para>
/// </remarks>
public enum RuntimeState
{
    /// <summary>
    /// Keine oder keine gültige <c>config.json</c>. Heute der Normalfall, weil es den
    /// Einrichtungsassistenten noch nicht gibt.
    /// </summary>
    NotConfigured,

    /// <summary>
    /// Eingerichtet, aber TANSS nimmt gerade nichts an. Die Beobachtung läuft weiter, die
    /// Warteschlange füllt sich; nur das Senden ruht.
    /// </summary>
    Degraded,

    /// <summary>Alles in Ordnung.</summary>
    Working,
}

/// <summary>
/// Woran die Störung liegt — die Unterscheidung, die über den nächsten Handgriff entscheidet.
/// </summary>
/// <remarks>
/// Die wichtigste Grenze verläuft zwischen <see cref="Unreachable"/> und
/// <see cref="TokenRejected"/>: Das eine ist ein Netz- oder Adressproblem, das andere ein
/// Rechte- oder Tokenproblem. Wer beides zu „Verbindung fehlgeschlagen“ zusammenzieht, schickt
/// den Techniker in die falsche Richtung.
/// </remarks>
public enum DegradedCause
{
    /// <summary>Keine Störung.</summary>
    None,

    /// <summary>TANSS hat nicht geantwortet: Netz, VPN, Adresse oder Zertifikat.</summary>
    Unreachable,

    /// <summary>TANSS hat geantwortet und abgewiesen: Token abgelaufen, falscher Mandant, Recht fehlt.</summary>
    TokenRejected,

    /// <summary>Im lokalen Profil liegt kein lesbares Token.</summary>
    TokenUnreadable,

    /// <summary>Das Modul Fernwartung ist auf dieser Instanz nicht lizenziert.</summary>
    ModuleNotLicensed,

    /// <summary>
    /// <c>state.db</c> lässt sich nicht öffnen — der schwerste Fall, weil dann nicht einmal
    /// zwischengelagert werden kann.
    /// </summary>
    StateDatabase,

    /// <summary>Etwas anderes ist schiefgegangen; die Begründung nennt die Meldung.</summary>
    Unknown,
}

/// <summary>
/// Eine Sache, die läuft, aber nicht in Ordnung ist.
/// </summary>
/// <remarks>
/// Getrennt vom Zustand, weil eine Warnung den Betrieb nicht anhält. Der einzige Grund, sie
/// überhaupt zu führen, ist Sichtbarkeit: <c>tanss.verify_tls = false</c> stand bisher nur als
/// Kommentar im Quelltext, und ein abgeschalteter Zertifikatscheck, von dem niemand weiß, ist
/// derselbe Fehler wie gar keiner.
/// </remarks>
/// <param name="Title">Die Überschrift, etwa „Zertifikatsprüfung abgeschaltet“.</param>
/// <param name="Reason">Was das bedeutet und warum es üblicherweise so kommt.</param>
/// <param name="Advice">Was zu tun ist.</param>
public sealed record RuntimeWarning(string Title, string Reason, string Advice);

/// <summary>
/// Der Betriebszustand, wie ihn die Oberfläche anzeigt: Zustand, Klartextgrund, Vorschlag.
/// </summary>
/// <remarks>
/// <para><b>Grund und Vorschlag sind Pflicht, nicht Beiwerk</b> (Hausregel 3). Ein Zustand ohne
/// beides ist für den Techniker wertlos — „Gestört“ allein sagt ihm nur, dass er nicht weiß,
/// was los ist. Die Sätze entstehen deshalb dort, wo die Ursache bekannt ist, und werden nicht
/// in der Oberfläche zusammengeraten.</para>
///
/// <para>Unveränderlich und als Ganzes ausgetauscht: Eine Ansicht, die zwischen zwei
/// Eigenschaften eines halb aktualisierten Zustands liest, zeigte sonst „Arbeitend“ neben der
/// Begründung der letzten Störung.</para>
/// </remarks>
public sealed record AppStatus
{
    /// <summary>Der Betriebszustand.</summary>
    public required RuntimeState State { get; init; }

    /// <summary>Die kurze Beschriftung für Plakette und Infobereich, etwa „Nicht eingerichtet“.</summary>
    public required string Headline { get; init; }

    /// <summary>Was los ist und warum das üblicherweise passiert — ein bis drei Sätze.</summary>
    public required string Reason { get; init; }

    /// <summary>Was zu tun ist. Niemals leer.</summary>
    public required string Advice { get; init; }

    /// <summary>Die Ursache der Störung; bei den übrigen Zuständen <see cref="DegradedCause.None"/>.</summary>
    public DegradedCause Cause { get; init; } = DegradedCause.None;

    /// <summary>Der Ort, an dem die Konfiguration erwartet wird. Für den Zustand „Nicht eingerichtet“.</summary>
    public string ConfigPath { get; init; } = string.Empty;

    /// <summary>
    /// Die Einzelverstöße der Prüfung, je Eintrag <c>pfad: was stimmt nicht</c>.
    /// </summary>
    /// <remarks>
    /// Kommt aus <see cref="Storage.ConfigValidationException.Problems"/> und steht hier
    /// einzeln, damit eine Ansicht sie untereinander setzen kann. <see cref="Reason"/> enthält
    /// dieselben Angaben bereits als Fließtext.
    /// </remarks>
    public IReadOnlyList<string> Problems { get; init; } = [];

    /// <summary>Was läuft, aber nicht in Ordnung ist.</summary>
    public IReadOnlyList<RuntimeWarning> Warnings { get; init; } = [];

    /// <summary>Seit wann dieser Zustand gilt.</summary>
    public DateTimeOffset Since { get; init; } = DateTimeOffset.Now;

    /// <summary>Gibt es Warnungen? Für die Ansicht, die daraufhin gelb statt grün zeigt.</summary>
    public bool HasWarnings => Warnings.Count > 0;

    /// <summary>Ist das Werkzeug eingerichtet — unabhängig davon, ob TANSS gerade antwortet?</summary>
    public bool IsConfigured => State != RuntimeState.NotConfigured;

    /// <summary>Läuft die Beobachtung? In beiden eingerichteten Zuständen ja.</summary>
    /// <remarks>
    /// Genau der Punkt, den die Warteschlange rechtfertigt: Eine Störung hält das Senden an,
    /// nicht das Beobachten. Wer bei fehlender Verbindung auch die Beobachtung abschaltete,
    /// verlöre die Sitzungen, die er gerade aufbewahren wollte.
    /// </remarks>
    public bool IsWatching => IsConfigured;
}
