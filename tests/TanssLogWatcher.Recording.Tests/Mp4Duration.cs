using System.Buffers.Binary;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Liest die Dauer aus einer mp4-Datei — aus der Datei, nicht aus unserer Buchführung.
/// </summary>
/// <remarks>
/// <para><b>Warum von Hand und nicht mit einer Bibliothek.</b> Die Behauptung, um die es geht,
/// lautet: „Eine Sitzung mit Pause ergibt eine Datei, die nur die aufgezeichnete Zeit lang
/// ist.“ Prüfte man das gegen unsere eigene <see cref="RecordingClock"/>, prüfte man die
/// Rechnung gegen sich selbst. Hier wird stattdessen gelesen, was ein Abspieler liest: der
/// <c>mvhd</c>-Block im <c>moov</c>-Container.</para>
/// <para>Eine mp4-Datei ist eine Folge von Blöcken: vier Byte Länge, vier Byte Kennung, Inhalt.
/// Container enthalten weitere Blöcke. Mehr braucht es dafür nicht.</para>
/// </remarks>
internal static class Mp4Duration
{
    /// <summary>
    /// Die Dauer der Datei, wie ein Abspieler sie ermittelt.
    /// </summary>
    /// <param name="path">Die Datei.</param>
    /// <returns>Die Dauer, oder <c>null</c>, wenn die Datei keinen lesbaren Kopf hat.</returns>
    public static TimeSpan? Of(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);

        if (FindBox(bytes, 0, bytes.Length, "moov") is not { } moov)
        {
            return null;
        }

        if (FindBox(bytes, moov.Content, moov.End, "mvhd") is not { } mvhd)
        {
            return null;
        }

        int at = mvhd.Content;

        if (at + 4 > bytes.Length)
        {
            return null;
        }

        byte version = bytes[at];

        // Fassung 0: Zeitmass und Dauer sind 32 Bit, Fassung 1: 64 Bit. Dazwischen liegen
        // Erzeugungs- und Aenderungszeitpunkt in derselben Breite.
        int offset = at + 4;

        if (version == 1)
        {
            offset += 16;

            if (offset + 12 > bytes.Length)
            {
                return null;
            }

            uint scale64 = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
            ulong duration64 = BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(offset + 4));

            return scale64 == 0
                ? null
                : TimeSpan.FromSeconds((double)duration64 / scale64);
        }

        offset += 8;

        if (offset + 8 > bytes.Length)
        {
            return null;
        }

        uint scale = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset));
        uint duration = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 4));

        return scale == 0 ? null : TimeSpan.FromSeconds((double)duration / scale);
    }

    /// <summary>
    /// Die Blöcke der obersten Ebene, in der Reihenfolge, in der sie in der Datei stehen.
    /// </summary>
    /// <remarks>
    /// Für die Frage, die sich nur an der Reihenfolge entscheidet: Steht der Index
    /// (<c>moov</c>) VOR den Daten? Genau davon hängt ab, ob eine abgebrochene Aufzeichnung
    /// noch etwas taugt.
    /// </remarks>
    /// <param name="path">Die Datei.</param>
    public static IReadOnlyList<string> BoxesOf(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        List<string> found = [];
        int at = 0;

        while (at + 8 <= bytes.Length)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            string kind = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);

            long length = size switch
            {
                0 => bytes.Length - at,
                1 => at + 16 <= bytes.Length
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8))
                    : 0,
                _ => size,
            };

            if (length < 8)
            {
                break;
            }

            found.Add(kind);
            at += (int)length;
        }

        return found;
    }

    private static Box? FindBox(byte[] bytes, int from, int until, string type)
    {
        int at = from;

        while (at + 8 <= until)
        {
            uint size = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(at));
            string kind = System.Text.Encoding.ASCII.GetString(bytes, at + 4, 4);

            // Groesse 0 heisst "bis zum Ende der Datei", 1 heisst "die echte Groesse steht in
            // den naechsten acht Byte". Beides kommt bei Media Foundation nicht vor, wird aber
            // abgefangen, damit der Leser nicht in eine Schleife laeuft.
            long length = size switch
            {
                0 => until - at,
                1 => at + 16 <= until
                    ? (long)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(at + 8))
                    : 0,
                _ => size,
            };

            if (length < 8)
            {
                return null;
            }

            if (string.Equals(kind, type, StringComparison.Ordinal))
            {
                int content = size == 1 ? at + 16 : at + 8;
                return new Box(content, (int)Math.Min(at + length, until));
            }

            at += (int)length;
        }

        return null;
    }

    private readonly record struct Box(int Content, int End);
}
