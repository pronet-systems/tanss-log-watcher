using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Das Grafikgerät, auf dem alle Aufnahmen laufen.
/// </summary>
/// <remarks>
/// <para><b>Eines für alle, nicht eines je Fenster.</b> Eine Sitzung bringt regelmässig mehrere
/// Fenster mit — Hauptfenster, Anmeldedialog, Fortschrittsfenster. Je Fenster ein eigenes
/// Grafikgerät wäre nicht nur verschwenderisch: Bilder von verschiedenen Geräten lassen sich
/// nicht ohne Umweg über den Hauptspeicher zusammenkopieren, und genau das soll die Leinwand
/// später vermeiden.</para>
///
/// <para><b>Ohne Hardwarebeschleunigung geht es auch.</b> Scheitert das Gerät auf der
/// Grafikkarte — ein Treiber in Reparatur, eine Sitzung über Remotedesktop —, wird die
/// Rechenvariante genommen. Sie ist langsamer und für vier Bilder je Sekunde immer noch
/// reichlich; eine Aufzeichnung, die es gar nicht gibt, wäre die schlechtere Wahl.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed partial class CaptureDevice : IDisposable
{
    private const uint DriverTypeHardware = 1;
    private const uint DriverTypeWarp = 5;

    // BGRA_SUPPORT: Ohne dieses Kennzeichen nimmt die Fensteraufnahme das Geraet nicht an.
    private const uint FlagBgraSupport = 0x20;

    private ID3D11Device? _device;
    private ID3D11DeviceContext? _context;
    private IDirect3DDevice? _winrt;

    private CaptureDevice(ID3D11Device device, ID3D11DeviceContext context,
                          IDirect3DDevice winrt, bool hardware)
    {
        _device = device;
        _context = context;
        _winrt = winrt;
        IsHardware = hardware;
    }

    /// <summary>Läuft das Gerät auf der Grafikkarte?</summary>
    /// <remarks>
    /// Für die Anzeige und für die Begleitdatei: Wer später fragt, warum eine Aufzeichnung viel
    /// Rechenzeit gekostet hat, findet die Antwort hier und muss nicht raten.
    /// </remarks>
    public bool IsHardware { get; }

    /// <summary>Das Gerät, wie die Fensteraufnahme es erwartet.</summary>
    public IDirect3DDevice WinRtDevice =>
        _winrt ?? throw new ObjectDisposedException(nameof(CaptureDevice));

    /// <summary>Das Gerät für eigene Texturen.</summary>
    public ID3D11Device Device =>
        _device ?? throw new ObjectDisposedException(nameof(CaptureDevice));

    /// <summary>Der Zeichenzusammenhang, über den kopiert wird.</summary>
    public ID3D11DeviceContext Context =>
        _context ?? throw new ObjectDisposedException(nameof(CaptureDevice));

    /// <summary>
    /// Legt das Gerät an — erst auf der Grafikkarte, sonst rechnend.
    /// </summary>
    /// <exception cref="RecordingException">Auch die Rechenvariante ist gescheitert.</exception>
    public static CaptureDevice Create()
    {
        if (TryCreate(DriverTypeHardware, out CaptureDevice? hardware))
        {
            return hardware;
        }

        if (TryCreate(DriverTypeWarp, out CaptureDevice? software))
        {
            return software;
        }

        throw new RecordingException(
            "Es liess sich kein Grafikgerät für die Aufzeichnung anlegen — weder auf der "
            + "Grafikkarte noch rechnend. Ohne Grafikgerät gibt es keine Bildschirmaufnahme. "
            + "Üblichste Ursache: ein Grafiktreiber, der gerade neu geladen wird; ein zweiter "
            + "Versuch nach einer Minute gelingt dann meist.");
    }

    /// <summary>Gibt das Gerät frei.</summary>
    public void Dispose()
    {
        if (_winrt is not null)
        {
            (_winrt as IDisposable)?.Dispose();
            _winrt = null;
        }

        if (_context is not null)
        {
            _ = Marshal.ReleaseComObject(_context);
            _context = null;
        }

        if (_device is not null)
        {
            _ = Marshal.ReleaseComObject(_device);
            _device = null;
        }
    }

    private static unsafe bool TryCreate(uint driverType, out CaptureDevice device)
    {
        device = null!;

        int hr = D3D11CreateDevice(0, driverType, 0, FlagBgraSupport, 0, 0, 7,
                                   out nint rawDevice, out _, out nint rawContext);

        if (hr < 0 || rawDevice == 0)
        {
            return false;
        }

        nint dxgi = 0;
        nint inspectable = 0;

        try
        {
            Guid dxgiIid = typeof(IDXGIDevice).GUID;

            if (Marshal.QueryInterface(rawDevice, in dxgiIid, out dxgi) < 0)
            {
                return false;
            }

            if (CreateDirect3D11DeviceFromDXGIDevice(dxgi, out inspectable) < 0)
            {
                return false;
            }

            IDirect3DDevice winrt = WinRT.MarshalInspectable<IDirect3DDevice>.FromAbi(inspectable);

            device = new CaptureDevice(
                (ID3D11Device)Marshal.GetObjectForIUnknown(rawDevice),
                (ID3D11DeviceContext)Marshal.GetObjectForIUnknown(rawContext),
                winrt,
                driverType == DriverTypeHardware);

            return true;
        }
        finally
        {
            if (inspectable != 0)
            {
                _ = Marshal.Release(inspectable);
            }

            if (dxgi != 0)
            {
                _ = Marshal.Release(dxgi);
            }

            if (rawContext != 0)
            {
                _ = Marshal.Release(rawContext);
            }

            if (rawDevice != 0)
            {
                _ = Marshal.Release(rawDevice);
            }
        }
    }

    // Von Hand erklaert und nicht ueber CsWin32: Die zweite Funktion steht in einer
    // Interop-Kopfdatei der Windows-Runtime und nicht in den Metadaten, aus denen CsWin32
    // erzeugt.
    [LibraryImport("d3d11.dll")]
    private static partial int D3D11CreateDevice(nint adapter, uint driverType, nint software,
        uint flags, nint featureLevels, uint levelCount, uint sdkVersion,
        out nint device, out uint featureLevel, out nint context);

    [LibraryImport("d3d11.dll")]
    private static partial int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice,
        out nint graphicsDevice);
}
