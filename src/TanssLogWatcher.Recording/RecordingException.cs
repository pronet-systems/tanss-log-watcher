namespace TanssLogWatcher.Recording;

/// <summary>
/// Die Aufzeichnung ist gescheitert — und die Meldung sagt, woran.
/// </summary>
/// <remarks>
/// <para>Eine eigene Art, damit der Aufrufer sie von einem Programmfehler unterscheiden kann.
/// Das ist hier keine Förmlichkeit: Eine gescheiterte Aufzeichnung kostet eine Aufzeichnung,
/// ein Programmfehler kostet den Dienst — und der Dienst trägt die Sitzungen, aus denen die
/// Arbeitszeit wird.</para>
/// <para>Der Text gehört in die Oberfläche. Er nennt deshalb, was war, warum es so kam und was
/// zu tun ist, und nicht den Rückgabewert einer Windows-Funktion.</para>
/// </remarks>
public sealed class RecordingException : Exception
{
    /// <summary>Für die Serialisierung und für Aufrufer ohne eigene Meldung.</summary>
    public RecordingException()
        : base("Die Bildschirmaufzeichnung ist gescheitert.")
    {
    }

    /// <summary>Mit einer Meldung in deutscher Prosa.</summary>
    /// <param name="message">Was war, warum, und was zu tun ist.</param>
    public RecordingException(string message)
        : base(message)
    {
    }

    /// <summary>Mit einer Meldung und der zugrunde liegenden Ursache.</summary>
    /// <param name="message">Was war, warum, und was zu tun ist.</param>
    /// <param name="innerException">Was Windows gemeldet hat.</param>
    public RecordingException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
