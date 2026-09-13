using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Win32.Graphics.Direct3D11;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Die Aufnahme genau eines Fensters.
/// </summary>
/// <remarks>
/// <para><b>Warum je Fenster eine eigene.</b> Gemessen: Die Fensteraufnahme von Windows liefert
/// besessene Unterfenster <b>nicht</b> mit — ein grüner Dialog über einem roten Hauptfenster
/// ergab an seiner Stelle reines Rot. Wer nur das Hauptfenster aufnähme, verlöre genau die
/// Dialoge, um die es hinterher geht. Die Schnittstelle, die das abnähme
/// (<c>IncludeSecondaryWindows</c>), wirft auf Windows 11 Build 22631 eine Ausnahme, obwohl sie
/// in der Projektion steht.</para>
///
/// <para><b>Was diese Aufnahme von selbst richtig macht</b> — alles gemessen, nicht
/// angenommen: Sie folgt ihrem Fenster über Bildschirmgrenzen, ohne dass jemand umschaltet. Sie
/// liefert den Inhalt des Fensters auch dann, wenn ein fremdes darüber liegt. Und bei
/// Minimierung liefert sie exakt nichts mehr, ohne zu enden — daraus wird die Pause, ohne dass
/// es dafür eine eigene Mechanik bräuchte.</para>
///
/// <para><b>Der gelbe Rahmen bleibt an.</b> Er lässt sich abschalten, landet aber nachweislich
/// nicht im aufgezeichneten Bild — die Randpunkte waren in beiden Betriebsarten schwarz. Damit
/// warnt er den Techniker, ohne die Dokumentation zu verschmutzen. Ein Werkzeug, das sonst
/// überall darauf besteht, nichts zu verbergen, sollte nicht ausgerechnet die Aufzeichnung
/// unsichtbar machen.</para>
/// </remarks>
[SupportedOSPlatform("windows10.0.19041.0")]
internal sealed class WindowCapture : IDisposable
{
    private readonly CaptureDevice _device;
    private readonly nint _handle;

    private Direct3D11CaptureFramePool? _pool;
    private GraphicsCaptureSession? _session;
    private ID3D11Texture2D? _staging;
    private int _stagingWidth;
    private int _stagingHeight;

    private WindowCapture(CaptureDevice device, nint handle,
                          Direct3D11CaptureFramePool pool, GraphicsCaptureSession session)
    {
        _device = device;
        _handle = handle;
        _pool = pool;
        _session = session;
    }

    /// <summary>Das aufgenommene Fenster.</summary>
    public nint Handle => _handle;

    /// <summary>Steht die Bildschirmaufnahme auf diesem Rechner überhaupt zur Verfügung?</summary>
    /// <remarks>
    /// Sie wird gefragt und nicht vorausgesetzt. Auf einem Rechner ohne sie muss die
    /// Einstellungsseite es sagen können, statt die Aufzeichnung stumm scheitern zu lassen.
    /// </remarks>
    public static bool IsSupported()
    {
        try
        {
            return GraphicsCaptureSession.IsSupported();
        }
        catch (Exception ex) when (ex is TypeLoadException or EntryPointNotFoundException
                                      or DllNotFoundException)
        {
            return false;
        }
    }

    /// <summary>
    /// Beginnt die Aufnahme eines Fensters.
    /// </summary>
    /// <param name="device">Das gemeinsame Grafikgerät.</param>
    /// <param name="handle">Das Fenster.</param>
    /// <param name="showBorder">Soll der Aufnahmerahmen sichtbar sein?</param>
    /// <exception cref="RecordingException">Das Fenster lässt sich nicht aufnehmen.</exception>
    public static WindowCapture Start(CaptureDevice device, nint handle, bool showBorder = true)
    {
        ArgumentNullException.ThrowIfNull(device);

        if (handle == 0)
        {
            throw new RecordingException(
                "Zu diesem Fenster gibt es kein Handle. Das Fenster ist zwischen dem Erfassen "
                + "der Sitzung und dem Beginn der Aufzeichnung geschlossen worden.");
        }

        return Start(device, handle, CaptureItemFactory.ForWindow(handle), showBorder);
    }

    /// <summary>
    /// Beginnt die Aufnahme eines ganzen Bildschirms.
    /// </summary>
    /// <remarks>
    /// <para><b>Ein Unterschied, der im Direktor zählt:</b> Eine Bildschirmaufnahme hört
    /// gemessen <b>nie</b> auf zu liefern — zwölf von zwölf Abfragen an einem ruhenden
    /// Bildschirm brachten ein Bild. Die Pause bei minimiertem Fenster entsteht in dieser
    /// Betriebsart also nicht von selbst; sie hängt daran, dass der Direktor die Sichtbarkeit
    /// der Sitzungsfenster prüft. Das ist keine Feinheit, sondern der Grund, warum im
    /// Bildschirmbetrieb nicht der private Bildschirm des Technikers mitläuft, während die
    /// Fernwartung minimiert ist.</para>
    /// </remarks>
    /// <param name="device">Das gemeinsame Grafikgerät.</param>
    /// <param name="monitor">Das Bildschirmhandle.</param>
    /// <param name="showBorder">Soll der Aufnahmerahmen sichtbar sein?</param>
    /// <exception cref="RecordingException">Der Bildschirm lässt sich nicht aufnehmen.</exception>
    public static WindowCapture StartScreen(CaptureDevice device, nint monitor,
                                            bool showBorder = true)
    {
        ArgumentNullException.ThrowIfNull(device);

        return Start(device, monitor, CaptureItemFactory.ForMonitor(monitor), showBorder);
    }

    private static WindowCapture Start(CaptureDevice device, nint handle,
                                       GraphicsCaptureItem item, bool showBorder)
    {
        Direct3D11CaptureFramePool pool = Direct3D11CaptureFramePool.CreateFreeThreaded(
            device.WinRtDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 2, item.Size);

        GraphicsCaptureSession session = pool.CreateCaptureSession(item);

        // Der Mauszeiger gehoert dazu: Ohne ihn ist im Video nicht zu sehen, worauf der
        // Techniker geklickt hat - und genau das ist die Frage, die eine Dokumentation
        // beantworten soll.
        session.IsCursorCaptureEnabled = true;

        TrySetBorder(session, showBorder);
        session.StartCapture();

        return new WindowCapture(device, handle, pool, session);
    }

    /// <summary>
    /// Holt das neueste Bild, falls eines vorliegt.
    /// </summary>
    /// <remarks>
    /// <para>Es wird nicht gewartet. Liegt nichts an, hat sich am Fenster nichts geändert — oder
    /// es ist minimiert. Beides beantwortet der Aufrufer, nicht diese Stelle.</para>
    /// <para>Der Rückgabepuffer gehört dem Aufrufer bis zum nächsten Aufruf. Er wird
    /// wiederverwendet: Bei vier Bildern je Sekunde und einem grossen Fenster wären zwanzig
    /// Megabyte je Bild sonst zwanzig Megabyte Müll je Bild.</para>
    /// </remarks>
    /// <param name="buffer">Der Puffer, in den kopiert wird; wird bei Bedarf vergrössert.</param>
    /// <returns>Die Abmessungen des Bildes, oder <c>null</c>, wenn gerade keines vorliegt.</returns>
    public unsafe FrameSize? TryCopyLatest(ref byte[] buffer)
    {
        if (_pool is null)
        {
            return null;
        }

        using Direct3D11CaptureFrame? frame = _pool.TryGetNextFrame();

        if (frame is null)
        {
            return null;
        }

        int width = frame.ContentSize.Width;
        int height = frame.ContentSize.Height;

        if (width <= 0 || height <= 0)
        {
            return null;
        }

        ID3D11Texture2D source = TextureOf(frame);

        try
        {
            EnsureStaging(width, height);

            // Der Bildbeutel ist oft groesser als der Inhalt - er folgt der Fenstergroesse mit
            // Verzoegerung. Deshalb wird genau der Inhaltsbereich kopiert und nicht die ganze
            // Textur: sonst stuende am Rand der Rest des vorigen, groesseren Fensters.
            D3D11_BOX box = new()
            {
                left = 0,
                top = 0,
                front = 0,
                right = (uint)width,
                bottom = (uint)height,
                back = 1,
            };

            _device.Context.CopySubresourceRegion(_staging!, 0, 0, 0, 0, source, 0, &box);

            D3D11_MAPPED_SUBRESOURCE mapped;
            _device.Context.Map(_staging!, 0, D3D11_MAP.D3D11_MAP_READ, 0, &mapped);

            try
            {
                int stride = width * 4;
                int needed = stride * height;

                if (buffer.Length < needed)
                {
                    buffer = new byte[needed];
                }

                byte* from = (byte*)mapped.pData;

                for (int row = 0; row < height; row++)
                {
                    new ReadOnlySpan<byte>(from + (row * mapped.RowPitch), stride)
                        .CopyTo(buffer.AsSpan(row * stride, stride));
                }

                return new FrameSize(width, height);
            }
            finally
            {
                _device.Context.Unmap(_staging!, 0);
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(source);
        }
    }

    /// <summary>Beendet die Aufnahme.</summary>
    public void Dispose()
    {
        _session?.Dispose();
        _session = null;

        _pool?.Dispose();
        _pool = null;

        if (_staging is not null)
        {
            _ = Marshal.ReleaseComObject(_staging);
            _staging = null;
        }
    }

    /// <summary>
    /// Schaltet den Aufnahmerahmen, wenn diese Windows-Fassung es kennt.
    /// </summary>
    /// <remarks>
    /// Über Reflexion und in einem <c>try</c>: Die Eigenschaft steht in der Projektion, wirft
    /// auf älteren Windows-Fassungen aber beim Zugriff — gemessen an
    /// <c>IncludeSecondaryWindows</c> und <c>MinUpdateInterval</c>, die sich genauso verhalten.
    /// Eine Versionsabfrage wäre hier die schlechtere Wahl: Sie behauptete zu wissen, ab welcher
    /// Fassung es geht, statt es zu probieren.
    /// </remarks>
    private static void TrySetBorder(GraphicsCaptureSession session, bool visible)
    {
        try
        {
            typeof(GraphicsCaptureSession)
                .GetProperty("IsBorderRequired")?
                .SetValue(session, visible);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Diese Windows-Fassung kennt den Schalter nicht. Der Rahmen bleibt dann, wie
            // Windows ihn vorsieht - sichtbar, und das ist die Betriebsart, die wir ohnehin
            // wollen.
        }
    }

    private unsafe void EnsureStaging(int width, int height)
    {
        if (_staging is not null && _stagingWidth == width && _stagingHeight == height)
        {
            return;
        }

        if (_staging is not null)
        {
            _ = Marshal.ReleaseComObject(_staging);
            _staging = null;
        }

        D3D11_TEXTURE2D_DESC description = new()
        {
            Width = (uint)width,
            Height = (uint)height,
            MipLevels = 1,
            ArraySize = 1,
            Format = Windows.Win32.Graphics.Dxgi.Common.DXGI_FORMAT.DXGI_FORMAT_B8G8R8A8_UNORM,
            SampleDesc = new Windows.Win32.Graphics.Dxgi.Common.DXGI_SAMPLE_DESC
            {
                Count = 1,
                Quality = 0,
            },
            Usage = D3D11_USAGE.D3D11_USAGE_STAGING,
            BindFlags = 0,
            CPUAccessFlags = D3D11_CPU_ACCESS_FLAG.D3D11_CPU_ACCESS_READ,
            MiscFlags = 0,
        };

        // Die erzeugte Fassung nimmt einen Zeiger auf die unverwaltete Gegenstelle. Der Weg
        // ueber Marshal ist der einzige, der von dort zu einem verwalteten Verweis fuehrt.
        ID3D11Texture2D_unmanaged* raw = null;
        _device.Device.CreateTexture2D(&description, null, &raw);

        try
        {
            _staging = (ID3D11Texture2D)Marshal.GetObjectForIUnknown((nint)raw);
        }
        finally
        {
            _ = Marshal.Release((nint)raw);
        }

        _stagingWidth = width;
        _stagingHeight = height;
    }

    /// <summary>
    /// Der Weg von der Windows-Runtime-Oberfläche zur Direct3D-Textur.
    /// </summary>
    /// <remarks>
    /// Wieder über die Funktionstabelle, und aus demselben Grund wie in
    /// <see cref="CaptureItemFactory"/>: Ein WinRT-Objekt lässt sich in .NET nicht auf eine
    /// klassische COM-Schnittstelle umwandeln — gemessen, die Umwandlung wirft
    /// <c>InvalidCastException</c>. <c>IDirect3DDxgiInterfaceAccess</c> hat genau einen eigenen
    /// Eintrag, und der liefert die Textur.
    /// </remarks>
    private static unsafe ID3D11Texture2D TextureOf(Direct3D11CaptureFrame frame)
    {
        nint surface = WinRT.MarshalInspectable<IDirect3DSurface>.FromManaged(frame.Surface);

        try
        {
            Guid accessId = DxgiInterfaceAccessId;
            int hr = Marshal.QueryInterface(surface, in accessId, out nint access);

            if (hr < 0 || access == 0)
            {
                throw new RecordingException(
                    "Das aufgenommene Bild liess sich nicht als Direct3D-Textur öffnen. Das "
                    + "deutet auf einen Grafiktreiber hin, der gerade neu geladen wird.");
            }

            try
            {
                void** vtable = *(void***)access;

                delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int> getInterface =
                    (delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)vtable[3];

                Guid textureId = Texture2DId;
                nint raw;

                hr = getInterface(access, &textureId, &raw);

                if (hr < 0 || raw == 0)
                {
                    throw new RecordingException(
                        $"Die Direct3D-Textur des Bildes war nicht zu holen (0x{hr:X8}).");
                }

                try
                {
                    return (ID3D11Texture2D)Marshal.GetObjectForIUnknown(raw);
                }
                finally
                {
                    _ = Marshal.Release(raw);
                }
            }
            finally
            {
                _ = Marshal.Release(access);
            }
        }
        finally
        {
            _ = Marshal.Release(surface);
        }
    }

    // IDirect3DDxgiInterfaceAccess - der Weg von der Windows-Runtime zu Direct3D.
    private static readonly Guid DxgiInterfaceAccessId =
        new("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1");

    // ID3D11Texture2D.
    private static readonly Guid Texture2DId = new("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
}

/// <summary>Die Abmessungen eines aufgenommenen Bildes.</summary>
/// <param name="Width">Breite in Bildpunkten.</param>
/// <param name="Height">Höhe in Bildpunkten.</param>
public readonly record struct FrameSize(int Width, int Height);

