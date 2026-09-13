using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace TanssLogWatcher.Recording;

/// <summary>
/// Eine einzelne Videodatei, geschrieben über Media Foundation.
/// </summary>
/// <remarks>
/// <para><b>Sie entscheidet nichts.</b> Wann eine Datei beginnt, wann eine neue fällig ist und
/// welcher Zeitstempel an welches Bild gehört, bestimmen <see cref="RecordingDirector"/> und
/// <see cref="RecordingClock"/>. Hier wird nur geschrieben.</para>
///
/// <para><b>H.264 ist die einzige gebaute Betriebsart.</b> Gemessen auf einem gewöhnlichen
/// Arbeitsplatz: H.264 hat Windows ab Werk, HEVC nur, weil das Store-Paket
/// „HEVC Video Extensions“ installiert ist. Ein Werkzeug, das ohne dieses Paket nicht
/// aufzeichnet, fiele beim zweiten Techniker aus — und ein Store-Paket lässt sich vom Setup
/// nicht still nachinstallieren.</para>
///
/// <para><b>Die Kodiereinstellungen gehen über <c>SetInputMediaType</c> und nirgendwo sonst.</b>
/// Der naheliegende Weg über <c>GetServiceForStream(ICodecAPI)</c> meldet Erfolg und tut
/// nachweislich nichts: Bei gleichem Inhalt kamen einmal 2380 KiB und einmal 932 KiB heraus, je
/// nachdem, welcher Weg benutzt wurde. Ein Fehler, der erst auffällt, wenn die Platte voll ist.
/// </para>
///
/// <para><b><see cref="Complete"/> ist nicht <see cref="Dispose()"/>.</b> Ohne den Abschluss
/// schreibt Media Foundation den Index nicht, und heraus kommt eine Datei, die kein Abspieler
/// öffnet. Deshalb sind es zwei Wege: Der eine schliesst ab, der andere räumt auf — auch dann,
/// wenn der Abschluss misslungen ist.</para>
/// </remarks>
[SupportedOSPlatform("windows6.1")]
public sealed class VideoFile : IDisposable
{
    // Die Bezeichner der Kodiereinstellungen. CsWin32 erzeugt sie nicht, weil sie in codecapi.h
    // stehen und nicht in den Metadaten der Schnittstelle. Werte aus dem Windows SDK.
    private static readonly Guid RateControlMode =
        new("1c0608e9-370c-4710-8a58-cb6181c42423");

    private static readonly Guid Quality =
        new("fcbf57a3-7ea5-4b0c-9644-69b40c39c391");

    private static readonly Guid GopSize =
        new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    private static readonly Guid BPictureCount =
        new("8d390aac-dc5c-4200-b57f-814d04bab53b");

    /// <summary>Qualitätsgeführte Ratensteuerung.</summary>
    /// <remarks>
    /// Nicht feste Bitrate: Ein stehender Bildschirm braucht fast nichts, eine gescrollte
    /// Protokolldatei viel. Eine feste Rate verschwendete im ersten Fall Platz und liesse im
    /// zweiten die Schrift verschwimmen — und Schrift ist das Einzige, worauf es hier ankommt.
    /// </remarks>
    private const uint QualityMode = 3;

    private readonly uint _streamIndex;
    private readonly int _width;
    private readonly int _height;
    private readonly int _stride;

    /// <summary>Die kleinste Länge eines Bruchstücks, in 100-ns-Einheiten: eine Sekunde.</summary>
    /// <remarks>
    /// Gemessen an 200 Bildern 1920×1080 bei 4 B/s: Die Zahl der Bruchstücke folgt der Dauer
    /// genau (0,5 s → 100 Stück, 1 s → 50, 2 s → 25, 4 s → 13), der Verlust bei einem Absturz
    /// aber kaum — 15, 17, 17 und 24 von 200 Bildern. Der Verlust steckt also fast ganz im
    /// Rückstau des H.264-Kodierers und nicht in der Bruchstücklänge. Kürzer als eine Sekunde
    /// bringt deshalb nichts mehr und verdoppelt nur die Verwaltungsdaten.
    /// </remarks>
    private const ulong MinimumFragment = 10_000_000;

    private IMFSinkWriter? _writer;
    private IMFMediaSink? _sink;
    private IMFByteStream? _byteStream;
    private bool _completed;

    private VideoFile(IMFSinkWriter writer, IMFMediaSink? sink, IMFByteStream? byteStream,
                      uint streamIndex, int width, int height)
    {
        _writer = writer;
        _sink = sink;
        _byteStream = byteStream;
        _streamIndex = streamIndex;
        _width = width;
        _height = height;
        _stride = width * 4;
    }

    /// <summary>
    /// Wird gerufen, wenn der bruchstückweise Weg versagt hat und zurückgefallen wird.
    /// </summary>
    /// <remarks>
    /// Kein Fehler, sondern eine Meldung fürs Protokoll: Die Aufzeichnung läuft weiter, sie
    /// überlebt nur einen Absturz nicht mehr. Wer das nicht protokolliert, sucht später
    /// vergeblich, warum ausgerechnet auf diesem Rechner eine Datei ganz verloren ging.
    /// </remarks>
    public static Action<Exception>? Fallback { get; set; }

    /// <summary>Ob die Datei bruchstückweise geschrieben wird.</summary>
    /// <remarks>
    /// Gehört in die Begleitdatei der Aufzeichnung: Wer später eine abgebrochene Datei in der
    /// Hand hält, muss wissen, ob er Bruchstücke erwarten darf.
    /// </remarks>
    public bool Fragmented => _sink is not null;

    /// <summary>Die Breite der Datei in Bildpunkten.</summary>
    public int Width => _width;

    /// <summary>Die Höhe der Datei in Bildpunkten.</summary>
    public int Height => _height;

    /// <summary>Wie viele Bilder geschrieben wurden.</summary>
    public long WrittenFrames { get; private set; }

    /// <summary>
    /// Legt eine Datei an und bereitet den Kodierer vor.
    /// </summary>
    /// <param name="path">Der vollständige Pfad; die Endung bestimmt den Behälter (.mp4).</param>
    /// <param name="width">Die Breite; muss gerade sein.</param>
    /// <param name="height">Die Höhe; muss gerade sein.</param>
    /// <param name="framesPerSecond">Die Bildrate der Zeitachse.</param>
    /// <param name="quality">Die Qualitätsstufe von 1 bis 100.</param>
    /// <exception cref="ArgumentException">Eine Kantenlänge ist ungerade oder zu klein.</exception>
    /// <exception cref="RecordingException">Media Foundation hat abgelehnt.</exception>
    public static unsafe VideoFile Create(string path, int width, int height,
                                          int framesPerSecond, int quality = 70)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentOutOfRangeException.ThrowIfLessThan(framesPerSecond, 1);
        ArgumentOutOfRangeException.ThrowIfLessThan(quality, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(quality, 100);

        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0)
        {
            throw new ArgumentException(
                $"Die Leinwand {width}×{height} taugt nicht: H.264 mit 4:2:0 legt die "
                + "Farbanteile auf halbe Auflösung und verlangt deshalb gerade Kantenlängen. "
                + $"{nameof(CanvasLayout)} rundet von sich aus auf — ein ungerader Wert kommt "
                + "also nicht von dort.",
                nameof(width));
        }

        MediaFoundation.Start();

        try
        {
            try
            {
                // Die Pruefung ist keine Foermlichkeit: MFCreateFMPEG4MediaSink traegt in den
                // Metadaten windows8.0, VideoFile traegt windows6.1. Ohne diese Wache meldet
                // CA1416 - und unter TreatWarningsAsErrors steht der Bau. Gemessen: (6, 2)
                // reicht dem Analysator NICHT, (8) raeumt die Meldung weg.
                if (!OperatingSystem.IsWindowsVersionAtLeast(8))
                {
                    return OpenWhole(path, width, height, framesPerSecond, quality);
                }

                return OpenFragmented(path, width, height, framesPerSecond, quality);
            }
            catch (Exception ex) when (ex is not RecordingException
                                       and not OutOfMemoryException)
            {
                // Hausregel 5, und breit gefangen mit Absicht. CsWin32 uebersetzt HRESULTs
                // ueber Marshal.ThrowExceptionForHR, und das liefert je nach Wert eine andere
                // CLR-Art: E_FAIL und die MF_E_* werden zur COMException, E_NOINTERFACE zur
                // InvalidCastException - aber E_INVALIDARG zur ArgumentException, E_NOTIMPL zur
                // NotImplementedException und E_ACCESSDENIED zur UnauthorizedAccessException.
                // Gemessen mit eingespritzten HRESULTs: Ein Filter auf COMException und
                // InvalidCastException laesst genau die drei letzten durch, und dann kostet ein
                // zickiger Kodierer die ganze Aufzeichnung statt nur die Absturzsicherung.
                // Ausgenommen bleiben die RecordingException - die meldet ein Dateiproblem, das
                // der Weg am Stueck genauso haette - und der Speichermangel, bei dem ein
                // zweiter Versuch nichts besser macht.
                Fallback?.Invoke(ex);
                return OpenWhole(path, width, height, framesPerSecond, quality);
            }
        }
        catch (RecordingException)
        {
            MediaFoundation.Stop();
            throw;
        }
        catch (Exception ex)
        {
            MediaFoundation.Stop();
            throw new RecordingException(NotWritable(path), ex);
        }
    }

    /// <summary>
    /// Schreibt ein Bild.
    /// </summary>
    /// <remarks>
    /// Der Zeitstempel kommt von aussen und wird hier nicht gerechnet — er stammt aus der
    /// <see cref="RecordingClock"/>, die während einer Pause steht. Würde er hier aus der
    /// Wanduhr genommen, trüge die Datei die Pausen als Standbild und behauptete eine Dauer,
    /// die es nicht gab.
    /// </remarks>
    /// <param name="bgra">Die Bildpunkte, 4 Byte je Punkt, von oben nach unten.</param>
    /// <param name="timestamp">Der Zeitstempel des Bildes ab Beginn der Aufzeichnung.</param>
    /// <param name="duration">Wie lange das Bild steht.</param>
    /// <exception cref="ArgumentException">Der Puffer passt nicht zur Leinwand.</exception>
    /// <exception cref="ObjectDisposedException">Die Datei ist schon abgeschlossen.</exception>
    public unsafe void Write(ReadOnlySpan<byte> bgra, TimeSpan timestamp, TimeSpan duration)
    {
        ObjectDisposedException.ThrowIf(_writer is null, this);

        int expected = _stride * _height;

        if (bgra.Length != expected)
        {
            throw new ArgumentException(
                $"Der Puffer trägt {bgra.Length} Byte, die Leinwand {_width}×{_height} "
                + $"verlangt {expected}. Ein halbes Bild zu schreiben ergäbe ein halbes Bild.",
                nameof(bgra));
        }

        PInvoke.MFCreateMemoryBuffer((uint)expected, out IMFMediaBuffer buffer);

        try
        {
            byte* target = null;
            buffer.Lock(&target, null, null);

            try
            {
                bgra.CopyTo(new Span<byte>(target, expected));
            }
            finally
            {
                buffer.Unlock();
            }

            buffer.SetCurrentLength((uint)expected);

            PInvoke.MFCreateSample(out IMFSample sample);

            try
            {
                sample.AddBuffer(buffer);
                sample.SetSampleTime(timestamp.Ticks);
                sample.SetSampleDuration(duration.Ticks);

                _writer!.WriteSample(_streamIndex, sample);
                WrittenFrames++;
            }
            finally
            {
                _ = Marshal.ReleaseComObject(sample);
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(buffer);
        }
    }

    /// <summary>
    /// Schliesst die Datei ab.
    /// </summary>
    /// <remarks>
    /// <b>Ohne diesen Aufruf gibt es keinen Index und damit keine abspielbare Datei.</b> Genau
    /// deshalb ist er getrennt von <see cref="Dispose()"/>: Aufräumen muss auch dann gehen,
    /// wenn der Abschluss misslungen ist — sonst bliebe die Datei gesperrt.
    /// </remarks>
    public void Complete()
    {
        if (_writer is null || _completed)
        {
            return;
        }

        _writer.Finalize();
        _completed = true;
    }

    /// <summary>Gibt den Kodierer frei.</summary>
    public void Dispose()
    {
        if (_writer is null)
        {
            return;
        }

        _ = Marshal.ReleaseComObject(_writer);
        _writer = null;

        // Der Schreiber aus MFCreateSinkWriterFromURL nimmt seine Senke mit, der aus
        // MFCreateSinkWriterFromMediaSink nicht. Wer hier nicht selbst herunterfaehrt, laesst
        // die Datei offen, und die naechste Aufzeichnung findet sie gesperrt.
        if (_sink is not null)
        {
            // Shutdown schliesst den Byte-Strom gleich mit. Ein Close danach meldet gemessen
            // E_INVALIDARG - und CsWin32 macht daraus eine ArgumentException, keine
            // COMException. Also gar nicht erst schliessen, nur loslassen.
            Shutdown(_sink);
            _ = Marshal.ReleaseComObject(_sink);
            _sink = null;
        }

        if (_byteStream is not null)
        {
            _ = Marshal.ReleaseComObject(_byteStream);
            _byteStream = null;
        }

        MediaFoundation.Stop();
    }

    private static string NotWritable(string path) =>
        $"Media Foundation konnte „{path}“ nicht zum Schreiben anlegen. Üblichste Ursache: "
        + "Der Ordner gibt es nicht oder er ist schreibgeschützt.";

    // Aufraeumen darf nie scheitern (Hausregel 5). CsWin32 uebersetzt die HRESULTs dieser
    // Aufrufe in verschiedene CLR-Arten - E_INVALIDARG wird zur ArgumentException, nicht zur
    // COMException -, deshalb wird hier breit gefangen und nichts weitergereicht.
    private static void Shutdown(IMFMediaSink sink)
    {
        try
        {
            sink.Shutdown();
        }
        catch (Exception)
        {
        }
    }

    private static void Close(IMFByteStream stream)
    {
        try
        {
            stream.Close();
        }
        catch (Exception)
        {
        }
    }

    /// <summary>
    /// Legt die Datei bruchstückweise an: eigener Byte-Strom, fragmentierende Senke, Schreiber
    /// darauf.
    /// </summary>
    /// <remarks>
    /// <para><b>Wofür das gut ist.</b> Der Weg am Stück schreibt den Index (<c>moov</c>) erst
    /// beim Abschluss. Gemessen an 200 Bildern 1920×1080 bei 4 B/s, Prozess nach dem letzten
    /// Bild hart getötet: Die Datei des ganzen Weges trug 21,6 MB Nutzdaten, aber keinen
    /// <c>moov</c>-Block — Windows öffnete sie gar nicht erst (0xC00D36C4), 0 von 200 Bildern.
    /// Die bruchstückweise Datei trug Kopf und 46 vollständige <c>moof</c>/<c>mdat</c>-Paare;
    /// ein <c>IMFSourceReader</c> las 183 von 200 Bildern daraus. <b>Ein Absturz kostet also
    /// 17 Bilder statt aller 200.</b></para>
    /// <para><b>Was es kostet.</b> Bei gleichem Inhalt 23.219.569 statt 23.214.070 Byte, also
    /// <b>+0,0237 %</b>. So wenig, weil die 50 <c>moof</c>-Blöcke zwar hinzukommen, der
    /// <c>moov</c>-Block dafür von 3.075 auf 662 Byte schrumpft — die Abtastwerttabellen
    /// entfallen dort.</para>
    /// <para>Die Senke bringt ihren Datenstrom schon mit; er hat fest die Nummer 0, und
    /// <c>AddStream</c> wird hier deshalb nicht gerufen.</para>
    /// </remarks>
    [SupportedOSPlatform("windows8.0")]
    private static VideoFile OpenFragmented(string path, int width, int height,
                                            int framesPerSecond, int quality)
    {
        HRESULT opened = PInvoke.MFCreateFile(MF_FILE_ACCESSMODE.MF_ACCESSMODE_WRITE,
                                              MF_FILE_OPENMODE.MF_OPENMODE_DELETE_IF_EXIST,
                                              MF_FILE_FLAGS.MF_FILEFLAGS_NONE,
                                              path,
                                              out IMFByteStream byteStream);

        // Eine RecordingException, keine COMException: Ein Ordner, den es nicht gibt, ist kein
        // Grund, es mit dem ganzen Weg noch einmal zu versuchen - der scheiterte genauso, und
        // der Rueckfall stuende faelschlich im Protokoll.
        if (opened.Failed)
        {
            throw new RecordingException(NotWritable(path),
                                         Marshal.GetExceptionForHR(opened.Value)!);
        }

        IMFMediaSink? sink = null;
        IMFSinkWriter? writer = null;

        try
        {
            IMFMediaType output = VideoType(width, height, framesPerSecond);

            try
            {
                PInvoke.MFCreateFMPEG4MediaSink(byteStream, output, null, out sink)
                       .ThrowOnFailure();
            }
            finally
            {
                _ = Marshal.ReleaseComObject(output);
            }

            // Das einzige Attribut, das hier gesetzt wird - und es muss vor BeginWriting
            // stehen. Gemessen: Die Zahl der moof-Bloecke folgt diesem Wert genau.
            ((IMFAttributes)sink).SetUINT64(PInvoke.MF_MPEG4SINK_MIN_FRAGMENT_DURATION,
                                            MinimumFragment);

            PInvoke.MFCreateSinkWriterFromMediaSink(sink, null, out writer).ThrowOnFailure();

            SetInput(writer, 0, width, height, framesPerSecond, quality);
            writer.BeginWriting();

            return new VideoFile(writer, sink, byteStream, 0, width, height);
        }
        catch
        {
            if (writer is not null)
            {
                _ = Marshal.ReleaseComObject(writer);
            }

            if (sink is not null)
            {
                Shutdown(sink);
                _ = Marshal.ReleaseComObject(sink);
            }
            else
            {
                // Nur wenn es noch keine Senke gibt, die den Strom mitnehmen koennte.
                Close(byteStream);
            }

            _ = Marshal.ReleaseComObject(byteStream);
            throw;
        }
    }

    /// <summary>Legt die Datei am Stück an — der Weg von früher, jetzt der Rückfall.</summary>
    private static VideoFile OpenWhole(string path, int width, int height,
                                       int framesPerSecond, int quality)
    {
        PInvoke.MFCreateSinkWriterFromURL(path, null, null, out IMFSinkWriter writer)
               .ThrowOnFailure();

        try
        {
            uint stream = AddStream(writer, width, height, framesPerSecond, quality);
            writer.BeginWriting();
            return new VideoFile(writer, null, null, stream, width, height);
        }
        catch
        {
            _ = Marshal.ReleaseComObject(writer);
            throw;
        }
    }

    private static uint AddStream(IMFSinkWriter writer, int width, int height,
                                  int framesPerSecond, int quality)
    {
        IMFMediaType output = VideoType(width, height, framesPerSecond);

        try
        {
            writer.AddStream(output, out uint stream);
            SetInput(writer, stream, width, height, framesPerSecond, quality);
            return stream;
        }
        finally
        {
            _ = Marshal.ReleaseComObject(output);
        }
    }

    // Die Ausgabe: H.264, High Profile. Die Bitrate ist eine Vorgabe und keine Zusage -
    // die Ratensteuerung in SetInput fuehrt ueber die Qualitaet, nicht ueber die Rate.
    // Getrennt von AddStream, weil MFCreateFMPEG4MediaSink diesen Typ schon beim Anlegen
    // verlangt, lange bevor es einen Schreiber gibt.
    private static IMFMediaType VideoType(int width, int height, int framesPerSecond)
    {
        PInvoke.MFCreateMediaType(out IMFMediaType output);

        try
        {
            output.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            output.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_H264);
            output.SetUINT32(PInvoke.MF_MT_MPEG2_PROFILE, 100);
            output.SetUINT32(PInvoke.MF_MT_AVG_BITRATE, Bitrate(width, height, framesPerSecond));
            output.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE,
                             (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            SetSize(output, PInvoke.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            SetSize(output, PInvoke.MF_MT_FRAME_RATE, (uint)framesPerSecond, 1);
            SetSize(output, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);
            return output;
        }
        catch
        {
            _ = Marshal.ReleaseComObject(output);
            throw;
        }
    }

    // Die Eingabe: BGRA, von oben nach unten. Der positive Streifenabstand ist noetig - ohne
    // ihn nimmt Media Foundation die Zeilen von unten nach oben und das Bild steht auf dem
    // Kopf. HIER, und nur hier, wirken auch die Kodiereinstellungen: Ueber
    // GetServiceForStream(ICodecAPI) melden dieselben Werte Erfolg und tun nichts -
    // nachgemessen an identischem Inhalt, 2380 KiB gegen 932 KiB.
    private static void SetInput(IMFSinkWriter writer, uint stream, int width, int height,
                                 int framesPerSecond, int quality)
    {
        PInvoke.MFCreateMediaType(out IMFMediaType input);

        try
        {
            input.SetGUID(PInvoke.MF_MT_MAJOR_TYPE, PInvoke.MFMediaType_Video);
            input.SetGUID(PInvoke.MF_MT_SUBTYPE, PInvoke.MFVideoFormat_RGB32);
            input.SetUINT32(PInvoke.MF_MT_INTERLACE_MODE,
                            (uint)MFVideoInterlaceMode.MFVideoInterlace_Progressive);
            input.SetUINT32(PInvoke.MF_MT_DEFAULT_STRIDE, (uint)(width * 4));
            input.SetUINT32(PInvoke.MF_MT_ALL_SAMPLES_INDEPENDENT, 1);
            SetSize(input, PInvoke.MF_MT_FRAME_SIZE, (uint)width, (uint)height);
            SetSize(input, PInvoke.MF_MT_FRAME_RATE, (uint)framesPerSecond, 1);
            SetSize(input, PInvoke.MF_MT_PIXEL_ASPECT_RATIO, 1, 1);

            PInvoke.MFCreateMediaType(out IMFMediaType parameters);

            try
            {
                parameters.SetUINT32(RateControlMode, QualityMode);
                parameters.SetUINT32(Quality, (uint)quality);
                parameters.SetUINT32(GopSize, (uint)(framesPerSecond * 4));
                parameters.SetUINT32(BPictureCount, 0);

                writer.SetInputMediaType(stream, input, parameters);
            }
            finally
            {
                _ = Marshal.ReleaseComObject(parameters);
            }
        }
        finally
        {
            _ = Marshal.ReleaseComObject(input);
        }
    }

    /// <summary>
    /// Eine Vorgabe für die Bitrate, aus Fläche und Bildrate.
    /// </summary>
    /// <remarks>
    /// Sie ist eine Vorgabe und keine Zusage: Geführt wird über die Qualität. Ganz weglassen
    /// lässt sich der Wert trotzdem nicht — ohne ihn lehnen manche Kodierer den Ausgabetyp ab.
    /// Die Formel ist grob und soll es sein: 0,1 Bit je Bildpunkt und Bild, mit Untergrenze,
    /// damit ein kleines Fenster nicht auf eine unbrauchbare Rate fällt.
    /// </remarks>
    private static uint Bitrate(int width, int height, int framesPerSecond)
    {
        double bits = (double)width * height * framesPerSecond * 0.1;
        return (uint)Math.Clamp(bits, 1_000_000, 60_000_000);
    }

    // Zwei 32-Bit-Werte in einem 64-Bit-Attribut, oberer Wert zuerst. So legt Media Foundation
    // Groesse, Bildrate und Seitenverhaeltnis ab; die Hilfsfunktion des SDK tut nichts anderes.
    private static void SetSize(IMFMediaType type, Guid key, uint high, uint low) =>
        type.SetUINT64(key, ((ulong)high << 32) | low);
}
