using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Http;

namespace TanssLogWatcher.Api.Auth;

/// <summary>Anmeldung, Token prägen, Token erneuern.</summary>
/// <remarks>
/// <para>Das Arbeitstoken kommt aus <c>GET /api/v1/jwts/tanss_app</c>. Der Programmname ist
/// fest vorgegeben; <c>tanss_x</c> gibt es nicht, obwohl die Integrationsrouten so heißen.</para>
/// <para>Diese Klasse prüft <b>keine Signatur</b>. Das ist Absicht: die Signatur prüft der
/// Server, und der Schlüssel dazu liegt dort. Alles, was hier aus dem Token gelesen wird, dient
/// der Betriebsführung (Wann läuft es ab? Lohnt eine Erneuerung?) und niemals einer
/// Zugriffsentscheidung.</para>
/// </remarks>
public static class TanssAuth
{
    /// <summary>Ein Tag in Millisekunden — die Einheit, die <c>duration</c> erwartet.</summary>
    private const long MillisecondsPerDay = 86_400_000L;

    /// <summary>
    /// Liest den Nutzteil eines JWT, <b>ohne die Signatur zu prüfen</b>.
    /// </summary>
    /// <param name="token">Das Token, mit oder ohne führendes <c>Bearer </c>.</param>
    /// <exception cref="TanssAuthException">
    /// Der Text ist kein JWT. Der Aufrufer muss dann ein neues Token einrichten lassen — ein
    /// beschädigter Tokenspeicher heilt nicht von selbst.
    /// </exception>
    public static TanssTokenClaims DecodeClaims(string? token)
    {
        string raw = StripBearer(token);
        if (raw.Length == 0)
        {
            throw new TanssAuthException(
                "Es ist gar kein Token hinterlegt. Ohne Token kann dieses Werkzeug nichts nach "
                + "TANSS schreiben; es muss einmalig eingerichtet werden.");
        }

        string[] parts = raw.Split('.');
        if (parts.Length < 2 || parts[1].Length == 0)
        {
            throw new TanssAuthException(
                "Der hinterlegte Wert ist kein JWT: erwartet werden drei durch Punkt getrennte "
                + "Abschnitte. Häufigste Ursache ist ein beim Kopieren abgeschnittener oder um "
                + "Zeilenumbrüche ergänzter Text.");
        }

        try
        {
            byte[] payload = DecodeBase64Url(parts[1]);
            using JsonDocument document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new TanssAuthException("Der Nutzteil des Tokens ist kein JSON-Objekt.");
            }

            Dictionary<string, JsonElement> claims = new(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                claims[property.Name] = property.Value.Clone();
            }

            return new TanssTokenClaims(claims);
        }
        catch (Exception ex) when (ex is FormatException or JsonException or DecoderFallbackException)
        {
            throw new TanssAuthException(
                "Der Nutzteil des Tokens lässt sich nicht lesen: " + Redaction.Scrub(ex.Message));
        }
    }

    /// <summary>Wann läuft das Token ab? <c>null</c>, wenn es keinen <c>exp</c>-Anspruch trägt.</summary>
    public static DateTimeOffset? ExpiresAt(string? token) => DecodeClaims(token).ExpiresAt;

    /// <summary>Wie viele Tage bleiben? Negativ, wenn das Token bereits abgelaufen ist.</summary>
    public static double DaysRemaining(string? token, DateTimeOffset? now = null) =>
        DecodeClaims(token).DaysRemaining(now ?? DateTimeOffset.UtcNow);

    /// <summary>
    /// Prägt ein neues Arbeitstoken.
    /// </summary>
    /// <param name="client">Der Zugang; er hängt <c>loggedInUserId</c> selbst an.</param>
    /// <param name="durationDays">Laufzeit in Tagen. TANSS erwartet Millisekunden, die Umrechnung
    /// steht hier, damit sie nur an einer Stelle falsch sein kann.</param>
    /// <param name="info">Freitext, den TANSS neben dem Token protokolliert.</param>
    /// <param name="forTesting">
    /// <c>true</c> macht daraus einen Rechte-Trockentest.
    /// <para><b>ACHTUNG — nachgemessen gegen eine Instanz der Version 10.10.0, und anders als
    /// hier früher behauptet:</b> Der Schalter macht das Token <b>nicht</b> unbrauchbar und
    /// verkürzt seine Laufzeit <b>nicht</b>. Bei gleicher <c>duration</c> liefert TANSS mit
    /// <c>true</c> und mit <c>false</c> ein Token mit identischem <c>exp</c>, und beide werden
    /// auf jeder Route angenommen.</para>
    /// <para>Kurzlebig wird der Trockentest allein dadurch, dass <b>diese Methode</b> in diesem
    /// Fall 60 Sekunden anfragt — siehe die Berechnung von <c>milliseconds</c> unten. Die
    /// Harmlosigkeit hängt also an unserer eigenen Zeile und an keiner Zusage des Servers. Wer
    /// sie ändert, stellt ab dann bei jedem Trockentest langlebige Token aus, die TANSS 10.10.0
    /// nicht widerrufen kann.</para>
    /// <para>Ob TANSS den Vorgang protokolliert, ist von aussen nicht feststellbar; das Feld
    /// <c>info</c> kommt im Token zurück und wird demnach gespeichert. Es ist deshalb nicht
    /// anzunehmen, dass ein Trockentest spurlos bleibt.</para>
    /// </param>
    /// <param name="ct">Abbruchmarke.</param>
    /// <returns>Das Token einschließlich des Präfixes <c>Bearer </c>.</returns>
    /// <exception cref="TanssAuthException">
    /// Dem Mitarbeiter fehlt das Recht, Token zu prägen (<c>NOT_ALLOWED_TO_CREATE_JWTS</c>).
    /// Das ist ein Recht in TANSS und muss dort vergeben werden.
    /// </exception>
    public static async Task<string> MintAsync(ITanssClient client, int durationDays = 365,
                                               string info = "TANSS Log-Watcher",
                                               bool forTesting = false,
                                               CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(durationDays);

        // DIESE ZEILE IST DIE GANZE HARMLOSIGKEIT DES TROCKENTESTS. Nachgemessen: TANSS gibt bei
        // isForTesting=true dasselbe Token wie bei false und haelt sich an die angefragte
        // duration. Kurzlebig wird das Probetoken allein dadurch, dass hier 60 Sekunden stehen.
        // Wer die 60_000 vergroessert, stellt ab dann bei jedem "folgenlosen" Rechtetest ein
        // langlebiges Token aus, das sich nicht widerrufen laesst.
        long milliseconds = forTesting ? 60_000L : durationDays * MillisecondsPerDay;
        Dictionary<string, string?> query = new(StringComparer.Ordinal)
        {
            ["duration"] = milliseconds.ToString(CultureInfo.InvariantCulture),
            ["info"] = info,
            ["isForTesting"] = forTesting ? "true" : "false",
        };

        // Das Verb ist GET, der Vorgang ist es nicht: TANSS stellt bei jedem Aufruf ein neues
        // JWT aus und traegt es in sein Tokenprotokoll ein. Eine automatische Wiederholung
        // nach einer Zeitueberschreitung praegte also ein zweites Token, von dem hier niemand
        // mehr erfaehrt - und TANSS 10.10.0 kann ein ausgestelltes Token nicht widerrufen.
        // Deshalb laeuft diese Route ueber TanssRoutes.HasSideEffectOnGet mit genau einem
        // Versuch; scheitert sie, entscheidet ein Mensch ueber die Wiederholung.
        MintedToken? minted = await client.GetAsync<MintedToken>(TanssRoutes.MintToken, query, ct)
            .ConfigureAwait(false);

        if (minted is null || string.IsNullOrWhiteSpace(minted.ApiToken))
        {
            throw new TanssAuthException(
                "TANSS hat das Prägen mit Erfolg quittiert, aber kein Token geliefert. Das "
                + "bisherige Token bleibt in Kraft; der Vorgang ist zu wiederholen.");
        }

        return minted.ApiToken;
    }

    /// <summary>
    /// Darf dieser Mitarbeiter überhaupt Token prägen?
    /// </summary>
    /// <remarks>
    /// Fragt mit <c>isForTesting=true</c> und 60 Sekunden Laufzeit. TANSS protokolliert diesen
    /// Versuch nicht und gibt ein unbrauchbares Token zurück — er kostet also nichts und eignet
    /// sich für die Einrichtung. Wirft nicht: die Antwort auf „darf ich?“ ist ja oder nein.
    /// </remarks>
    public static async Task<bool?> CanRotateAsync(ITanssClient client, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);

        try
        {
            string probe = await MintAsync(client, durationDays: 1, info: "Rechte-Trockentest",
                                           forTesting: true, ct).ConfigureAwait(false);
            return probe.Length > 0;
        }
        catch (TanssAuthException)
        {
            // Abgewiesen — DAS ist die Antwort „darf nicht“. Nur dieser eine Fall.
            return false;
        }
        catch (TanssException)
        {
            // Alles andere heisst „konnte nicht gefragt werden“: Instanz nicht erreichbar,
            // Adresse falsch, Modul nicht lizenziert, Token abgelaufen. Das auf false zu
            // druecken machte aus einem Netzproblem die Aussage, dem Mitarbeiter fehle das
            // Recht 480 — und schickte den Techniker zum TANSS-Administrator statt ans Netz.
            return null;
        }
    }

    /// <summary>
    /// Erneuert das Token, wenn es bald abläuft — und übernimmt es erst nach bestandener Probe.
    /// </summary>
    /// <remarks>
    /// <para><b>Das neue Token wird gegengeprüft, bevor es das alte ersetzt.</b> Solange das
    /// alte Token gültig ist, ist der schlechtestmögliche Ausgang einer misslungenen Erneuerung,
    /// dass alles bleibt, wie es war. Würde erst geschrieben und dann geprüft, stünde das
    /// Werkzeug bei einem fehlerhaften neuen Token ohne jeden Zugang da — und das womöglich
    /// mitten in einer laufenden Fernwartung.</para>
    /// <para>Geprüft wird mit einem echten, aber harmlosen Aufruf, den der Aufrufer mitbringt:
    /// diese Schicht kennt keine Route, die sich dafür besonders eignet.</para>
    /// </remarks>
    /// <param name="client">Zugang mit dem <b>bisherigen</b> Token.</param>
    /// <param name="store">Der Tokenspeicher; er sichert das alte Token vor dem Überschreiben.</param>
    /// <param name="verify">
    /// Probe auf das neue Token: bekommt das frisch geprägte Token und meldet, ob damit ein
    /// Aufruf gelingt.
    /// </param>
    /// <param name="beforeDays">Ab wie vielen Resttagen erneuert wird.</param>
    /// <param name="durationDays">Laufzeit des neuen Tokens.</param>
    /// <param name="info">Freitext für das TANSS-Protokoll.</param>
    /// <param name="now">Bezugszeitpunkt; für Tests einsetzbar.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static async Task<TokenRotationResult> RotateIfNeededAsync(
        ITanssClient client, ITokenStore store,
        Func<string, CancellationToken, Task<bool>> verify,
        int beforeDays = 60, int durationDays = 365, string info = "TANSS Log-Watcher",
        DateTimeOffset? now = null, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(verify);

        DateTimeOffset moment = now ?? DateTimeOffset.UtcNow;

        DateTimeOffset? oldExpiry;
        double remaining;
        try
        {
            TanssTokenClaims claims = DecodeClaims(store.Read());
            oldExpiry = claims.ExpiresAt;
            remaining = claims.DaysRemaining(moment);
        }
        catch (TanssAuthException ex)
        {
            return new TokenRotationResult(false,
                "Das hinterlegte Token ist nicht lesbar; eine Erneuerung würde das Werkzeug "
                + "vollends lahmlegen. Es muss von Hand neu eingerichtet werden.",
                null, null, Redaction.Scrub(ex.Message));
        }

        if (oldExpiry is null)
        {
            return new TokenRotationResult(false,
                "Das Token trägt keinen Ablauf; es gibt nichts zu erneuern.", null, null, null);
        }

        if (remaining > beforeDays)
        {
            string reason = string.Create(CultureInfo.InvariantCulture,
                $"Noch {remaining:0} Tage gültig, erneuert wird ab {beforeDays} Resttagen.");
            return new TokenRotationResult(false, reason, oldExpiry, null, null);
        }

        string minted;
        try
        {
            minted = await MintAsync(client, durationDays, info, forTesting: false, ct)
                .ConfigureAwait(false);
        }
        catch (TanssException ex)
        {
            return new TokenRotationResult(false,
                "Das Prägen ist fehlgeschlagen; das bisherige Token bleibt in Kraft.",
                oldExpiry, null, Redaction.Scrub(ex.Message));
        }

        DateTimeOffset? newExpiry;
        try
        {
            newExpiry = DecodeClaims(minted).ExpiresAt;
        }
        catch (TanssAuthException ex)
        {
            return new TokenRotationResult(false,
                "TANSS hat etwas geliefert, das kein JWT ist; das bisherige Token bleibt in Kraft.",
                oldExpiry, null, Redaction.Scrub(ex.Message));
        }

        bool proven;
        try
        {
            proven = await verify(minted, ct).ConfigureAwait(false);
        }
        catch (TanssException ex)
        {
            return new TokenRotationResult(false,
                "Die Probe auf das neue Token ist fehlgeschlagen; das bisherige bleibt in Kraft "
                + "und ist ja noch gültig.", oldExpiry, newExpiry, Redaction.Scrub(ex.Message));
        }

        if (!proven)
        {
            return new TokenRotationResult(false,
                "Das neue Token hat die Probe nicht bestanden; das bisherige bleibt in Kraft "
                + "und ist ja noch gültig.", oldExpiry, newExpiry, null);
        }

        // Erst jetzt, nach bestandener Probe, wird das alte Token abgeloest.
        store.Write(minted);

        return new TokenRotationResult(true,
            "Token erneuert und geprüft; das bisherige wurde vom Speicher gesichert.",
            oldExpiry, newExpiry, null);
    }

    /// <summary>
    /// Meldet sich mit Benutzername und Kennwort an — <b>ohne</b> Token im Kopf.
    /// </summary>
    /// <remarks>
    /// Der einzige Aufruf dieses Werkzeugs, der ohne Token auskommt, und deshalb der einzige,
    /// der nicht über <see cref="TanssClient"/> läuft. Das Ergebnis (<c>apiKey</c>) ist ein
    /// Sitzungsschlüssel von kurzer Laufzeit; für den Dauerbetrieb wird daraus über
    /// <see cref="MintAsync"/> ein langlaufendes Token geprägt.
    /// </remarks>
    /// <param name="baseUrl">Basisadresse einschließlich <c>/backend</c>.</param>
    /// <param name="username">Anmeldename in TANSS.</param>
    /// <param name="password">Kennwort.</param>
    /// <param name="twoFactorToken">Zweiter Faktor, falls die Instanz ihn verlangt.</param>
    /// <param name="handler">
    /// Verbindungsschicht; ohne Angabe eine eigene. Wird nicht freigegeben, wenn sie von außen
    /// kommt.
    /// </param>
    /// <param name="timeout">Zeitgrenze für die Anmeldung.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static async Task<LoginResult> LoginAsync(string baseUrl, string username, string password,
                                                     string? twoFactorToken = null,
                                                     HttpMessageHandler? handler = null,
                                                     TimeSpan? timeout = null,
                                                     CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(baseUrl);
        ArgumentException.ThrowIfNullOrWhiteSpace(username);

        using HttpClient http = handler is null
            ? new HttpClient()
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = timeout ?? TimeSpan.FromSeconds(30);

        Uri address = new(baseUrl.TrimEnd('/') + TanssRoutes.Login, UriKind.Absolute);
        LoginRequest body = new(username, password, twoFactorToken ?? string.Empty);

        using StringContent content = new(TanssJson.Serialize(body), Encoding.UTF8);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };

        HttpResponseMessage response;
        try
        {
            response = await http.PostAsync(address, content, ct).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new TanssUnreachableException(
                "Die Anmeldung hat TANSS nicht erreicht. Zu prüfen sind Basisadresse (sie muss "
                + "auf /backend enden), Netzverbindung und Zertifikat. " + Redaction.Scrub(ex.Message),
                ex);
        }
        catch (TaskCanceledException ex) when (!ct.IsCancellationRequested)
        {
            throw new TanssUnreachableException(
                "Die Anmeldung lief in die Zeitgrenze, ohne dass TANSS geantwortet hat.", ex);
        }

        using (response)
        {
            string payload = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

            // Auch hier: erst der Status, dann der Rumpf.
            if (!response.IsSuccessStatusCode)
            {
                throw TanssEnvelope.ToException((int)response.StatusCode, payload, "POST",
                                                TanssRoutes.Login);
            }

            (LoginResult? result, _) = TanssEnvelope.Unpack<LoginResult>(payload);
            return result ?? throw new TanssAuthException(
                "TANSS hat die Anmeldung angenommen, aber keine Sitzungsdaten geliefert.");
        }
    }

    /// <summary>Entfernt ein führendes <c>Bearer </c> und umgebenden Leerraum.</summary>
    public static string StripBearer(string? token)
    {
        string raw = (token ?? string.Empty).Trim();
        return raw.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
            ? raw["Bearer ".Length..].Trim()
            : raw;
    }

    /// <summary>
    /// Base64url mit nachträglich ergänzter Auffüllung.
    /// </summary>
    /// <remarks>
    /// JWT verwendet Base64url ohne <c>=</c> am Ende und mit <c>-_</c> statt <c>+/</c>.
    /// <see cref="Convert.FromBase64String"/> kennt beides nicht und scheitert ausgerechnet an
    /// den Token, deren Nutzteil nicht zufällig auf vier Zeichen aufgeht.
    /// </remarks>
    private static byte[] DecodeBase64Url(string value)
    {
        string padded = value.Replace('-', '+').Replace('_', '/');
        padded = (padded.Length % 4) switch
        {
            2 => padded + "==",
            3 => padded + "=",
            0 => padded,
            _ => throw new FormatException("Der Abschnitt hat keine gültige Base64url-Länge."),
        };

        return Convert.FromBase64String(padded);
    }
}
