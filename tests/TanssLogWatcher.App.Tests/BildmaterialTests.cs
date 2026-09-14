using System.Buffers.Binary;
using System.IO;
using System.Runtime.Versioning;
using System.Windows.Resources;
using Xunit;

// Das Testprojekt kennt auch System.Windows.Forms.Application; ohne Alias ist der Name
// mehrdeutig. Gemeint ist immer die WPF-Anwendung - nur sie kennt Ressourcen-Adressen.
using WpfApplication = System.Windows.Application;

namespace TanssLogWatcher.App.Tests;

/// <summary>
/// Wacht darüber, dass das mitgelieferte Bildmaterial heil ist.
/// </summary>
/// <remarks>
/// <para><b>Warum dieser Fall da ist.</b> Am 14.09.2026 fiel auf, dass das Logo auf der
/// Über-Seite fehlte. Die Datei war noch da und im Projekt eingetragen — aber ein Byte ihrer
/// Kennung war verschwunden: aus <c>89 50 4E 47 0D 0A 1A 0A</c> war
/// <c>89 50 4E 47 0A 1A 0A 00</c> geworden, die Datei genau ein Byte kürzer. Ein projektweites
/// Suchen und Ersetzen war über die Binärdatei gelaufen und hatte ein Wagenrücklaufzeichen
/// entfernt.</para>
///
/// <para><b>Das Tückische war nicht der Fehler, sondern sein Ausbleiben.</b> WPF meldet ein
/// unlesbares Bild nicht: <c>Image</c> zeigt schlicht nichts. Der Bau blieb grün, die
/// Testsammlung blieb grün, und die beschädigte Datei überstand zwei Veröffentlichungen — bis
/// jemandem am Bildschirm auffiel, dass da eine Lücke ist.</para>
///
/// <para>Diese Fälle prüfen deshalb nicht, ob eine Datei <i>vorhanden</i> ist — das war sie ja —,
/// sondern ob sie <i>heil</i> ist, bis hinunter zu den Prüfsummen der einzelnen Blöcke.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
[Collection(Ladefaelle.Name)]
public sealed class BildmaterialTests(UiThreadFixture oberflaeche)
{
    private readonly UiThreadFixture _oberflaeche = oberflaeche;

    /// <summary>Die PNG-Kennung, acht Bytes.</summary>
    /// <remarks>
    /// Die beiden Bytes <c>0D 0A</c> an vierter und fünfter Stelle stehen dort mit Absicht: Sie
    /// sind die eingebaute Falle für jede Verarbeitung, die Zeilenenden umschreibt. Genau sie
    /// ist zugeschnappt.
    /// </remarks>
    private static ReadOnlySpan<byte> PngKennung => [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

    [Fact]
    public void Das_Logo_der_Ueber_Seite_ist_ein_heiles_PNG() => _oberflaeche.Run(() =>
    {
        byte[] daten = Ressource("Assets/pronet-logo.png");

        Assert.True(daten.Length > 8, "Die Datei ist zu kurz für ein PNG.");
        Assert.True(
            daten.AsSpan(0, 8).SequenceEqual(PngKennung),
            "Die PNG-Kennung stimmt nicht. Erwartet 89504E470D0A1A0A, gefunden "
            + Convert.ToHexString(daten.AsSpan(0, 8))
            + ". Das entsteht, wenn eine Verarbeitung Zeilenenden in einer Binärdatei umschreibt.");

        (string Kind, bool Heil)[] bloecke = PngBloecke(daten);

        Assert.All(bloecke, b => Assert.True(
            b.Heil, $"Die Prüfsumme des Blocks {b.Kind} stimmt nicht — die Datei ist beschädigt."));

        Assert.Equal("IHDR", bloecke[0].Kind);
        Assert.Equal("IEND", bloecke[^1].Kind);

        // Die Masse stehen im Kopfblock und sind zugleich die Zusage, auf die sich die
        // Seitengestaltung stuetzt - AboutPage.xaml nennt sie im Kommentar.
        Assert.Equal(742, BinaryPrimitives.ReadInt32BigEndian(daten.AsSpan(16, 4)));
        Assert.Equal(258, BinaryPrimitives.ReadInt32BigEndian(daten.AsSpan(20, 4)));

        // Farbtyp 6 heisst RGBA. Ohne Alphakanal saesse das Logo auf einem Kasten, und der
        // faele in genau einer der beiden Darstellungen auf.
        Assert.Equal(6, daten[25]);
    });

    [Fact]
    public void Das_Symbol_im_Infobereich_ist_ein_heiles_Icon() => _oberflaeche.Run(() =>
    {
        byte[] daten = Ressource("Assets/tray.ico");

        Assert.True(daten.Length > 22, "Die Datei ist zu kurz für ein Icon.");

        // ICONDIR: zwei Null-Bytes, dann Typ 1 (Icon), dann die Anzahl der Bilder.
        Assert.Equal(0, BinaryPrimitives.ReadInt16LittleEndian(daten.AsSpan(0, 2)));
        Assert.Equal(1, BinaryPrimitives.ReadInt16LittleEndian(daten.AsSpan(2, 2)));

        int bilder = BinaryPrimitives.ReadInt16LittleEndian(daten.AsSpan(4, 2));
        Assert.True(bilder > 0, "Das Icon enthält kein einziges Bild.");

        // Jeder Eintrag muss auf Daten zeigen, die wirklich in der Datei liegen. Ein
        // abgeschnittenes oder umgeschriebenes Icon faellt genau hier auf.
        for (int i = 0; i < bilder; i++)
        {
            ReadOnlySpan<byte> eintrag = daten.AsSpan(6 + (i * 16), 16);
            int laenge = BinaryPrimitives.ReadInt32LittleEndian(eintrag[8..12]);
            int versatz = BinaryPrimitives.ReadInt32LittleEndian(eintrag[12..16]);

            Assert.True(laenge > 0, $"Bild {i} ist leer.");
            Assert.True(
                versatz + laenge <= daten.Length,
                $"Bild {i} reicht über das Dateiende hinaus — die Datei ist abgeschnitten.");
        }
    });

    /// <summary>
    /// Holt das Bildmaterial so, wie es im ausgelieferten Programm liegt.
    /// </summary>
    /// <remarks>
    /// Bewusst aus der eingebetteten Ressource und nicht aus dem Quellbaum: Geprüft werden soll,
    /// was der Techniker wirklich bekommt. Eine heile Datei im Quellbaum nützt nichts, wenn sie
    /// beim Einbetten verlorengeht.
    /// </remarks>
    private static byte[] Ressource(string pfad)
    {
        string assembly = typeof(global::TanssLogWatcher.App.App).Assembly.GetName().Name!;
        Uri ort = new($"pack://application:,,,/{assembly};component/{pfad}");

        StreamResourceInfo? info = WpfApplication.GetResourceStream(ort);
        Assert.NotNull(info);

        using Stream strom = info.Stream;
        using MemoryStream puffer = new();
        strom.CopyTo(puffer);
        return puffer.ToArray();
    }

    /// <summary>Läuft die Blockkette eines PNG ab und prüft jede Prüfsumme.</summary>
    private static (string Kind, bool Heil)[] PngBloecke(byte[] daten)
    {
        List<(string, bool)> bloecke = [];
        int i = 8;

        while (i + 12 <= daten.Length)
        {
            int laenge = BinaryPrimitives.ReadInt32BigEndian(daten.AsSpan(i, 4));
            if (laenge < 0 || i + 12 + laenge > daten.Length)
            {
                bloecke.Add(("abgeschnitten", false));
                break;
            }

            string kind = System.Text.Encoding.ASCII.GetString(daten, i + 4, 4);

            // Die Pruefsumme deckt Blockkind UND Blockinhalt ab, nicht nur den Inhalt.
            uint soll = BinaryPrimitives.ReadUInt32BigEndian(daten.AsSpan(i + 8 + laenge, 4));
            uint ist = Crc32(daten.AsSpan(i + 4, 4 + laenge));

            bloecke.Add((kind, soll == ist));
            i += 12 + laenge;

            if (kind == "IEND")
            {
                break;
            }
        }

        return [.. bloecke];
    }

    /// <summary>
    /// Die Prüfsumme, die PNG benutzt.
    /// </summary>
    /// <remarks>
    /// Von Hand statt aus einem Paket: <c>System.IO.Hashing</c> ist im Projekt nirgends
    /// referenziert, und ein Paket für zwölf Zeilen aufzunehmen — dessen Rückgabe zudem in der
    /// umgekehrten Byte-Reihenfolge liegt als die, die PNG in die Datei schreibt — wäre mehr
    /// Gelegenheit zum Irrtum als Ersparnis.
    /// </remarks>
    private static uint Crc32(ReadOnlySpan<byte> daten)
    {
        uint rest = 0xFFFFFFFFu;

        foreach (byte b in daten)
        {
            rest ^= b;
            for (int k = 0; k < 8; k++)
            {
                rest = (rest & 1) != 0 ? (rest >> 1) ^ 0xEDB88320u : rest >> 1;
            }
        }

        return rest ^ 0xFFFFFFFFu;
    }
}
