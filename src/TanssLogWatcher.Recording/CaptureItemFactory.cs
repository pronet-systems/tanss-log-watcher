using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Aus einem Fensterhandle wird ein Aufnahmeziel.
/// </summary>
/// <remarks>
/// <para><b>Der einzige Ort mit COM-Handarbeit in diesem Projekt.</b> Die Windows-Runtime kennt
/// keinen Weg von einem Win32-Fensterhandle zu einem Aufnahmeziel; dafür gibt es eine eigene
/// COM-Schnittstelle an der Aktivierungsfabrik. Sie ist seit Windows 10 1803 stabil.</para>
///
/// <para><b>Warum über die Funktionstabelle und nicht über <c>[ComImport]</c>.</b> Gemessen: Der
/// bequeme Weg — Fabrikzeiger holen, mit <c>Marshal.GetObjectForIUnknown</c> einen
/// Stellvertreter bauen und auf die Schnittstelle umwandeln — scheitert zur Laufzeit mit
/// <c>InvalidCastException</c>. Die eingebaute COM-Vermittlung von .NET erzeugt für einen
/// WinRT-Objektverweis keinen Stellvertreter, der eine klassische COM-Schnittstelle bedienen
/// könnte. Der Weg über die Funktionstabelle ist dafür umständlich, aber er ist der Weg, den
/// die Schnittstelle vorsieht: <c>QueryInterface</c>, dann der vierte Eintrag.</para>
///
/// <para><b>Warum nicht <c>TryCreateFromWindowId</c>.</b> Das gibt es erst ab Windows 11, und
/// die README sagt Windows 10 zu. Ein zweiter Weg nur für Windows 10 wäre ein Weg, den auf den
/// Rechnern der Techniker niemand je durchliefe — und der deshalb auch nicht auffiele, wenn er
/// kaputt ist.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal static class CaptureItemFactory
{
    // IGraphicsCaptureItemInterop. Die Kennung steht seit Windows 10 1803 fest.
    private static readonly Guid InteropId = new("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356");

    /// <summary>
    /// Die Kennung der Schnittstelle IGraphicsCaptureItem.
    /// </summary>
    /// <remarks>
    /// Ausdrücklich hier und nicht <c>typeof(GraphicsCaptureItem).GUID</c>: Das liefert die
    /// Kennung der Laufzeitklasse, nicht die der Schnittstelle. Gemessen — mit der Klassenkennung
    /// antwortet die Fabrik mit <c>E_NOINTERFACE</c> (0x80004002), und die Fehlermeldung führt
    /// dann in die Irre, weil sie nach einem gesperrten Fenster klingt.
    /// </remarks>
    private static readonly Guid CaptureItemId = new("79C3F95B-31F7-4EC2-A464-632EF5D30760");

    /// <summary>
    /// Legt das Aufnahmeziel für ein Fenster an.
    /// </summary>
    /// <param name="handle">Das Fenster.</param>
    /// <exception cref="RecordingException">Windows hat das Fenster abgelehnt.</exception>
    public static unsafe GraphicsCaptureItem ForWindow(nint handle)
    {
        WinRT.IObjectReference factory =
            WinRT.ActivationFactory.Get("Windows.Graphics.Capture.GraphicsCaptureItem");

        Guid interopId = InteropId;
        int hr = Marshal.QueryInterface(factory.ThisPtr, in interopId, out nint interop);

        if (hr < 0 || interop == 0)
        {
            throw new RecordingException(
                "Diese Windows-Fassung kennt die Schnittstelle nicht, über die aus einem "
                + "Fenster ein Aufnahmeziel wird. Die Bildschirmaufzeichnung setzt Windows 10 "
                + "in der Fassung 1803 oder neuer voraus.");
        }

        try
        {
            // Die Funktionstabelle: die ersten drei Eintraege gehoeren IUnknown, danach kommen
            // die eigenen. CreateForWindow ist der erste davon.
            void** vtable = *(void***)interop;

            delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int> createForWindow =
                (delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)vtable[3];

            Guid itemId = CaptureItemId;
            nint raw;

            hr = createForWindow(interop, handle, &itemId, &raw);

            if (hr < 0 || raw == 0)
            {
                throw new RecordingException(
                    "Windows hat dieses Fenster nicht zur Aufnahme freigegeben "
                    + $"(Rückgabewert 0x{hr:X8}). Übliche Ursachen: Das Fenster ist inzwischen "
                    + "geschlossen, es gehört zu einem Programm mit erhöhten Rechten, oder das "
                    + "Programm hat sich ausdrücklich von der Bildschirmaufnahme ausgenommen — "
                    + "manche Fernwartungs- und Bankprogramme tun das.");
            }

            try
            {
                return GraphicsCaptureItem.FromAbi(raw);
            }
            finally
            {
                _ = Marshal.Release(raw);
            }
        }
        finally
        {
            _ = Marshal.Release(interop);
        }
    }
}
