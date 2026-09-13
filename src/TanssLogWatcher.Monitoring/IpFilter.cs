using System.Net;
using System.Net.Sockets;

namespace TanssLogWatcher.Monitoring;

/// <summary>
/// Prüft Adressen gegen Ausschlusslisten.
/// </summary>
public static class IpFilter
{
    /// <summary>Das Trennzeichen der benutzereigenen Ausschlussliste.</summary>
    public const char ExcludeSeparator = ';';

    /// <summary>
    /// Prüft, ob eine Adresse in einem CIDR-Bereich liegt.
    /// </summary>
    /// <remarks>
    /// <b>Jede Eingabe ist Benutzereingabe und daher unzuverlässig.</b> Ein Zugriff ohne
    /// Bereichsprüfung auf <c>bytes[prefix / 8]</c> wirft bei einem Tippfehler wie
    /// <c>10.0.0.0/40</c> in jedem Durchlauf eine Ausnahme und legt — zusammen mit dem stummen
    /// <c>catch</c> um die Schleife — die gesamte Erkennung dauerhaft lahm. Hier gilt: alles, was
    /// nicht eindeutig passt, ergibt <c>false</c>.
    /// <para>Adressen unterschiedlicher Familien passen nie zueinander; eine IPv6-Adresse gegen
    /// einen IPv4-Bereich ergibt <c>false</c> statt eines Fehlers.</para>
    /// </remarks>
    /// <param name="address">Die zu prüfende Adresse.</param>
    /// <param name="cidr">Der Bereich in der Schreibweise <c>10.0.0.0/8</c>.</param>
    public static bool IsInSubnet(IPAddress? address, string? cidr)
    {
        if (address is null || string.IsNullOrWhiteSpace(cidr))
        {
            return false;
        }

        string[] parts = cidr.Split('/');
        if (parts.Length != 2)
        {
            return false;
        }

        if (!IPAddress.TryParse(parts[0].Trim(), out IPAddress? network))
        {
            return false;
        }

        if (!int.TryParse(parts[1].Trim(), System.Globalization.NumberStyles.None,
                          System.Globalization.CultureInfo.InvariantCulture, out int prefixLength))
        {
            return false;
        }

        byte[] addressBytes = address.GetAddressBytes();
        byte[] networkBytes = network.GetAddressBytes();
        if (addressBytes.Length != networkBytes.Length)
        {
            return false;
        }

        if (prefixLength < 0 || prefixLength > addressBytes.Length * 8)
        {
            return false;
        }

        int wholeBytes = prefixLength / 8;
        int remainingBits = prefixLength % 8;

        for (int i = 0; i < wholeBytes; i++)
        {
            if (addressBytes[i] != networkBytes[i])
            {
                return false;
            }
        }

        if (remainingBits == 0)
        {
            return true;
        }

        int mask = (byte)~(255 >> remainingBits);
        return (addressBytes[wholeBytes] & mask) == (networkBytes[wholeBytes] & mask);
    }

    /// <summary>
    /// Prüft eine Adresse gegen die benutzereigene Ausschlussliste.
    /// </summary>
    /// <remarks>
    /// Jeder mit Semikolon getrennte Eintrag wird zuerst als einzelne Adresse und danach als
    /// CIDR-Bereich gelesen. Unlesbare Einträge werden übergangen, nicht gemeldet: eine
    /// Ausschlussliste, die wegen eines Tippfehlers gar nichts mehr ausschließt, wäre schlimmer
    /// als eine, die einen Eintrag verliert.
    /// </remarks>
    /// <param name="address">Die zu prüfende Adresse.</param>
    /// <param name="excludeList">Die Liste, etwa <c>10.0.0.0/8;192.168.2.1</c>.</param>
    public static bool IsExcluded(IPAddress? address, string? excludeList)
    {
        if (address is null || string.IsNullOrWhiteSpace(excludeList))
        {
            return false;
        }

        foreach (string entry in excludeList.Split(ExcludeSeparator, StringSplitOptions.RemoveEmptyEntries |
                                                                    StringSplitOptions.TrimEntries))
        {
            if (IPAddress.TryParse(entry, out IPAddress? single) && single.Equals(address))
            {
                return true;
            }

            if (IsInSubnet(address, entry))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Wahr für Adressen, die niemals eine Gegenstelle einer Fernwartung sein können.
    /// </summary>
    /// <param name="address">Die zu prüfende Adresse.</param>
    public static bool IsUninteresting(IPAddress? address) =>
        address is null
        || address.Equals(IPAddress.Any)
        || address.Equals(IPAddress.None)
        || address.Equals(IPAddress.IPv6Any)
        || IPAddress.IsLoopback(address)
        || (address.AddressFamily == AddressFamily.InterNetwork &&
            address.GetAddressBytes() is [255, 255, 255, 255]);
}
