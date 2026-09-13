using System.Runtime.Versioning;
using Windows.Win32;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Media Foundation hoch- und wieder herunterfahren, mit Zählwerk.
/// </summary>
/// <remarks>
/// <para><b>Warum gezählt wird.</b> <c>MFStartup</c> und <c>MFShutdown</c> gehören paarweise,
/// und mehrere Aufzeichnungen laufen gleichzeitig — eine je Sitzung, und bei einem
/// Abschnittswechsel überlappen sich für einen Augenblick zwei Dateien. Wer beim Schliessen der
/// einen bedingungslos herunterfährt, zieht der anderen den Boden weg; der Fehler äussert sich
/// als <c>MF_E_SHUTDOWN</c> mitten im Schreiben, also genau dort, wo eine Aufzeichnung
/// verlorengeht.</para>
/// <para><b>Nicht heruntergefahren wird nie.</b> Auch der letzte Aufruf zählt herunter und
/// beendet die Bibliothek — ein Werkzeug, das Media Foundation für die Lebensdauer des
/// Prozesses offenhält, hält damit auch die Hardware-Kodierer belegt.</para>
/// </remarks>
[SupportedOSPlatform("windows6.1")]
internal static class MediaFoundation
{
    private static readonly Lock Gate = new();

    private static int _users;

    /// <summary>Fährt Media Foundation hoch, wenn es noch niemand getan hat.</summary>
    public static void Start()
    {
        lock (Gate)
        {
            if (_users == 0)
            {
                // MFSTARTUP_NOSOCKET: Die Netzwerkquellen von Media Foundation brauchen wir
                // nicht, und ein Werkzeug, das Bildschirme aufzeichnet, soll nicht nebenbei
                // Netzdienste hochfahren.
                PInvoke.MFStartup(PInvoke.MF_VERSION, 1);
            }

            _users++;
        }
    }

    /// <summary>Fährt Media Foundation herunter, wenn niemand mehr schreibt.</summary>
    public static void Stop()
    {
        lock (Gate)
        {
            if (_users == 0)
            {
                return;
            }

            _users--;

            if (_users == 0)
            {
                PInvoke.MFShutdown();
            }
        }
    }
}
