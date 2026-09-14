using Windows.Graphics.Imaging;
using Windows.Media.Editing;
using Windows.Storage;

namespace TanssLogWatcher.Recording.Tests;

/// <summary>
/// Ein einzelnes Bild aus einer fertigen mp4-Datei, von Windows decodiert.
/// </summary>
/// <remarks>
/// <para><b>Warum überhaupt decodiert wird.</b> Dass eine Datei entsteht, eine Dauer meldet und
/// die richtigen Abmessungen trägt, sagt nichts darüber, ob etwas darauf zu sehen ist. Genau
/// das ist der Fehler, den ein Mehrschirmaufbau hervorruft: Die Datei ist tadellos und das Bild
/// ist schwarz. Geprüft werden muss deshalb der Bildpunkt.</para>
/// <para><b>Mit dem Decoder von Windows und nicht mit einem eigenen.</b> Ein selbstgebauter
/// H.264-Leser prüfte unsere Rechnung gegen unsere Rechnung. <c>MediaComposition</c> ist
/// derselbe Weg, den ein Abspieler nimmt.</para>
/// </remarks>
internal static class Mp4Frame
{
    /// <summary>
    /// Holt ein Bild aus der Datei, in voller Grösse und ohne Skalierung.
    /// </summary>
    /// <param name="path">Die mp4-Datei.</param>
    /// <param name="at">Der Zeitpunkt ab Beginn.</param>
    /// <param name="width">Die Bildbreite der Datei.</param>
    /// <param name="height">Die Bildhöhe der Datei.</param>
    /// <returns>Die Bildpunkte als BGRA, oder <c>null</c>, wenn sich nichts lesen liess.</returns>
    public static byte[]? Of(string path, TimeSpan at, int width, int height)
    {
        StorageFile file = StorageFile.GetFileFromPathAsync(path).AsTask()
            .GetAwaiter().GetResult();

        MediaClip clip = MediaClip.CreateFromFileAsync(file).AsTask().GetAwaiter().GetResult();

        MediaComposition composition = new();
        composition.Clips.Add(clip);

        using Windows.Storage.Streams.IRandomAccessStream stream = composition
            .GetThumbnailAsync(at, width, height, VideoFramePrecision.NearestFrame)
            .AsTask().GetAwaiter().GetResult();

        BitmapDecoder decoder = BitmapDecoder.CreateAsync(stream).AsTask()
            .GetAwaiter().GetResult();

        PixelDataProvider pixels = decoder.GetPixelDataAsync(
            BitmapPixelFormat.Bgra8,
            BitmapAlphaMode.Ignore,
            new BitmapTransform { ScaledWidth = (uint)width, ScaledHeight = (uint)height },
            ExifOrientationMode.IgnoreExifOrientation,
            ColorManagementMode.DoNotColorManage).AsTask().GetAwaiter().GetResult();

        byte[] data = pixels.DetachPixelData();

        return data.Length >= width * height * 4 ? data : null;
    }

    /// <summary>Der Bildpunkt an einer Stelle, als Blau/Grün/Rot.</summary>
    /// <param name="pixels">Die Bildpunkte.</param>
    /// <param name="width">Die Bildbreite.</param>
    /// <param name="x">Die Spalte.</param>
    /// <param name="y">Die Zeile.</param>
    public static (int B, int G, int R) At(byte[] pixels, int width, int x, int y)
    {
        int at = ((y * width) + x) * 4;
        return (pixels[at], pixels[at + 1], pixels[at + 2]);
    }

    /// <summary>Ist an dieser Stelle Rot zu sehen?</summary>
    /// <remarks>
    /// Mit Spielraum: H.264 rechnet über YUV, und reines Rot kommt gemessen als etwa
    /// (0/20/250) zurück, nicht als (0/0/255).
    /// </remarks>
    /// <param name="pixels">Die Bildpunkte.</param>
    /// <param name="width">Die Bildbreite.</param>
    /// <param name="x">Die Spalte.</param>
    /// <param name="y">Die Zeile.</param>
    public static bool IsRed(byte[] pixels, int width, int x, int y)
    {
        (int b, int g, int r) = At(pixels, width, x, y);
        return r > 170 && g < 90 && b < 90;
    }

    /// <summary>Ist an dieser Stelle Schwarz?</summary>
    /// <param name="pixels">Die Bildpunkte.</param>
    /// <param name="width">Die Bildbreite.</param>
    /// <param name="x">Die Spalte.</param>
    /// <param name="y">Die Zeile.</param>
    public static bool IsBlack(byte[] pixels, int width, int x, int y)
    {
        (int b, int g, int r) = At(pixels, width, x, y);
        return r < 60 && g < 60 && b < 60;
    }
}
