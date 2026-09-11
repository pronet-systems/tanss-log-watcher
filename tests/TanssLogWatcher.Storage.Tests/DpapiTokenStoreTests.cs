using System.Text;
using TanssLogWatcher.Storage.Secrets;
using Xunit;

namespace TanssLogWatcher.Storage.Tests;

public sealed class DpapiTokenStoreTests
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxIiwiZXhwIjoxOTAwMDAwMDAwfQ.KiZ_Pd7w-AbC123";

    [Fact]
    public void Rundlauf_liefert_das_Token_mit_Praefix()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));

        Assert.False(store.Exists());
        store.Write("Bearer " + Jwt);

        Assert.True(store.Exists());
        Assert.Equal("Bearer " + Jwt, store.Read());
    }

    [Fact]
    public void Ein_nacktes_Token_bekommt_das_Praefix()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));

        // /api/v1/login liefert unter apiKey den nackten Wert. Ohne Praefix antwortet
        // TANSS auf jede Anfrage mit 403 - ununterscheidbar von einem abgelaufenen Token.
        store.Write(Jwt);

        Assert.Equal("Bearer " + Jwt, store.Read());
    }

    [Fact]
    public void Auf_der_Platte_steht_nichts_Lesbares()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));
        store.Write(Jwt);

        byte[] raw = File.ReadAllBytes(store.Path);

        Assert.DoesNotContain(Jwt, Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
        Assert.DoesNotContain("Bearer", Encoding.UTF8.GetString(raw), StringComparison.Ordinal);
    }

    [Fact]
    public void Vor_dem_Wechsel_entsteht_eine_Sicherung()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));

        store.Write("Bearer " + Jwt);
        Assert.False(File.Exists(store.BackupPath));

        store.Write("Bearer neues-token");

        Assert.True(File.Exists(store.BackupPath));
        Assert.Equal("Bearer neues-token", store.Read());
        Assert.Equal("Bearer " + Jwt, store.ReadBackup());

        // Der Zweck der Sicherung: Ein misslungener Wechsel darf nicht bedeuten, dass gar
        // kein Token mehr da ist.
        Assert.True(store.RestoreFromBackup());
        Assert.Equal("Bearer " + Jwt, store.Read());
    }

    [Fact]
    public void Lesen_ohne_hinterlegtes_Token_nennt_den_Einrichtungsbefehl()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));

        TokenStoreException error = Assert.Throws<TokenStoreException>(() => store.Read());

        Assert.Contains("noch nicht eingerichtet", error.Message, StringComparison.Ordinal);
        Assert.Contains("tanss-logwatch setup", error.Message, StringComparison.Ordinal);
        Assert.Contains(store.Path, error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unentschluesselbare_Datei_erklaert_den_Benutzerwechsel()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));
        File.WriteAllBytes(store.Path, [7, 7, 7, 7, 7, 7, 7, 7]);

        TokenStoreException error = Assert.Throws<TokenStoreException>(() => store.Read());

        Assert.Contains("anderen Benutzer", error.Message, StringComparison.Ordinal);
        Assert.Contains("erneuter Versuch", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Leere_Datei_ist_kein_leeres_Token()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));
        File.WriteAllBytes(store.Path, []);

        TokenStoreException error = Assert.Throws<TokenStoreException>(() => store.Read());
        Assert.Contains("ist leer", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Fehlende_Sicherung_wird_als_solche_gemeldet()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));

        Assert.False(store.RestoreFromBackup());
        TokenStoreException error = Assert.Throws<TokenStoreException>(() => store.ReadBackup());
        Assert.Contains("keine Sicherung", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Abmelden_raeumt_Token_und_Sicherung_fort()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));

        store.Write(Jwt);
        store.Write("Bearer zweites");
        store.Clear();

        Assert.False(File.Exists(store.Path));
        Assert.False(File.Exists(store.BackupPath));
    }

    [Theory]
    [InlineData("abc", "Bearer abc")]
    [InlineData("  abc  ", "Bearer abc")]
    [InlineData("Bearer abc", "Bearer abc")]
    [InlineData("bearer  abc ", "Bearer abc")]
    public void Praefix_wird_genau_einmal_gesetzt(string input, string expected) =>
        Assert.Equal(expected, DpapiTokenStore.Normalize(input));

    [Fact]
    public void Leeres_Token_wird_gar_nicht_erst_geschrieben()
    {
        using TempDirectory temp = new();
        DpapiTokenStore store = new(temp.File("credentials.dat"));

        _ = Assert.Throws<ArgumentException>(() => store.Write("   "));
        Assert.False(File.Exists(store.Path));
    }
}
