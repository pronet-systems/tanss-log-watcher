using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace TanssLogWatcher.Storage.Secrets;

/// <summary>
/// Ein kurzer Abdruck, der denselben Text wiedererkennbar macht, ohne ihn preiszugeben —
/// HMAC-SHA256 unter einem je Installation einmalig erzeugten Zufallsschlüssel.
/// </summary>
/// <remarks>
/// <para><b>Warum nicht der schlichte Hash aus <see cref="SecretFingerprint"/>.</b> Der ist
/// ungesalzen. Für ein Token mit hoher Entropie genügt das, für eine <i>ratbare</i> Angabe
/// nicht: Eine Fensterbeschriftung lautet „RDP: kunde-srv01“ oder trägt einen Rechnernamen
/// aus dem eigenen Bestand. Wer <c>state.db</c> in die Hand bekommt und eine Liste möglicher
/// Beschriftungen hat, rechnet jeden Kandidaten durch und vergleicht — der Abdruck ist dann
/// keine Verschleierung, sondern eine <b>bestätigbare Festlegung</b>. Mit einem Schlüssel,
/// der nur auf diesem Rechner und nur für diesen Windows-Benutzer entschlüsselbar liegt,
/// scheitert dieses Durchrechnen: Der Angreifer kann den Abdruck nicht nachbilden.</para>
///
/// <para><b>Was der Abdruck weiterhin leistet.</b> Derselbe Text ergibt unter demselben
/// Schlüssel denselben Abdruck. Genau das ist sein Zweck: Die Frage „ist das dasselbe
/// Fenster wie vorhin“ bleibt über mehrere Protokolleinträge hinweg beantwortbar. Über
/// <i>Installationsgrenzen</i> hinweg ist sie es nicht mehr — zwei Rechner führen
/// verschiedene Schlüssel und damit verschiedene Abdrücke desselben Textes. Das ist kein
/// Verlust, sondern dieselbe Eigenschaft von der anderen Seite gesehen.</para>
///
/// <para>Acht Hexziffern sind absichtlich wenig. Sie reichen, um zwei Beschriftungen
/// auseinanderzuhalten, und sind zu kurz, um als Ausgangspunkt für irgendetwas zu taugen.
/// Gelegentliche Kollisionen sind hingenommen: „vermutlich dasselbe Fenster“ ist die
/// Aussage, die hier gebraucht wird.</para>
/// </remarks>
public sealed class KeyedFingerprint
{
    /// <summary>Länge des Schlüssels in Bytes.</summary>
    /// <remarks>
    /// Entspricht der Blocklänge von SHA-256. Ein längerer Schlüssel würde von HMAC zuerst
    /// gehasht und brächte nichts, ein kürzerer verschenkte Entropie.
    /// </remarks>
    public const int KeySizeBytes = 32;

    /// <summary>Was ein leerer Wert ergibt — derselbe Text wie bei <see cref="SecretFingerprint"/>.</summary>
    public const string EmptyMarker = "(leer)";

    private readonly byte[] _key;

    /// <summary>Bindet den Abdruck an einen Schlüssel.</summary>
    /// <param name="key">Genau <see cref="KeySizeBytes"/> Bytes.</param>
    /// <remarks>
    /// Der Schlüssel wird kopiert. Andernfalls könnte der Aufrufer ihn später überschreiben
    /// — und dann änderte sich der Abdruck derselben Beschriftung mitten im Betrieb, ohne
    /// dass jemand die Ursache sähe.
    /// </remarks>
    public KeyedFingerprint(ReadOnlySpan<byte> key)
    {
        if (key.Length != KeySizeBytes)
        {
            throw new StorageException(
                $"Der Schlüssel für Abdrücke hat {key.Length} statt {KeySizeBytes} Bytes. "
                + "Die Schlüsseldatei stammt üblicherweise aus einer anderen Programmfassung "
                + "oder wurde beschädigt. Sie darf gelöscht werden: Der nächste Start legt "
                + "einen neuen Schlüssel an; nur die Abdrücke bereits geschriebener "
                + "Protokolleinträge passen dann nicht mehr zu den neuen.");
        }

        _key = key.ToArray();
    }

    /// <summary>Erzeugt einen frischen Zufallsschlüssel.</summary>
    public static KeyedFingerprint CreateRandom()
    {
        byte[] key = RandomNumberGenerator.GetBytes(KeySizeBytes);
        try
        {
            return new KeyedFingerprint(key);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key);
        }
    }

    /// <summary>Der Abdruck. Ein leerer Wert ergibt <see cref="EmptyMarker"/>.</summary>
    public string Of(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return EmptyMarker;
        }

        byte[] mac = HMACSHA256.HashData(_key, Encoding.UTF8.GetBytes(value));
        return Convert.ToHexString(mac.AsSpan(0, 4)).ToLower(CultureInfo.InvariantCulture);
    }

    /// <summary>Gibt den Schlüssel heraus, damit er versiegelt werden kann.</summary>
    /// <remarks>
    /// Nur für <see cref="FingerprintKeyStore"/>. Der Aufrufer soll den gelieferten Puffer
    /// nach Gebrauch mit <see cref="CryptographicOperations.ZeroMemory"/> überschreiben.
    /// </remarks>
    internal byte[] ExportKey() => (byte[])_key.Clone();
}
