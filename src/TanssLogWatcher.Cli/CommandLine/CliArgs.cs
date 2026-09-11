namespace TanssLogWatcher.Cli.CommandLine;

/// <summary>Die Befehle des Werkzeugs.</summary>
public enum CliCommand
{
    /// <summary>Kein Befehl erkannt.</summary>
    None,

    /// <summary>Einrichtung, Verbindung, Token und Warteschlange prüfen.</summary>
    Doctor,

    /// <summary>Sitzungen beobachten.</summary>
    Watch,

    /// <summary>Sichtbare Fenster auflisten.</summary>
    Windows,

    /// <summary>Warteschlange anzeigen und senden.</summary>
    Queue,

    /// <summary>Tokenstand und Tokenwechsel.</summary>
    Token,
}

/// <summary>
/// Die ausgewertete Befehlszeile.
/// </summary>
/// <remarks>
/// <para><b>Von Hand ausgewertet, ohne Paket.</b> <c>System.CommandLine</c> liegt zwar in einer
/// stabilen Fassung vor (geprüft am 11.09.2026: 2.0.x auf nuget.org, netstandard2.0 und damit
/// auch für net10.0) — bei fünf Befehlen, zwei Schaltern und einer Option trägt es aber nichts
/// bei, was diese Datei nicht in wenigen Zeilen leistet, und es brächte eine eigene,
/// englischsprachige Hilfe- und Fehlerausgabe mit. Deutsche Benutzertexte sind hier Hausregel
/// und keine Kür.</para>
/// <para>Die Auswertung wirft <b>nie</b>. Ein Aufruffehler ist ein Ergebnis
/// (<see cref="Error"/>), kein Ausnahmefall: Er soll denselben Weg durch die Ausgabe nehmen
/// wie jede andere Meldung und mit <see cref="ExitCode.Usage"/> enden.</para>
/// </remarks>
public sealed record CliArgs
{
    /// <summary>Der erkannte Befehl.</summary>
    public CliCommand Command { get; init; }

    /// <summary>Der Unterbefehl, derzeit nur bei <see cref="CliCommand.Token"/>.</summary>
    public string? SubCommand { get; init; }

    /// <summary><c>watch --dry-run</c>: zeigen, was geschähe, ohne etwas einzureihen.</summary>
    public bool DryRun { get; init; }

    /// <summary><c>queue --flush</c>: fällige Einträge senden.</summary>
    public bool Flush { get; init; }

    /// <summary><c>windows --profile &lt;Schlüssel&gt;</c>: Profilschlüssel aus dem Katalog.</summary>
    public string? ProfileKey { get; init; }

    /// <summary>Hilfe wurde angefordert.</summary>
    public bool HelpRequested { get; init; }

    /// <summary>Die Fassung wurde angefordert.</summary>
    public bool VersionRequested { get; init; }

    /// <summary>
    /// Der Aufruffehler in deutscher Prosa, sonst <c>null</c>.
    /// </summary>
    /// <remarks>
    /// Der Text sagt, was nicht ging und was stattdessen erwartet wird. Ein blosses
    /// „unbekannte Option“ zwänge den Benutzer, die Hilfe abzutippen, bis er die richtige
    /// Schreibweise trifft.
    /// </remarks>
    public string? Error { get; init; }

    /// <summary>Wertet die Befehlszeile aus.</summary>
    /// <param name="args">Die Argumente ohne den Programmnamen.</param>
    public static CliArgs Parse(IReadOnlyList<string>? args)
    {
        if (args is null || args.Count == 0)
        {
            return new CliArgs
            {
                Error = "Es wurde kein Befehl angegeben. Erwartet wird einer von: doctor, "
                    + "watch, windows, queue, token. Die vollständige Übersicht zeigt "
                    + "„tanss-logwatch --help“.",
            };
        }

        string head = args[0];
        if (IsHelp(head))
        {
            return new CliArgs { HelpRequested = true };
        }

        if (string.Equals(head, "--version", StringComparison.Ordinal))
        {
            return new CliArgs { VersionRequested = true };
        }

        CliArgs result = head switch
        {
            "doctor" => new CliArgs { Command = CliCommand.Doctor },
            "watch" => new CliArgs { Command = CliCommand.Watch },
            "windows" => new CliArgs { Command = CliCommand.Windows },
            "queue" => new CliArgs { Command = CliCommand.Queue },
            "token" => new CliArgs { Command = CliCommand.Token },
            _ => new CliArgs
            {
                Error = $"Unbekannter Befehl „{head}“. Erwartet wird einer von: doctor, watch, "
                    + "windows, queue, token. Die vollständige Übersicht zeigt "
                    + "„tanss-logwatch --help“.",
            },
        };

        if (result.Error is not null)
        {
            return result;
        }

        for (int index = 1; index < args.Count; index++)
        {
            string token = args[index];

            if (IsHelp(token))
            {
                return result with { HelpRequested = true };
            }

            switch (token)
            {
                case "--dry-run" when result.Command == CliCommand.Watch:
                    result = result with { DryRun = true };
                    continue;

                case "--flush" when result.Command == CliCommand.Queue:
                    result = result with { Flush = true };
                    continue;

                case "--profile" when result.Command == CliCommand.Windows:
                    if (index + 1 >= args.Count)
                    {
                        return result with
                        {
                            Error = "„--profile“ erwartet einen Profilschlüssel, etwa "
                                + "„--profile mstsc“. Welche Schlüssel es gibt, zeigt "
                                + "„tanss-logwatch windows“ am Ende der Ausgabe.",
                        };
                    }

                    result = result with { ProfileKey = args[++index] };
                    continue;

                default:
                    break;
            }

            if (result.Command == CliCommand.Token && result.SubCommand is null
                && !token.StartsWith('-'))
            {
                if (token is not ("status" or "rotate"))
                {
                    return result with
                    {
                        Error = $"„token {token}“ gibt es nicht. Erwartet wird „token status“ "
                            + "(Restlaufzeit und Erneuerungsfähigkeit) oder „token rotate“ "
                            + "(neues Token prägen, prüfen und erst dann übernehmen).",
                    };
                }

                result = result with { SubCommand = token };
                continue;
            }

            return result with { Error = Unexpected(result.Command, token) };
        }

        if (result.Command == CliCommand.Token && result.SubCommand is null)
        {
            return result with
            {
                Error = "„token“ braucht einen Unterbefehl: „token status“ zeigt Restlaufzeit "
                    + "und Erneuerungsfähigkeit, „token rotate“ prägt ein neues Token, prüft "
                    + "es mit einem echten Aufruf und übernimmt es erst dann.",
            };
        }

        return result;
    }

    /// <summary>Der Name eines Befehls, so wie er auf der Kommandozeile steht.</summary>
    public static string Name(CliCommand command) => command switch
    {
        CliCommand.Doctor => "doctor",
        CliCommand.Watch => "watch",
        CliCommand.Windows => "windows",
        CliCommand.Queue => "queue",
        CliCommand.Token => "token",
        _ => "(keiner)",
    };

    private static bool IsHelp(string token) => token is "--help" or "-h" or "help" or "/?";

    /// <summary>
    /// Die Meldung für ein Argument, das dieser Befehl nicht kennt.
    /// </summary>
    /// <remarks>
    /// Sie nennt ausdrücklich, was der Befehl stattdessen kennt. Ein Schalter, den es nur bei
    /// einem anderen Befehl gibt — <c>watch --flush</c> etwa —, ist der häufigste Fall, und
    /// eine Meldung ohne diese Angabe schickte den Benutzer auf die Suche.
    /// </remarks>
    private static string Unexpected(CliCommand command, string token)
    {
        string known = command switch
        {
            CliCommand.Watch => "„--dry-run“",
            CliCommand.Windows => "„--profile <Schlüssel>“",
            CliCommand.Queue => "„--flush“",
            CliCommand.Token => "„status“ oder „rotate“",
            _ => "keine weiteren Argumente",
        };

        return $"„{token}“ passt nicht zum Befehl „{Name(command)}“. Dort erwartet werden {known}.";
    }
}
