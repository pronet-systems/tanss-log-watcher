using System.Text;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class AtomicFileTests
{
    [Fact]
    public void Ein_Abbruch_laesst_den_alten_Stand_stehen()
    {
        using TempDirectory temp = new();
        string target = temp.File("config.json");
        AtomicFile.WriteText(target, "{ \"alt\": true }");

        // Der gefaehrliche Fall: Der Schreibvorgang bricht mittendrin ab, nachdem bereits
        // Bytes geflossen sind. Wuerde direkt in die Zieldatei geschrieben, staende hier
        // danach ein halbes JSON - und der naechste Start haette weder den alten noch den
        // neuen Stand.
        InvalidOperationException error = Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Write(target, stream =>
            {
                byte[] half = Encoding.UTF8.GetBytes("{ \"neu\": tru");
                stream.Write(half, 0, half.Length);
                throw new InvalidOperationException("Abbruch mitten im Schreiben");
            }));

        Assert.Equal("Abbruch mitten im Schreiben", error.Message);
        Assert.Equal("{ \"alt\": true }", File.ReadAllText(target, Encoding.UTF8));
    }

    [Fact]
    public void Ein_Abbruch_laesst_keine_Zwischendatei_zurueck()
    {
        using TempDirectory temp = new();
        string target = temp.File("config.json");
        AtomicFile.WriteText(target, "alt");

        _ = Assert.Throws<InvalidOperationException>(() =>
            AtomicFile.Write(target, _ => throw new InvalidOperationException("Abbruch")));

        string[] leftovers = Directory.GetFiles(temp.Path);
        string[] expected = [target];
        Assert.Equal(expected, leftovers);
    }

    [Fact]
    public void Neuer_Stand_ersetzt_den_alten_vollstaendig()
    {
        using TempDirectory temp = new();
        string target = temp.File("config.json");

        AtomicFile.WriteText(target, "ein sehr langer alter Inhalt mit vielen Zeichen");
        AtomicFile.WriteText(target, "kurz");

        // Kein Rest des laengeren Vorgaengers - der Nachweis, dass nicht in die bestehende
        // Datei hineingeschrieben wurde.
        Assert.Equal("kurz", File.ReadAllText(target, Encoding.UTF8));
    }

    [Fact]
    public void Schreiben_legt_fehlende_Verzeichnisse_an()
    {
        using TempDirectory temp = new();
        string target = Path.Combine(temp.Path, "ProNet Systems", "TanssLogWatcher", "config.json");

        AtomicFile.WriteText(target, "inhalt");

        Assert.True(File.Exists(target));
    }

    [Fact]
    public void Sicherung_entsteht_nur_wenn_es_etwas_zu_sichern_gibt()
    {
        using TempDirectory temp = new();
        string target = temp.File("credentials.dat");
        string backup = target + ".bak";

        Assert.False(AtomicFile.Backup(target, backup));
        Assert.False(File.Exists(backup));

        AtomicFile.WriteBytes(target, [1, 2, 3]);
        Assert.True(AtomicFile.Backup(target, backup));
        Assert.Equal<byte>([1, 2, 3], File.ReadAllBytes(backup));
    }

    [Fact]
    public void UTF8_ohne_Byte_Order_Mark()
    {
        using TempDirectory temp = new();
        string target = temp.File("config.json");

        AtomicFile.WriteText(target, "Fernwartung für Größenänderung");

        byte[] bytes = File.ReadAllBytes(target);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("Fernwartung für Größenänderung", File.ReadAllText(target, Encoding.UTF8));
    }
}
