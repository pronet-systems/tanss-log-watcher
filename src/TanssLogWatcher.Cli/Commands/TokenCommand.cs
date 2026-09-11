using System.Globalization;
using TanssLogWatcher.Api;
using TanssLogWatcher.Api.Auth;
using TanssLogWatcher.Api.Contract;
using TanssLogWatcher.Api.Diagnostics;
using TanssLogWatcher.Api.Repository;
using TanssLogWatcher.Cli.Output;
using TanssLogWatcher.Storage.Config;

namespace TanssLogWatcher.Cli.Commands;

/// <summary>Was <c>token</c> braucht.</summary>
public sealed record TokenInputs
{
    /// <summary>Die geprüfte Konfiguration.</summary>
    public required AppConfig Config { get; init; }

    /// <summary>Der Zugang mit dem <b>bisherigen</b> Token.</summary>
    public required ITanssClient Client { get; init; }

    /// <summary>Der Tokenspeicher.</summary>
    public required ITokenStore Tokens { get; init; }

    /// <summary>
    /// Baut einen Zugang mit einem mitgegebenen Token.
    /// </summary>
    /// <remarks>
    /// Für die Probe beim Wechsel. Das frisch geprägte Token muss sich bewähren, bevor es das
    /// bisherige ablöst — geprüft wird deshalb mit einem Zugang, der es trägt, ohne dass es
    /// schon im Speicher steht.
    /// </remarks>
    public required Func<string, ITanssClient> ClientFactory { get; init; }

    /// <summary>Die Uhr; für Tests einsetzbar.</summary>
    public TimeProvider Clock { get; init; } = TimeProvider.System;
}

/// <summary>
/// Zeigt den Stand des Tokens und wechselt es auf Wunsch.
/// </summary>
/// <remarks>
/// <para><b>Geprägt wird über <c>GET /api/v1/jwts/tanss_app</c> — ein Lesevorgang ist das
/// nicht.</b> TANSS stellt bei jedem Aufruf ein neues JWT aus und trägt es in sein
/// Tokenprotokoll ein; widerrufen lässt sich in 10.10.0 keines. Deshalb läuft diese Route mit
/// genau einem Versuch, und deshalb entscheidet über eine Wiederholung ein Mensch.</para>
/// <para><b>Übernommen wird erst nach bestandener Probe.</b> Solange das alte Token gültig
/// ist, ist der schlechteste Ausgang eines misslungenen Wechsels, dass alles bleibt, wie es
/// war. Umgekehrt — erst schreiben, dann prüfen — stünde das Werkzeug bei einem fehlerhaften
/// neuen Token ohne jeden Zugang da, womöglich mitten in einer Fernwartung.</para>
/// </remarks>
public static class TokenCommand
{
    /// <summary>Der Freitext, den TANSS neben dem Token protokolliert.</summary>
    private const string MintInfo = "TANSS Log-Watcher";

    /// <summary>Führt <c>token status</c> oder <c>token rotate</c> aus.</summary>
    /// <param name="inputs">Die Bausteine.</param>
    /// <param name="subCommand">„status“ oder „rotate“.</param>
    /// <param name="writer">Wohin die Ausgabe geht.</param>
    /// <param name="ct">Abbruchmarke.</param>
    public static Task<int> RunAsync(TokenInputs inputs, string subCommand, TextWriter writer,
                                     CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(inputs);
        ArgumentNullException.ThrowIfNull(writer);

        return subCommand switch
        {
            "status" => StatusAsync(inputs, writer, ct),
            "rotate" => RotateAsync(inputs, writer, ct),
            _ => throw new ArgumentOutOfRangeException(nameof(subCommand), subCommand,
                "Erwartet wird „status“ oder „rotate“."),
        };
    }

    /// <summary>Restlaufzeit, Erneuerungsfähigkeit und der nächste Erneuerungstermin.</summary>
    private static async Task<int> StatusAsync(TokenInputs inputs, TextWriter writer,
                                               CancellationToken ct)
    {
        writer.WriteLine("Token");
        writer.WriteLine();

        TanssTokenClaims claims;
        try
        {
            claims = TanssAuth.DecodeClaims(inputs.Tokens.Read());
        }
        catch (TanssAuthException ex)
        {
            Report.WriteCheck(writer, new CheckResult("Hinterlegtes Token", CheckLevel.Fail,
                Redaction.Scrub(ex.Message)
                + " Ein beschädigter oder fehlender Tokenspeicher heilt nicht von selbst; er ist "
                + "mit „tanss-logwatch setup“ neu einzurichten."));
            return ExitCode.Broken;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Report.WriteCheck(writer, new CheckResult("Hinterlegtes Token", CheckLevel.Fail,
                "Das Token liess sich nicht aus dem lokalen Profil lesen. Üblichste Ursache: ein "
                + "anderes Windows-Konto als bei der Einrichtung — die Ablage ist mit DPAPI an "
                + "Benutzer und Rechner gebunden. Meldung: " + Redaction.Scrub(ex.Message)));
            return ExitCode.Broken;
        }

        int worst = ExitCode.Healthy;
        void Say(CheckResult check)
        {
            Report.WriteCheck(writer, check);
            worst = ExitCode.Worse(worst, Report.ToExitCode(check.Level));
        }

        DateTimeOffset now = inputs.Clock.GetUtcNow();
        int beforeDays = inputs.Config.Tanss.RotateBeforeDays;

        if (claims.ExpiresAt is not { } expiry)
        {
            Say(new CheckResult("Restlaufzeit", CheckLevel.Warn,
                "Das Token trägt keinen exp-Anspruch. Wann es abläuft, ist von hier aus nicht zu "
                + "sagen; die Erneuerung kann deshalb nicht rechtzeitig anlaufen. Einmal frisch "
                + "prägen: „tanss-logwatch token rotate“."));
        }
        else
        {
            TimeSpan remaining = expiry - now;
            DateTimeOffset due = expiry - TimeSpan.FromDays(beforeDays);

            Say(new CheckResult("Restlaufzeit",
                remaining <= TimeSpan.Zero ? CheckLevel.Fail
                    : remaining <= TimeSpan.FromDays(beforeDays) ? CheckLevel.Warn
                    : CheckLevel.Ok,
                $"Gültig bis {Report.Moment(expiry)} ({Report.Duration(remaining)})."
                + (claims.IssuedAt is { } issued
                    ? $" Ausgestellt am {Report.Moment(issued)}."
                    : string.Empty)));

            Say(new CheckResult("Nächster Erneuerungstermin",
                due <= now ? CheckLevel.Warn : CheckLevel.Ok,
                due <= now
                    ? string.Create(CultureInfo.InvariantCulture,
                        $"Fällig seit {Report.Moment(due)} (tanss.rotate_before_days = {beforeDays}). ")
                      + "Sie geschieht nicht von selbst, solange kein Dienst läuft: "
                      + "„tanss-logwatch token rotate“."
                    : string.Create(CultureInfo.InvariantCulture,
                        $"Ab {Report.Moment(due)}, also in {Report.Duration(due - now)} ")
                      + string.Create(CultureInfo.InvariantCulture,
                        $"(tanss.rotate_before_days = {beforeDays}).")));
        }

        bool allowed;
        try
        {
            allowed = await TanssAuth.CanRotateAsync(inputs.Client, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            Say(new CheckResult("Erneuerungsfähigkeit (Trockentest)", CheckLevel.Warn,
                "Der Trockentest ist nicht zustande gekommen. Meldung: "
                + Redaction.Scrub(ex.Message)));
            return worst;
        }

        Say(allowed
            ? new CheckResult("Erneuerungsfähigkeit (Trockentest)", CheckLevel.Ok,
                "Der Mitarbeiter darf Token prägen; der Wechsel wird gelingen. Der Trockentest "
                + "selbst bleibt folgenlos — TANSS protokolliert ihn nicht.")
            : new CheckResult("Erneuerungsfähigkeit (Trockentest)", CheckLevel.Warn,
                "Der Mitarbeiter darf keine Token prägen (in TANSS das Recht 480). Heute merkt "
                + "man davon nichts; am Tag des Ablaufs stirbt das Token lautlos, und ab dann "
                + "bleibt jede Fernwartung liegen. Das Recht ist in TANSS zu vergeben."));

        return worst;
    }

    /// <summary>Prägt, prüft und übernimmt erst dann.</summary>
    private static async Task<int> RotateAsync(TokenInputs inputs, TextWriter writer,
                                               CancellationToken ct)
    {
        writer.WriteLine("Tokenwechsel");
        writer.WriteLine();
        writer.WriteLine("Geprägt wird über GET /api/v1/jwts/tanss_app. Das Verb ist GET, der");
        writer.WriteLine("Vorgang ist es nicht: TANSS stellt dabei ein neues Token aus und kann es");
        writer.WriteLine("nicht widerrufen. Deshalb genau ein Versuch — und das bisherige Token");
        writer.WriteLine("bleibt in Kraft, bis das neue sich bewährt hat.");
        writer.WriteLine();

        TokenRotationResult result = await TanssAuth.RotateIfNeededAsync(
            inputs.Client, inputs.Tokens,
            (minted, token) => VerifyAsync(inputs, minted, token),
            // Ausdruecklich erzwungen: Wer "token rotate" tippt, will wechseln, nicht hoeren,
            // dass es noch nicht faellig ist. Ob es faellig WAERE, sagt "token status".
            beforeDays: int.MaxValue,
            durationDays: 365,
            info: MintInfo,
            now: inputs.Clock.GetUtcNow(),
            ct: ct).ConfigureAwait(false);

        Report.WriteCheck(writer, new CheckResult(
            result.Rotated ? "Gewechselt" : "Nicht gewechselt",
            result.Rotated ? CheckLevel.Ok : CheckLevel.Fail,
            result.Reason + (result.Error is { Length: > 0 } error ? " Meldung: " + error : string.Empty)));

        if (result.OldExpiry is { } old)
        {
            writer.WriteLine($"            Bisheriges Token: gültig bis {Report.Moment(old)}");
        }

        if (result.NewExpiry is { } fresh)
        {
            writer.WriteLine($"            Neues Token:      gültig bis {Report.Moment(fresh)}");
        }

        if (!result.Rotated)
        {
            writer.WriteLine();
            writer.WriteLine("Es wurde nichts überschrieben. Ist beim Prägen bereits ein Token");
            writer.WriteLine("entstanden, bleibt es in TANSS stehen, ohne benutzt zu werden — ein");
            writer.WriteLine("Widerruf ist in 10.10.0 nicht vorgesehen. Ein zweiter Versuch prägt");
            writer.WriteLine("ein weiteres; er lohnt also nur, wenn die Ursache behoben ist.");
        }

        return result.Rotated ? ExitCode.Healthy : ExitCode.Broken;
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
    private static async Task<bool> VerifyAsync(TokenInputs inputs, string minted,
                                                CancellationToken ct)
    {
        using ITanssClient probe = inputs.ClientFactory(minted);
        _ = await new RemoteSupportRepository(probe).ListSystemsAsync(ct).ConfigureAwait(false);
        return true;
    }
}
