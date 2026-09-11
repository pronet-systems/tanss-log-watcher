using System.Net;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Windows.Win32;
using Windows.Win32.NetworkManagement.IpHelper;

namespace TanssLogWatcher.Monitoring.Native;

/// <summary>
/// Die offenen TCP-Verbindungen über <c>GetExtendedTcpTable</c>.
/// </summary>
/// <remarks>
/// <para><b>Halb geschlossene Verbindungen zählen nicht.</b> <c>TIME_WAIT</c> hält eine Gegenstelle
/// nach dem Trennen noch bis zu vier Minuten in der Tabelle, <c>CLOSE_WAIT</c> unbegrenzt lange.
/// Die Vorlage filterte den Zustand nicht und schrieb deshalb Fernwartungen, die Minuten nach dem
/// tatsächlichen Ende weiterliefen.</para>
/// <para>Nur IPv4 — wie in der Vorlage. Für IPv6 wäre <c>AF_INET6</c> ein zweiter Aufruf; solange
/// keine der überwachten Anwendungen darüber arbeitet, bleibt der Aufwand ungerechtfertigt.</para>
/// </remarks>
public sealed class TcpConnectionSource : ITcpConnectionSource
{
    private const uint AfInet = 2;
    private const uint ErrorInsufficientBuffer = 122;
    private const uint NoError = 0;

    /// <summary>Wie oft ein Anlauf wiederholt wird, wenn die Tabelle zwischenzeitlich wächst.</summary>
    private const int MaxAttempts = 4;

    private readonly ILogger<TcpConnectionSource> logger;

    /// <summary>Erzeugt die Quelle.</summary>
    /// <param name="logger">Protokoll; <c>null</c> schaltet es ab.</param>
    public TcpConnectionSource(ILogger<TcpConnectionSource>? logger = null) =>
        this.logger = logger ?? NullLogger<TcpConnectionSource>.Instance;

    /// <inheritdoc />
    public IReadOnlyList<IPAddress> GetRemoteAddresses(int processId, IEnumerable<int> childIds)
    {
        ArgumentNullException.ThrowIfNull(childIds);

        HashSet<uint> owners = [(uint)processId];
        foreach (int child in childIds)
        {
            owners.Add((uint)child);
        }

        List<IPAddress> result = [];
        foreach (Connection connection in ReadTable())
        {
            if (!owners.Contains(connection.OwningPid))
            {
                continue;
            }

            if (connection.State is MIB_TCP_STATE.MIB_TCP_STATE_TIME_WAIT
                                 or MIB_TCP_STATE.MIB_TCP_STATE_CLOSE_WAIT)
            {
                continue;
            }

            IPAddress address = new((long)connection.RemoteAddress);
            if (!IpFilter.IsUninteresting(address))
            {
                result.Add(address);
            }
        }

        return result;
    }

    private unsafe List<Connection> ReadTable()
    {
        List<Connection> connections = [];
        uint size = 0;

        uint status = PInvoke.GetExtendedTcpTable(null, ref size, false, AfInet,
                                                  TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);

        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (status != ErrorInsufficientBuffer && status != NoError)
            {
                logger.LogWarning("Die TCP-Tabelle ist nicht lesbar (Fehler {Status}).", status);
                return connections;
            }

            if (size == 0)
            {
                return connections;
            }

            byte[] buffer = new byte[size];
            fixed (byte* raw = buffer)
            {
                status = PInvoke.GetExtendedTcpTable(raw, ref size, false, AfInet,
                                                      TCP_TABLE_CLASS.TCP_TABLE_OWNER_PID_ALL, 0);
                if (status == ErrorInsufficientBuffer)
                {
                    // Zwischen Groessenabfrage und Abholen kam eine Verbindung hinzu. Neuer Anlauf
                    // mit der jetzt gemeldeten Groesse.
                    continue;
                }

                if (status != NoError)
                {
                    logger.LogWarning("Die TCP-Tabelle ist nicht lesbar (Fehler {Status}).", status);
                    return connections;
                }

                MIB_TCPTABLE_OWNER_PID* table = (MIB_TCPTABLE_OWNER_PID*)raw;
                int count = (int)table->dwNumEntries;
                if (count <= 0)
                {
                    return connections;
                }

                // Gegenprobe gegen den gemeldeten Umfang: eine zu grosse Zahl wuerde sonst ueber
                // das Ende des Puffers hinaus lesen.
                int capacity = (int)((size - sizeof(uint)) / sizeof(MIB_TCPROW_OWNER_PID));
                if (count > capacity)
                {
                    count = capacity;
                }

                Span<MIB_TCPROW_OWNER_PID> rows = table->table.AsSpan(count);
                connections.Capacity = count;
                foreach (ref readonly MIB_TCPROW_OWNER_PID row in rows)
                {
                    connections.Add(new Connection(row.dwOwningPid, row.dwRemoteAddr, row.dwState));
                }

                return connections;
            }
        }

        logger.LogWarning("Die TCP-Tabelle wuchs schneller, als sie gelesen werden konnte.");
        return connections;
    }

    private readonly record struct Connection(uint OwningPid, uint RemoteAddress, MIB_TCP_STATE State);
}
