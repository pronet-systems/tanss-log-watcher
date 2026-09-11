using Microsoft.Data.Sqlite;
using TanssLogWatcher.Api.Model;
using TanssLogWatcher.Storage.Config;

namespace TanssLogWatcher.Storage.Tests;

/// <summary>
/// Ein echtes temporäres Verzeichnis. Wird am Ende jedes Falls wieder entfernt.
/// </summary>
/// <remarks>
/// Hier wird bewusst weder das Dateisystem noch SQLite nachgebildet. Genau die Eigenschaften,
/// auf die es ankommt — das Umbenennen als unteilbarer Schritt, das Sperrverhalten von
/// SQLite, das Verhalten von DPAPI —, hat eine Attrappe nicht. Ein Test dagegen prüfte nur
/// die Attrappe.
/// </remarks>
internal sealed class TempDirectory : IDisposable
{
    public TempDirectory()
    {
        Path = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tanss-logwatch-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string File(string name) => System.IO.Path.Combine(Path, name);

    public void Dispose()
    {
        try
        {
            Directory.Delete(Path, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}

/// <summary>Eine Uhr, die nur vorgestellt wird, wenn der Test es sagt.</summary>
/// <remarks>
/// Fälligkeit, Rückstau und das Zurückholen hängengebliebener Einträge sind Aussagen über
/// Zeit. Ohne stellbare Uhr ließen sie sich nur durch Warten prüfen — also gar nicht.
/// </remarks>
internal sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan amount) => _now += amount;
}

/// <summary>Vorlagen für die Testfälle.</summary>
internal static class Sample
{
    public static AppConfig Config() => new()
    {
        Tanss = new TanssSection
        {
            BaseUrl = "https://tanss.kunde.de/backend",
            EmployeeId = 1,
        },
        Monitoring =
        [
            new MonitoringEntry
            {
                Key = "putty",
                RemoteSupportTypeId = 1003,
                ExcludeIpAddresses = ["10.0.0.0/8", "192.168.1.5"],
            },
        ],
    };

    public static RemoteSupportWrite Upload(string id, long start = 1_757_000_000) => new()
    {
        TypeId = 1003,
        EmployeeId = 1,
        StartTime = start,
        EndTime = start + 600,
        RemoteMaintenanceId = id,
        Comment = "Putty auf kunde-srv01",
        DeviceName = "kunde-srv01",
    };
}

/// <summary>
/// Zugriff auf die Zustandsdatenbank an der Anwendung vorbei.
/// </summary>
/// <remarks>
/// Damit lässt sich ein Datenbestand herstellen, den die Anwendung selbst nie erzeugen
/// würde: eine zerstörte Nutzlast, ein Wert, der nicht zum Spaltentyp passt, eine Datei vom
/// vorigen Stand. Genau diese Fälle entscheiden, ob eine einzelne kaputte Zeile die
/// Warteschlange anhält — und ohne sie ließe sich das nur behaupten.
/// </remarks>
internal static class Raw
{
    public static void Execute(string path, string sql)
    {
        using SqliteConnection connection = Open(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        _ = command.ExecuteNonQuery();
    }

    public static string? Text(string path, string sql)
    {
        using SqliteConnection connection = Open(path);
        using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? value = command.ExecuteScalar();
        return value is null or DBNull ? null : Convert.ToString(value,
            System.Globalization.CultureInfo.InvariantCulture);
    }

    private static SqliteConnection Open(string path)
    {
        SqliteConnectionStringBuilder builder = new() { DataSource = path, Pooling = false };
        SqliteConnection connection = new(builder.ToString());
        connection.Open();
        return connection;
    }
}
