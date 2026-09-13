using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace TanssLogWatcher.Storage.Config;

/// <summary>
/// Eine einzelne Adresse oder ein Netz in CIDR-Schreibweise, wie sie in
/// <c>exclude_ip_addresses</c> stehen dürfen.
/// </summary>
/// <remarks>
/// <para>Der Ausschluss ist die wichtigste Stellschraube gegen Fehlbuchungen: Ohne ihn
/// erzeugt jede RDP-Sitzung auf den eigenen Terminalserver eine Fernwartung beim Kunden.
/// Deshalb wird jede Angabe schon beim Laden der Konfiguration geprüft und nicht erst,
/// wenn zur Laufzeit eine Sitzung dagegen gehalten wird — ein Tippfehler in einer Maske
/// würde sonst wochenlang unbemerkt jede Sitzung durchlassen.</para>
///
/// <para>Gesetzte Bits unterhalb der Präfixlänge werden beim Einlesen <b>abgeschnitten</b>:
/// <c>192.168.1.7/24</c> ist also gleichbedeutend mit <c>192.168.1.0/24</c>. Das entspricht
/// der verbreiteten Erwartung und ist gutmütiger, als eine sonst richtige Zeile abzulehnen.</para>
///
/// <para>Ein Vergleich über Adressfamilien hinweg ergibt immer <c>false</c>. Ein
/// IPv4-Ausschluss deckt also keine IPv6-Verbindung ab — wer beides meint, trägt beides ein.</para>
/// </remarks>
public sealed class IpRange : IEquatable<IpRange>
{
    private readonly byte[] _network;

    private IpRange(IPAddress network, int prefixLength)
    {
        Network = network;
        PrefixLength = prefixLength;
        _network = network.GetAddressBytes();
    }

    /// <summary>Die Netzadresse, bereits auf die Präfixlänge maskiert.</summary>
    public IPAddress Network { get; }

    /// <summary>Länge des Präfixes in Bit. 32 bei einer einzelnen IPv4-Adresse, 128 bei IPv6.</summary>
    public int PrefixLength { get; }

    /// <summary>Adressfamilie dieses Bereichs.</summary>
    public AddressFamily Family => Network.AddressFamily;

    /// <summary>Liest eine Adresse oder ein Netz. Wirft bei fehlerhafter Angabe.</summary>
    public static IpRange Parse(string text) =>
        TryParse(text, out IpRange? range)
            ? range
            : throw new FormatException(
                $"„{text}“ ist weder eine IP-Adresse noch ein Netz in CIDR-Schreibweise " +
                "(erwartet etwa 10.0.0.5, 192.168.0.0/16 oder fe80::/10).");

    /// <summary>Liest eine Adresse oder ein Netz, ohne bei Fehleingabe zu werfen.</summary>
    public static bool TryParse(string? text, [NotNullWhen(true)] out IpRange? range)
    {
        range = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string trimmed = text.Trim();
        int slash = trimmed.IndexOf('/', StringComparison.Ordinal);
        string addressPart = slash < 0 ? trimmed : trimmed[..slash];

        if (!IPAddress.TryParse(addressPart, out IPAddress? address))
        {
            return false;
        }

        int bits = address.AddressFamily == AddressFamily.InterNetwork ? 32 : 128;
        int prefix = bits;

        if (slash >= 0)
        {
            string prefixPart = trimmed[(slash + 1)..];
            if (!int.TryParse(prefixPart, NumberStyles.None, CultureInfo.InvariantCulture, out prefix)
                || prefix < 0 || prefix > bits)
            {
                return false;
            }
        }

        range = new IpRange(new IPAddress(Mask(address.GetAddressBytes(), prefix)), prefix);
        return true;
    }

    /// <summary>
    /// Zerlegt eine getrennte Liste und prüft <b>jeden</b> Eintrag für sich.
    /// </summary>
    /// <remarks>
    /// <para><b>Jeder Eintrag einzeln, und beim ersten Fehler Schluss.</b> Eine Liste wie
    /// <c>10.0.0.0/8; 192.168.0.999; 172.16.0.0/12</c> darf nicht zu zwei übernommenen und
    /// einem verworfenen Netz führen: Der Benutzer sähe zwei Drittel seiner Eingabe
    /// wiederkehren und hielte das für vollständig. Entweder die ganze Liste ist lesbar, oder
    /// es wird nichts übernommen und der schuldige Eintrag benannt.</para>
    /// <para>Leere Abschnitte fallen weg. Wer eine Liste tippt, setzt gewohnheitsmässig ein
    /// Trennzeichen hinter den letzten Eintrag; daraus einen Fehler zu machen wäre Schikane.</para>
    /// <para>Der Rückgabewert ist die getrimmte Ursprungsschreibweise, nicht der geparste
    /// Bereich: In <c>config.json</c> soll stehen, was der Benutzer eingetragen hat —
    /// <c>192.168.1.7/24</c> und nicht das daraus maskierte <c>192.168.1.0/24</c>.</para>
    /// </remarks>
    /// <param name="text">Die Liste; <c>null</c> oder leer ergibt eine leere Liste.</param>
    /// <param name="separator">Das Trennzeichen, üblicherweise das Semikolon.</param>
    /// <param name="entries">Die geprüften Einträge; leer, wenn einer nicht lesbar war.</param>
    /// <param name="invalid">Der erste unlesbare Eintrag, oder <c>null</c>.</param>
    /// <returns><c>true</c>, wenn jeder Eintrag eine Adresse oder ein Netz ist.</returns>
    public static bool TrySplit(string? text, char separator,
                                out IReadOnlyList<string> entries,
                                [NotNullWhen(false)] out string? invalid)
    {
        List<string> parsed = [];

        foreach (string part in (text ?? string.Empty).Split(
                     separator,
                     StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            if (!TryParse(part, out _))
            {
                entries = [];
                invalid = part;
                return false;
            }

            parsed.Add(part);
        }

        entries = parsed;
        invalid = null;
        return true;
    }

    /// <summary>Liegt <paramref name="address"/> in diesem Bereich?</summary>
    public bool Contains(IPAddress? address)
    {
        if (address is null || address.AddressFamily != Family)
        {
            return false;
        }

        byte[] candidate = Mask(address.GetAddressBytes(), PrefixLength);
        return candidate.AsSpan().SequenceEqual(_network);
    }

    /// <summary>Liegt die als Text angegebene Adresse in diesem Bereich?</summary>
    public bool Contains(string? address) =>
        IPAddress.TryParse(address, out IPAddress? parsed) && Contains(parsed);

    private static byte[] Mask(byte[] bytes, int prefixLength)
    {
        for (int index = 0; index < bytes.Length; index++)
        {
            int remaining = prefixLength - (index * 8);
            if (remaining >= 8)
            {
                continue;
            }

            bytes[index] = remaining <= 0
                ? (byte)0
                : (byte)(bytes[index] & (0xFF << (8 - remaining)));
        }

        return bytes;
    }

    /// <inheritdoc/>
    public bool Equals(IpRange? other) =>
        other is not null && PrefixLength == other.PrefixLength && Network.Equals(other.Network);

    /// <inheritdoc/>
    public override bool Equals(object? obj) => Equals(obj as IpRange);

    /// <inheritdoc/>
    public override int GetHashCode() => HashCode.Combine(Network, PrefixLength);

    /// <inheritdoc/>
    public override string ToString() =>
        string.Create(CultureInfo.InvariantCulture, $"{Network}/{PrefixLength}");
}
