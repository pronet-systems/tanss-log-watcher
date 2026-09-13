using TanssLogWatcher.Api.Diagnostics;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>Prüft, dass kein Geheimnis in ein Protokoll gelangt.</summary>
public sealed class RedactionTests
{
    private const string Jwt =
        "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiI0MiIsImV4cCI6MTgwMDAwMDAwMH0.Zm9vYmFy";

    [Fact]
    public void Ein_Jwt_Im_Fliesstext_Wird_Geschwaerzt()
    {
        string text = Redaction.Scrub($"Anfrage mit Token {Jwt} abgewiesen.");

        Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
        Assert.Contains(Redaction.Mask, text, StringComparison.Ordinal);
        Assert.Contains("abgewiesen", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Ein_Bearer_Wert_Wird_Geschwaerzt()
    {
        string text = Redaction.Scrub("apiToken: Bearer " + Jwt);

        Assert.DoesNotContain(Jwt, text, StringComparison.Ordinal);
        Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Auch_Ein_Bearer_Ohne_Jwt_Verschwindet()
    {
        string text = Redaction.Scrub("Authorization: Bearer abc123geheim");

        Assert.DoesNotContain("abc123geheim", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"apiToken":"geheim1"}""", "geheim1")]
    [InlineData("""{"refreshToken":"geheim2"}""", "geheim2")]
    [InlineData("""{"apiKey":"geheim3"}""", "geheim3")]
    [InlineData("apiToken=geheim4&x=1", "geheim4")]
    [InlineData("Authorization: geheim5", "geheim5")]
    public void Geheime_Felder_Verlieren_Ihren_Wert(string text, string secret)
    {
        string scrubbed = Redaction.Scrub(text);

        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains(Redaction.Mask, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Der_Feldname_Bleibt_Stehen()
    {
        // Ohne den Namen waere die Protokollzeile zum Suchen der Ursache wertlos.
        string text = Redaction.Scrub("""{"apiToken":"geheim","employeeId":42}""");

        Assert.Contains("apiToken", text, StringComparison.Ordinal);
        Assert.Contains("employeeId", text, StringComparison.Ordinal);
        Assert.Contains("42", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Harmloser_Text_Bleibt_Unangetastet()
    {
        const string text = "Fernwartung 38584 angelegt, Typ 1003, Mitarbeiter 42.";

        Assert.Equal(text, Redaction.Scrub(text));
    }

    [Fact]
    public void Null_Und_Leer_Ergeben_Eine_Leere_Zeichenkette()
    {
        Assert.Equal(string.Empty, Redaction.Scrub((string?)null));
        Assert.Equal(string.Empty, Redaction.Scrub(string.Empty));
        Assert.Equal(string.Empty, Redaction.Scrub((Exception?)null));
    }

    [Fact]
    public void Eine_Ausnahme_Wird_Ueber_Ihre_Meldung_Geschwaerzt()
    {
        string text = Redaction.Scrub(new InvalidOperationException("Token " + Jwt + " abgelehnt"));

        Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
        Assert.Contains("abgelehnt", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Mehrere_Geheimnisse_In_Einer_Zeile_Verschwinden_Alle()
    {
        string text = Redaction.Scrub(
            $$"""{"apiToken":"Bearer {{Jwt}}","refreshToken":"r-123","note":"ok"}""");

        Assert.DoesNotContain("eyJ", text, StringComparison.Ordinal);
        Assert.DoesNotContain("r-123", text, StringComparison.Ordinal);
        Assert.Contains("ok", text, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"username":"sebastian","password":"kennwort","token":"123456"}""", "kennwort")]
    [InlineData("""{"username":"sebastian","password":"kennwort","token":"123456"}""", "123456")]
    [InlineData("password=kennwort&x=1", "kennwort")]
    public void Der_Anmelderumpf_Verliert_Kennwort_Und_Zweiten_Faktor(string text, string secret)
    {
        // POST /api/v1/login traegt beides im Klartext. Geraet der Rumpf je in eine
        // Fehlermeldung, muss die Schwaerzung greifen - Hausregel 4 kennt keine Ausnahme.
        string scrubbed = Redaction.Scrub(text);

        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains(Redaction.Mask, scrubbed, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Der Token: abgelaufen, bitte erneuern.")]
    [InlineData("Password: falsch eingegeben")]
    [InlineData("Fehler beim token=refresh Vorgang")]
    [InlineData("Authorization: fehlt")]
    [InlineData("Der apiToken: unbekannt")]
    public void Deutsche_Prosa_Ueberlebt_Die_Schwaerzung(string text)
    {
        // Diese Version laeuft ueber das Aenderungsprotokoll - also genau ueber den Text, der
        // spaeter die Frage "warum ist das schiefgegangen" beantworten soll. Waere "abgelaufen"
        // geschwaerzt, bliebe von der Begruendung nichts uebrig.
        Assert.Equal(text, Redaction.Scrub(text));
    }

    [Theory]
    [InlineData("token: abc123def456", "abc123def456")]
    [InlineData("apiToken: s3cr3t-value", "s3cr3t-value")]
    [InlineData("password: A1b2C3d4", "A1b2C3d4")]
    [InlineData("token=aWNoLWJpbi1laW4tZ2VoZWltbmlz", "aWNoLWJpbi1laW4tZ2VoZWltbmlz")]
    [InlineData("Fehler bei token: abcdefghijklmnopqrstuvwxyz", "abcdefghijklmnopqrstuvwxyz")]
    public void Ein_Tokenartiger_Wert_Verschwindet_Auch_Ohne_Anfuehrungszeichen(string text,
                                                                                string secret)
    {
        // Ziffern, Sonderzeichen oder schiere Laenge: drei Merkmale, die ein Geheimnis hat und
        // ein deutsches Wort nicht. Im Zweifel wird geschwaerzt.
        string scrubbed = Redaction.Scrub(text);

        Assert.DoesNotContain(secret, scrubbed, StringComparison.Ordinal);
        Assert.Contains(Redaction.Mask, scrubbed, StringComparison.Ordinal);
    }

    [Fact]
    public void Das_Satzzeichen_Hinter_Einem_Geheimnis_Bleibt_Stehen()
    {
        string text = Redaction.Scrub("Abgewiesen mit token: abc123def.");

        Assert.Equal("Abgewiesen mit token: " + Redaction.Mask + ".", text);
    }

    [Fact]
    public void Ein_Gewoehnliches_Wort_In_Einer_Abfragezeichenkette_Wird_Geschwaerzt()
    {
        // Ein & dahinter heisst: der Wert steht in einer Abfragezeichenkette, und die traegt
        // Felder, keine Saetze.
        string text = Redaction.Scrub("password=kennwort&next=1");

        Assert.DoesNotContain("kennwort", text, StringComparison.Ordinal);
        Assert.Contains("next=1", text, StringComparison.Ordinal);
    }

    [Fact]
    public void Zweimal_Schwaerzen_Aendert_Nichts_Mehr()
    {
        string once = Redaction.Scrub("""{"apiToken":"geheim","note":"Sitzung 1003"}""");

        Assert.Equal(once, Redaction.Scrub(once));
    }

    [Fact]
    public void Die_Namen_ApiToken_Und_RefreshToken_Bleiben_Lesbar()
    {
        // Das neue Muster "token" darf die laengeren Namen nicht anknabbern, sonst waere die
        // Protokollzeile zum Suchen der Ursache wertlos.
        string text = Redaction.Scrub("""{"apiToken":"a1","refreshToken":"a2","token":"a3"}""");

        Assert.Contains("apiToken", text, StringComparison.Ordinal);
        Assert.Contains("refreshToken", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a1", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a2", text, StringComparison.Ordinal);
        Assert.DoesNotContain("a3", text, StringComparison.Ordinal);
    }
}
