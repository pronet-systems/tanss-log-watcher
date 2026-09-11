namespace TanssLogWatcher.Cli;

/// <summary>
/// Die Rückgabewerte des Werkzeugs.
/// </summary>
/// <remarks>
/// <para>Sie sind ein Vertrag, kein Anzeigedetail: Eine Überwachung ruft
/// <c>tanss-logwatch doctor</c> auf und entscheidet allein anhand der Zahl, ob sie jemanden
/// weckt. Wer hier etwas umwidmet, ändert stillschweigend das Verhalten jeder Überwachung,
/// die schon läuft.</para>
/// <para><see cref="Usage"/> steht bewusst abseits der Skala 0/1/2: Ein Tippfehler im Aufruf
/// ist keine Aussage über die Gesundheit der Einrichtung. Der Wert 64 ist der übliche
/// <c>EX_USAGE</c>.</para>
/// </remarks>
public static class ExitCode
{
    /// <summary>Alles in Ordnung.</summary>
    public const int Healthy = 0;

    /// <summary>Läuft, aber etwas verlangt Aufmerksamkeit.</summary>
    public const int Warning = 1;

    /// <summary>Gestört: In diesem Zustand geht Arbeit verloren oder kommt nicht an.</summary>
    public const int Broken = 2;

    /// <summary>Der Aufruf selbst war fehlerhaft — unbekannter Befehl, fehlendes Argument.</summary>
    public const int Usage = 64;

    /// <summary>Der strengere der beiden Befunde. Ein Durchlauf ist so gesund wie seine schlechteste Stufe.</summary>
    public static int Worse(int left, int right) => Math.Max(left, right);
}
