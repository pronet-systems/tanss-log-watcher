namespace TanssLogWatcher.Api;

/// <summary>Basis aller Fehler, die aus dem Umgang mit TANSS stammen.</summary>
public class TanssException : Exception
{
    public int? Status { get; }

    /// <summary>Der Wert aus <c>error.text</c>, etwa <c>TYPE_GREATER_1000</c>.</summary>
    public string? Detail { get; }

    public TanssException(string message, int? status = null, string? detail = null,
                          Exception? inner = null)
        : base(message, inner)
    {
        Status = status;
        Detail = detail;
    }
}

/// <summary>TANSS war nicht erreichbar, oder die Verbindung brach ab.</summary>
public sealed class TanssUnreachableException : TanssException
{
    public TanssUnreachableException(string message, Exception? inner = null)
        : base(message, inner: inner) { }
}

/// <summary>
/// Zugriff verweigert (401/403). Die Nachricht nennt die drei üblichen Ursachen, weil
/// eine nackte 403 den Aufrufer sonst ratlos lässt.
/// </summary>
public sealed class TanssAuthException : TanssException
{
    public TanssAuthException(string message, int? status = null, string? detail = null)
        : base(message, status, detail) { }
}

/// <summary>Das angefragte Objekt gibt es nicht.</summary>
public sealed class TanssNotFoundException : TanssException
{
    public TanssNotFoundException(string message, int? status = null, string? detail = null)
        : base(message, status, detail) { }
}

/// <summary>
/// Der Fernwartungstyp taugt nicht: kleiner als 1000, oder in TANSS nicht angelegt.
/// Entspricht <c>TYPE_GREATER_1000</c> und <c>TYPE_DOESNT_EXIST</c>.
/// </summary>
public sealed class TanssRemoteSupportTypeException : TanssException
{
    public TanssRemoteSupportTypeException(string message, int? status = null, string? detail = null)
        : base(message, status, detail) { }
}

/// <summary>
/// Die Einstellungen taugen nicht — zuallererst die Basisadresse.
/// </summary>
/// <remarks>
/// Getrennt von <see cref="TanssUnreachableException"/>, weil der Aufrufer anders reagieren
/// muss: hier hilft kein zweiter Versuch und kein Warten, sondern nur eine berichtigte
/// Einstellung. Entsprechend gilt dieser Fehler nie als vorübergehend.
/// </remarks>
public sealed class TanssConfigurationException : TanssException
{
    public TanssConfigurationException(string message, Exception? inner = null)
        : base(message, inner: inner) { }
}

/// <summary>Das Modul Fernwartung ist auf der Instanz nicht lizenziert.</summary>
public sealed class TanssModuleNotLicensedException : TanssException
{
    public TanssModuleNotLicensedException(string message, int? status = null, string? detail = null)
        : base(message, status, detail) { }
}
