using System.Globalization;
using System.Text.Json.Serialization;

namespace TanssLogWatcher.Api.Model;

/// <summary>
/// Eine Firma aus der Suche <c>PUT /api/v1/search</c> mit dem Bereich <c>COMPANY</c>.
/// </summary>
/// <remarks>
/// <para><b>Namensdubletten sind der Normalfall, nicht die Ausnahme.</b> Nachgemessen gegen eine
/// Instanz der Version 10.10.0 stand derselbe Firmenname fünfmal mit verschiedenen Kennungen in
/// der Trefferliste. Eine Auswahl, die nur <see cref="Name"/> zeigt, ist damit unbrauchbar —
/// deshalb trägt dieses Modell <see cref="DisplayId"/>, <see cref="PostCode"/> und
/// <see cref="City"/> und bietet mit <see cref="Distinguisher"/> die Zeile an, die die Dubletten
/// auseinanderhält.</para>
/// <para><b>Nicht abgebildet sind <c>types</c> und <c>anticipatedCallbacks</c></b> sowie die
/// Kontaktfelder (<c>phoneNumber</c>, <c>faxNumber</c>, <c>email</c>, <c>website</c>,
/// <c>mobileNumber</c>, <c>privateNumber</c>). Sie kommen in der Antwort mit, unterscheiden aber
/// keine Dublette und werden für die Auswahl einer Firma nicht gebraucht. Wer sie später
/// braucht, ergänzt sie hier; ungenutzte Felder mitzuschleppen heißt nur, sie irgendwann falsch
/// zu deuten.</para>
/// </remarks>
public sealed record Company
{
    /// <summary>Die Kennung, mit der TANSS diese Firma führt. Das ist die <c>companyId</c>.</summary>
    [JsonPropertyName("id")] public int Id { get; init; }

    /// <summary>Der Firmenname. Allein <b>nicht</b> eindeutig.</summary>
    [JsonPropertyName("name")] public string Name { get; init; } = string.Empty;

    /// <summary>Straße und Hausnummer.</summary>
    [JsonPropertyName("street")] public string? Street { get; init; }

    /// <summary>Postleitzahl. Unterscheidet Dubletten.</summary>
    [JsonPropertyName("postCode")] public string? PostCode { get; init; }

    /// <summary>Ort. Unterscheidet Dubletten.</summary>
    [JsonPropertyName("city")] public string? City { get; init; }

    /// <summary>Land.</summary>
    [JsonPropertyName("country")] public string? Country { get; init; }

    /// <summary>Die Kundennummer, wie sie der Anwender kennt. Unterscheidet Dubletten.</summary>
    [JsonPropertyName("displayId")] public string? DisplayId { get; init; }

    /// <summary>
    /// Die Firma ist inaktiv gesetzt.
    /// </summary>
    /// <remarks>
    /// Nachgemessen kommen inaktive Firmen mit: 5 von 11 Treffern einer Suche trugen
    /// <c>inactive=true</c>. Sie bleiben <b>wählbar</b> — auf eine inaktive Firma zu buchen ist
    /// erlaubt und kommt vor —, müssen aber sichtbar gekennzeichnet sein; siehe
    /// <see cref="StatusNote"/>.
    /// </remarks>
    [JsonPropertyName("inactive")] public bool Inactive { get; init; }

    /// <summary>
    /// Die Firma ist gesperrt.
    /// </summary>
    /// <remarks>
    /// Die Beschreibung sagt dazu wörtlich „no service may be entered“. Eine gesperrte Firma
    /// darf deshalb nicht wählbar sein — <see cref="Selectable"/> ist für sie
    /// <see langword="false"/>. Ausgeblendet wird sie trotzdem nicht: Wer eine gesperrte Firma
    /// sucht, soll erfahren, <i>dass</i> sie gesperrt ist, und nicht „nicht gefunden“ lesen.
    /// </remarks>
    [JsonPropertyName("lockout")] public bool Lockout { get; init; }

    /// <summary>
    /// Zentrale, Filiale oder keines von beidem — <c>NONE</c>, <c>CENTRAL</c>, <c>BRANCH</c>.
    /// </summary>
    /// <remarks>
    /// Bewusst eine Zeichenkette und keine Aufzählung: ein unbekannter Wert aus einer späteren
    /// TANSS-Version würde beim Einlesen einer Aufzählung die ganze Antwort zu Fall bringen.
    /// Eine Firmensuche darf nicht daran scheitern, dass TANSS einen neuen Typ kennt.
    /// </remarks>
    [JsonPropertyName("centralType")] public string? CentralType { get; init; }

    /// <summary>Die „Firma“ ist in Wahrheit ein Privatkunde.</summary>
    [JsonPropertyName("personalCustomer")] public bool PersonalCustomer { get; init; }

    /// <summary>Darf auf diese Firma gebucht werden?</summary>
    /// <remarks>Gesperrt heißt nein. Inaktiv heißt ja.</remarks>
    [JsonIgnore] public bool Selectable => !Lockout;

    /// <summary>
    /// Der Hinweis, der neben dem Namen stehen muss — oder <see langword="null"/>, wenn alles in
    /// Ordnung ist.
    /// </summary>
    [JsonIgnore]
    public string? StatusNote => Lockout
        ? "gesperrt – für diese Firma darf keine Leistung erfasst werden"
        : Inactive ? "inaktiv" : null;

    /// <summary>
    /// Das, was zwei gleichnamige Firmen auseinanderhält: Kundennummer, Postleitzahl, Ort.
    /// </summary>
    /// <remarks>
    /// Leere Felder fallen weg. Bleibt gar nichts übrig, steht dort die TANSS-Kennung — irgendein
    /// Unterschied muss sichtbar sein, sonst wählt der Techniker zwischen zwei gleich
    /// aussehenden Zeilen.
    /// </remarks>
    [JsonIgnore]
    public string Distinguisher
    {
        get
        {
            List<string> parts = [];
            if (!string.IsNullOrWhiteSpace(DisplayId))
            {
                parts.Add(DisplayId);
            }

            string place = string.Join(
                " ",
                new[] { PostCode, City }.Where(part => !string.IsNullOrWhiteSpace(part)));
            if (place.Length > 0)
            {
                parts.Add(place);
            }

            return parts.Count == 0
                ? "ID " + Id.ToString(CultureInfo.CurrentCulture)
                : string.Join(" · ", parts);
        }
    }

    /// <summary>Name samt Unterscheidungsmerkmal — die Zeile für eine Auswahlliste.</summary>
    public override string ToString() => $"{Name} ({Distinguisher})";
}

/// <summary>
/// Der Suchrumpf für <c>PUT /api/v1/search</c>, beschränkt auf den Bereich <c>COMPANY</c>.
/// </summary>
/// <remarks>
/// Die Route sucht auch Mitarbeiter und Tickets. Dieses Werkzeug fragt nur nach Firmen; was
/// nicht angefragt wird, kommt auch nicht zurück und muss nicht abgebildet werden.
/// </remarks>
public sealed record CompanySearchRequest
{
    /// <summary>Die durchsuchten Bereiche. Hier immer genau <c>COMPANY</c>.</summary>
    [JsonPropertyName("areas")] public IReadOnlyList<string> Areas { get; init; } = ["COMPANY"];

    /// <summary>Der Suchbegriff.</summary>
    /// <remarks>
    /// Nachgemessen ist die Suche eine <b>Teilzeichenkettensuche</b> und nicht auf den Wortanfang
    /// beschränkt: <c>roN</c> trifft <c>ProNet</c>. Groß- und Kleinschreibung sind gleichgültig.
    /// </remarks>
    [JsonPropertyName("query")] public string Query { get; init; } = string.Empty;

    /// <summary>Die bereichsbezogenen Einstellungen.</summary>
    [JsonPropertyName("configs")] public CompanySearchConfigs Configs { get; init; } = new();
}

/// <summary>Der Einstellungsblock der Suche; belegt ist hier nur der Firmenteil.</summary>
public sealed record CompanySearchConfigs
{
    /// <summary>Die Einstellungen der Firmensuche.</summary>
    [JsonPropertyName("company")] public CompanySearchConfig Company { get; init; } = new();
}

/// <summary>Die Einstellungen der Firmensuche.</summary>
public sealed record CompanySearchConfig
{
    /// <summary>
    /// <b>Eine Schwelle, keine Begrenzung.</b>
    /// </summary>
    /// <remarks>
    /// <para>Der Name führt in die Irre, und die Beschreibung tut es auch. Nachgemessen gegen
    /// eine Instanz der Version 10.10.0: Der Suchbegriff <c>Gmb</c> hat 540 Treffer. Mit
    /// <c>maxResults=539</c> kommen <b>null</b> Treffer zurück, mit <c>540</c> kommen alle 540.
    /// TANSS kürzt also nicht, sondern verweigert die Auskunft, sobald die Trefferzahl die
    /// Schwelle übersteigt.</para>
    /// <para>Der dokumentierte Vorgabewert ist 100. Wer ihn übernimmt, bekommt bei jeder
    /// unspezifischen Suche eine leere Liste und sagt dem Anwender „keine Firma gefunden“ — eine
    /// Unwahrheit über 540 Firmen.</para>
    /// </remarks>
    [JsonPropertyName("maxResults")] public int MaxResults { get; init; }
}

/// <summary>Der <c>content</c>-Block der Suchantwort, soweit er hier gebraucht wird.</summary>
/// <remarks>
/// TANSS antwortet mit einem Objekt je Bereich (<c>companies</c>, <c>employees</c>,
/// <c>tickets</c>). Angefragt wird nur <c>COMPANY</c>; die übrigen Schlüssel bleiben
/// unabgebildet, statt sie mit leeren Listen nachzubauen.
/// </remarks>
public sealed record CompanySearchResponse
{
    /// <summary>Die gefundenen Firmen. Fehlt der Schlüssel, ist das wie eine leere Liste.</summary>
    [JsonPropertyName("companies")] public IReadOnlyList<Company>? Companies { get; init; }
}

/// <summary>
/// Wie sicher ist die Antwort der Firmensuche?
/// </summary>
/// <remarks>
/// Diese Aufzählung ist der Kern des Vertrags. Eine leere Antwort von TANSS bedeutet
/// <b>zweierlei</b> — „es gibt nichts“ oder „es sind zu viele“ —, und eine Oberfläche, die beides
/// zu „keine Firma gefunden“ verschmilzt, sagt in der Hälfte der Fälle die Unwahrheit.
/// </remarks>
public enum CompanySearchOutcome
{
    /// <summary>Der Suchbegriff war zu kurz; es wurde gar nicht erst gefragt.</summary>
    QueryTooShort,

    /// <summary>
    /// Es gibt Treffer, und zwar <b>vollzählig</b>: Eine nichtleere Antwort von TANSS ist immer
    /// die ganze Menge, weil die Schwelle sonst gar nichts geliefert hätte.
    /// </summary>
    Found,

    /// <summary>
    /// Es gibt <b>nachweislich</b> keine Firma zu diesem Suchbegriff.
    /// </summary>
    /// <remarks>
    /// Nachgewiesen über die Gegenprobe mit einer Obermenge, nicht geraten — die Begründung steht
    /// bei <c>CompanyRepository</c>.
    /// </remarks>
    NoMatch,

    /// <summary>
    /// <b>Nicht ermittelt.</b> TANSS hat nichts geliefert, und es ließ sich nicht klären, ob es
    /// nichts gibt oder ob es zu viele sind.
    /// </summary>
    /// <remarks>
    /// Die Oberfläche muss diesen Fall als Unklarheit zeigen und darf ihn nicht zu „keine Firma
    /// gefunden“ verkürzen. <see cref="CompanySearchResult.Explanation"/> trägt den passenden
    /// Satz.
    /// </remarks>
    Undetermined,
}

/// <summary>
/// Das Ergebnis einer Firmensuche — die Treffer <b>und</b> die Aussage darüber, was eine leere
/// Liste bedeutet.
/// </summary>
public sealed record CompanySearchResult
{
    /// <summary>Wie die Antwort zu lesen ist.</summary>
    public CompanySearchOutcome Outcome { get; init; }

    /// <summary>
    /// Die anzuzeigenden Firmen, nach Passgenauigkeit geordnet und gegebenenfalls gekürzt.
    /// </summary>
    /// <remarks>
    /// <para>Gesperrte Firmen stehen mit drin, aber mit <see cref="Company.Selectable"/> gleich
    /// <see langword="false"/>. Sie wegzulassen hieße, dem Suchenden „gibt es nicht“ zu sagen, wo
    /// „gesperrt“ richtig wäre.</para>
    /// <para><b>Inaktive Firmen stehen nicht mit drin.</b> Sie werden ausgeblendet und nicht
    /// gekennzeichnet — gemessen sind es in dieser Instanz 5 von 11 Treffern, und auf eine
    /// aufgelöste Firma wird nicht gebucht. Verschwiegen sind sie damit nicht: Ihre Zahl steht
    /// in <see cref="HiddenInactive"/> und als Satz in <see cref="Explanation"/>. Der
    /// Unterschied zu „gesperrt“ ist Absicht — gesperrt heißt „gibt es, darf nicht“, inaktiv
    /// heißt „war einmal“.</para>
    /// </remarks>
    public IReadOnlyList<Company> Companies { get; init; } = [];

    /// <summary>Wie viele Firmen TANSS insgesamt genannt hat — vor dem Kürzen.</summary>
    /// <remarks>Inaktive mitgezählt: Es ist die Zahl, die TANSS genannt hat.</remarks>
    public int TotalFound { get; init; }

    /// <summary>Wie viele Treffer ausgeblendet wurden, weil sie inaktiv sind.</summary>
    /// <remarks>
    /// Eine ausgeblendete Zeile, die niemand zählt, ist eine verschwiegene Zeile. Ohne diese
    /// Zahl könnte eine Suche mit lauter inaktiven Treffern wie „es gibt nichts“ aussehen — und
    /// das wäre dieselbe stille Unwahrheit, gegen die <see cref="CompanySearchOutcome"/>
    /// überhaupt erst angelegt wurde, nur selbstgemacht.
    /// </remarks>
    public int HiddenInactive { get; init; }

    /// <summary>Die Schwelle, mit der zuletzt gefragt wurde.</summary>
    public int Threshold { get; init; }

    /// <summary>Wie viele Aufrufe an TANSS die Antwort gekostet hat.</summary>
    /// <remarks>Nur zur Nachvollziehbarkeit im Protokoll; die Oberfläche braucht das nicht.</remarks>
    public int Requests { get; init; }

    /// <summary>Ein fertiger deutscher Satz für die Oberfläche.</summary>
    /// <remarks>
    /// Fertig formuliert und nicht bloß ein Schlüsselwort, damit niemand in der Oberfläche aus
    /// <see cref="CompanySearchOutcome.Undetermined"/> doch wieder „keine Firma gefunden“ macht.
    /// </remarks>
    public string Explanation { get; init; } = string.Empty;

    /// <summary>Wurde die Liste für die Anzeige gekürzt?</summary>
    /// <remarks>
    /// Die Ausgeblendeten zählen hier ausdrücklich <b>nicht</b> als gekürzt: Gekürzt heißt „es
    /// gibt mehr, die hierher gehören“ und ist mit einem längeren Suchbegriff zu heilen.
    /// Ausgeblendet heißt „es gibt mehr, die nicht hierher gehören“ — daran ändert kein
    /// Suchbegriff etwas.
    /// </remarks>
    public bool Truncated => TotalFound - HiddenInactive > Companies.Count;

    /// <summary>Wie viele der angezeigten Firmen tatsächlich gewählt werden dürfen.</summary>
    public int SelectableCount => Companies.Count(company => company.Selectable);

    /// <summary>Ist die Aussage belastbar? <see langword="false"/> heißt: nicht ermittelt.</summary>
    public bool IsCertain => Outcome != CompanySearchOutcome.Undetermined;
}
