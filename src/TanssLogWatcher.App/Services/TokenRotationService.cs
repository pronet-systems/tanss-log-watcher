using System.Globalization;
using System.Runtime.Versioning;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Repository;
using TanssLogWatcher.App.Runtime;
using TanssLogWatcher.Storage.Logging;

namespace TanssLogWatcher.App.Services;

/// <summary>
/// Sieht einmal täglich nach dem Arbeitstoken und erneuert es, bevor es abläuft.
/// </summary>
/// <remarks>
/// <para><b>Geprägt wird über <c>GET /api/v1/jwts/tanss_app</c> — ein Lesevorgang ist das
/// nicht.</b> TANSS stellt bei jedem Aufruf ein neues JWT aus und trägt es in sein
/// Tokenprotokoll ein; widerrufen lässt sich in 10.10.0 keines. Deshalb prägt dieser Dienst
/// nur, wenn <c>tanss.rotate_before_days</c> es verlangt, und deshalb wiederholt er einen
/// misslungenen Versuch nicht innerhalb desselben Tages.</para>
///
/// <para><b>Übernommen wird erst nach bestandener Probe.</b> Solange das alte Token gültig ist,
/// ist der schlechteste Ausgang eines misslungenen Wechsels, dass alles bleibt, wie es war.
/// Umgekehrt — erst schreiben, dann prüfen — stünde das Werkzeug bei einem fehlerhaften neuen
/// Token ohne jeden Zugang da, womöglich mitten in einer Fernwartung.</para>
///
/// <para><b>Ein fehlendes Token beendet nichts.</b> Es ist ein Befund wie jeder andere: Der
/// Zustand wird gestört, die Beobachtung läuft weiter, und die Warteschlange hält die
/// Sitzungen, bis wieder ein Token da ist.</para>
/// </remarks>
[SupportedOSPlatform("windows")]
public sealed class TokenRotationService : PeriodicService
{
    /// <summary>Der Freitext, den TANSS neben dem Token protokolliert.</summary>
    private const string MintInfo = "TANSS Log-Watcher";

    /// <summary>Laufzeit eines frisch geprägten Tokens in Tagen.</summary>
    private const int DurationDays = 365;

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);

    /// <summary>Baut den Dienst.</summary>
    /// <param name="context">Der Zugang zu Zustand und Zusammenbau.</param>
    public TokenRotationService(IRuntimeContext context) : base(context)
    {
    }

    /// <summary>Der Stand des Tokens hat sich geändert.</summary>
    public event EventHandler<TokenSnapshot>? TokenChanged;

    /// <summary>Ein Wechsel ist versucht worden — geglückt oder nicht.</summary>
    public event EventHandler<TokenRotationResult>? Rotated;

    /// <inheritdoc />
    public override string Name => "Tokenerneuerung";

    /// <inheritdoc />
    public override string Description =>
        "Prüft einmal täglich die Restlaufzeit des Arbeitstokens und erneuert es ab "
        + "tanss.rotate_before_days — mit echtem Gegentest, bevor das bisherige abgelöst wird.";

    /// <summary>Der zuletzt erhobene Stand des Tokens.</summary>
    public TokenSnapshot Token { get; private set; } = new();

    /// <inheritdoc />
    /// <remarks>
    /// Einmal täglich, wie vorgesehen. Häufiger wäre sinnlos: Die Fälligkeit bemisst sich in
    /// Tagen, und jeder Prägeversuch hinterlässt in TANSS ein Token, das niemand mehr
    /// zurücknehmen kann.
    /// </remarks>
    protected override TimeSpan Interval => TimeSpan.FromHours(24);

    /// <inheritdoc />
    protected override string NotConfiguredMessage =>
        "Ruht: Ohne Konfiguration ist keine Instanz bekannt, bei der ein Token zu prägen wäre.";

    /// <inheritdoc />
    protected override string FaultMessage =>
        "Die letzte Prüfung des Tokens ist abgebrochen. Das bisherige Token bleibt unangetastet "
        + "in Kraft; nichts wurde überschrieben. Beim nächsten Lauf wird erneut geprüft.";

    /// <summary>
    /// Erneuert sofort und ohne Rücksicht auf die Fälligkeit.
    /// </summary>
    /// <remarks>
    /// Für die Schaltfläche „Token jetzt erneuern“. Wer sie drückt, will wechseln und nicht
    /// hören, dass es noch nicht fällig sei — ob es fällig wäre, sagt <see cref="Token"/>.
    /// <para><b>Jeder Aufruf prägt ein Token, auch ein misslungener Wechsel.</b> Entsteht beim
    /// Prägen eines und scheitert danach die Probe, bleibt es in TANSS stehen, ohne benutzt zu
    /// werden; ein Widerruf ist in 10.10.0 nicht vorgesehen. Ein zweiter Versuch lohnt deshalb
    /// nur, wenn die Ursache behoben ist.</para>
    /// </remarks>
    /// <param name="ct">Abbruchmarke.</param>
    public Task<TokenRotationResult> RotateNowAsync(CancellationToken ct = default) =>
        RotateAsync(forced: true, ct);

    /// <summary>
    /// Liest den Stand des Tokens aus dem lokalen Profil, ohne Netzaufruf.
    /// </summary>
    /// <remarks>
    /// Gelesen, nicht geprüft: Die Signatur prüft TANSS. Hier wird allein die Betriebsfrage
    /// beantwortet — ist etwas da, lässt es sich unter diesem Windows-Konto entsiegeln, und wie
    /// lange reicht es noch?
    /// </remarks>
    public TokenSnapshot RefreshToken()
    {
        if (Context.Composition is not { } composition)
        {
            return Publish(new TokenSnapshot
            {
                Problem = "Ohne Konfiguration ist nicht bekannt, welches Token gemeint wäre.",
            });
        }

        int beforeDays = composition.Config.Tanss.RotateBeforeDays;

        string raw;
        try
        {
            raw = composition.Tokens.Read();
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return Publish(new TokenSnapshot
            {
                RotateBeforeDays = beforeDays,
                Problem =
                    "Das Token liess sich nicht aus dem lokalen Profil lesen. Üblichste Ursache: "
                    + "ein anderes Windows-Konto als bei der Einrichtung — die Ablage ist mit "
                    + "DPAPI an Benutzer und Rechner gebunden und wandert nicht mit. Einzurichten "
                    + "ist ein neues Token mit „tanss-logwatch token rotate“. Meldung: "
                    + Redaction.Scrub(ex.Message),
            });
        }

        TanssTokenClaims claims;
        try
        {
            claims = TanssAuth.DecodeClaims(raw);
        }
        catch (TanssAuthException ex)
        {
            return Publish(new TokenSnapshot
            {
                RotateBeforeDays = beforeDays,
                Problem =
                    "Der hinterlegte Wert ist kein lesbares JWT. Ein beschädigter Tokenspeicher "
                    + "heilt nicht von selbst und ist neu einzurichten. Meldung: "
                    + Redaction.Scrub(ex.Message),
            });
        }

        DateTimeOffset now = Context.Clock.GetLocalNow();

        return Publish(new TokenSnapshot
        {
            IsReadable = true,
            ExpiresAt = claims.ExpiresAt,
            IssuedAt = claims.IssuedAt,
            DaysRemaining = claims.DaysRemaining(now),
            RotateBeforeDays = beforeDays,
            CanRotate = Token.CanRotate,
            Problem = claims.ExpiresAt is null
                ? "Das Token trägt keinen exp-Anspruch. Wann es abläuft, ist von hier aus nicht "
                  + "zu sagen; die Erneuerung kann deshalb nicht rechtzeitig anlaufen und das "
                  + "Token stirbt im Betrieb. Es sollte einmal frisch geprägt werden."
                : claims.DaysRemaining(now) <= 0
                    ? "Das Token ist abgelaufen. TANSS nimmt damit nichts mehr an; jede Sitzung "
                      + "bleibt in der Warteschlange liegen, bis ein neues geprägt ist."
                    : null,
        });
    }

    /// <summary>
    /// Fragt mit einem folgenlosen Trockentest, ob der Mitarbeiter überhaupt Token prägen darf.
    /// </summary>
    /// <remarks>
    /// <c>isForTesting=true</c> ist der einzige folgenlose Weg, diese Frage zu stellen: TANSS
    /// antwortet mit einem unbrauchbaren Token von 60 Sekunden Laufzeit und protokolliert den
    /// Vorgang nicht. Die Antwort ist eine <b>Warnung</b> wert und kein Fehler — heute merkt man
    /// von einem fehlenden Recht nichts, aber am Tag des Ablaufs stirbt das Token lautlos.
    /// </remarks>
    /// <param name="ct">Abbruchmarke.</param>
    public async Task<bool?> CheckRotationRightAsync(CancellationToken ct = default)
    {
        if (Context.Composition is not { } composition)
        {
            return null;
        }

        // bool? bis hier durch: CanRotateAsync unterscheidet seit der Berichtigung zwischen
        // "abgewiesen" (false) und "konnte nicht gefragt werden" (null). Das hier auf bool zu
        // zwingen, stellte genau die Verwechslung wieder her, die dort beseitigt wurde.
        bool? allowed;
        try
        {
            allowed = await TanssAuth.CanRotateAsync(composition.Client, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Der Trockentest ist nicht zustande gekommen: Das ist "weiss ich nicht" und nicht
            // "darf nicht". Die beiden fuehren zu verschiedenen Handgriffen.
            return null;
        }

        _ = Publish(Token with { CanRotate = allowed });
        return allowed;
    }

    /// <inheritdoc />
    protected override async Task<string> RunCycleAsync(RuntimeComposition composition,
                                                        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(composition);

        TokenSnapshot snapshot = RefreshToken();

        if (!snapshot.IsReadable)
        {
            Context.ReportDegraded(DegradedCause.TokenUnreadable,
                snapshot.Problem ?? "Es liegt kein lesbares Token vor.",
                "Ein Token richtet bis auf Weiteres „tanss-logwatch token rotate“ ein. Die "
                + "Beobachtung läuft unterdessen weiter und die Warteschlange hält die "
                + "Sitzungen — es geht nichts verloren, es ruht nur das Senden.");

            return "Kein lesbares Token. Der Betriebszustand nennt den Grund.";
        }

        if (!snapshot.IsDue)
        {
            return snapshot.ExpiresAt is { } expiry
                ? string.Create(CultureInfo.CurrentCulture,
                    $"Token gültig bis {expiry:g}; noch {snapshot.DaysRemaining:0} Tage. "
                    + $"Erneuert wird ab {snapshot.RotateBeforeDays} Resttagen.")
                : "Das Token trägt keinen Ablauf; es gibt nichts zu erneuern.";
        }

        TokenRotationResult result = await RotateAsync(forced: false, ct).ConfigureAwait(false);
        return result.Reason;
    }

    /// <inheritdoc />
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _oneAtATime.Dispose();
        }

        base.Dispose(disposing);
    }

    /// <summary>Prägt, prüft und übernimmt erst dann.</summary>
    private async Task<TokenRotationResult> RotateAsync(bool forced, CancellationToken ct)
    {
        if (Context.Composition is not { } composition)
        {
            return new TokenRotationResult(false,
                "Ohne Konfiguration ist keine Instanz bekannt, bei der zu prägen wäre.",
                null, null, null);
        }

        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
                composition.Client, composition.Tokens,
                (minted, token) => VerifyAsync(composition, minted, token),
                // Erzwungen heisst: nicht nach Faelligkeit fragen. Wer die Schaltflaeche
                // drueckt, will wechseln; ob es faellig WAERE, steht in Token.
                beforeDays: forced ? int.MaxValue : composition.Config.Tanss.RotateBeforeDays,
                durationDays: DurationDays,
                info: MintInfo,
                now: Context.Clock.GetLocalNow(),
                ct: ct).ConfigureAwait(false);

            _ = RefreshToken();

            Log(composition, result);
            Context.Notifier.Raise(Rotated, this, result);

            if (result.Rotated)
            {
                // Ein geglueckter Wechsel ist ein bestandener echter Aufruf: Netz, Adresse und
                // Recht tragen damit nachweislich wieder.
                Context.ReportWorking();
            }
            else if (result.Error is { Length: > 0 })
            {
                Context.ReportDegraded(DegradedCause.TokenRejected,
                    result.Reason + " Meldung: " + result.Error,
                    "Es wurde nichts überschrieben; das bisherige Token bleibt in Kraft. Ist "
                    + "beim Prägen bereits eines entstanden, bleibt es in TANSS stehen, ohne "
                    + "benutzt zu werden — ein Widerruf ist in 10.10.0 nicht vorgesehen. Ein "
                    + "zweiter Versuch lohnt nur, wenn die Ursache behoben ist.");
            }

            return result;
        }
        finally
        {
            _ = _oneAtATime.Release();
        }
    }

    /// <summary>
    /// Die Probe auf das neue Token: ein echter, harmloser Aufruf.
    /// </summary>
    /// <remarks>
    /// <c>GET /api/tanss.x/v1/remoteSupports/systems</c> ist dafür die richtige Route — sie
    /// liest, ändert nichts und braucht genau die Rechte, auf die es diesem Werkzeug ankommt.
    /// Eine leere Liste ist dabei kein Fehlschlag: Bewiesen ist, dass der Aufruf angenommen
    /// wurde, nicht dass die Instanz Anbindungen pflegt.
    /// </remarks>
    private static async Task<bool> VerifyAsync(RuntimeComposition composition, string minted,
                                                CancellationToken ct)
    {
        using ITanssClient probe = composition.CreateClientWith(minted);
        _ = await new RemoteSupportRepository(probe).ListSystemsAsync(ct).ConfigureAwait(false);
        return true;
    }

    /// <summary>Übernimmt und meldet einen neuen Stand des Tokens.</summary>
    private TokenSnapshot Publish(TokenSnapshot snapshot)
    {
        Token = snapshot;
        Context.Notifier.Raise(TokenChanged, this, snapshot);
        return snapshot;
    }

    /// <summary>Vermerkt den Wechsel im Änderungsprotokoll.</summary>
    /// <remarks>
    /// Ohne diesen Eintrag wäre nach einem lautlos misslungenen Wechsel nicht mehr zu klären,
    /// wann zuletzt geprägt wurde — und in TANSS stünden Token, zu denen niemand mehr sagen
    /// kann, woher sie stammen.
    /// </remarks>
    private static void Log(RuntimeComposition composition, TokenRotationResult result)
    {
        try
        {
            _ = composition.Log.Append(new SessionLogEntry
            {
                Operation = "token.rotate",
                Outcome = result.Rotated ? SessionOutcome.Ok : SessionOutcome.Skipped,
                Reason = result.Reason
                    + (result.Error is { Length: > 0 } error ? " Meldung: " + error : string.Empty),
                Trigger = SessionTrigger.Watcher,
            });
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // Hausregel 5: Ein nicht geschriebener Protokolleintrag darf keinen Wechsel kosten.
        }
    }
}
