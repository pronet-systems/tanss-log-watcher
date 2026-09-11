using System.Text.Json;
using TanssLogWatcher.Api.Http;
using TanssLogWatcher.Api.Model;
using Xunit;

namespace TanssLogWatcher.Api.Tests;

/// <summary>Prüft, dass nichts verloren geht und Zeit Sekunden bleibt.</summary>
public sealed class SerializationTests
{
    private static RemoteSupportWrite Minimal => new()
    {
        TypeId = 1003,
        EmployeeId = 42,
        StartTime = 1_757_500_000,
        EndTime = 0,
        RemoteMaintenanceId = "6f1b0e4a-0000-4000-8000-000000000001",
    };

    [Fact]
    public void Fernwartung_Serialisiert_Auch_Felder_Mit_Standardwert()
    {
        // TANSS unterscheidet "Feld fehlt" von "Feld ist 0". Ein weggelassenes endTime ist
        // nicht dasselbe wie endTime = 0 (SQL NULL, "laeuft noch").
        using JsonDocument document = JsonDocument.Parse(TanssJson.Serialize(Minimal));
        JsonElement root = document.RootElement;

        string[] expected =
        [
            "typeId", "employeeId", "startTime", "endTime", "remoteMaintenanceId", "comment",
            "ticketId", "userId", "userName", "deviceId", "deviceName",
        ];

        foreach (string name in expected)
        {
            Assert.True(root.TryGetProperty(name, out _), $"Das Feld {name} fehlt im Rumpf.");
        }

        Assert.Equal(expected.Length, root.EnumerateObject().Count());
        Assert.Equal(0L, root.GetProperty("endTime").GetInt64());
        Assert.Equal(0, root.GetProperty("ticketId").GetInt32());
        Assert.Equal(string.Empty, root.GetProperty("comment").GetString());
        Assert.Equal(string.Empty, root.GetProperty("deviceName").GetString());
    }

    [Fact]
    public void Verbotene_Felder_Werden_Nicht_Gesendet()
    {
        // id, fee und typeName stehen bewusst nicht im Modell - id koennte serverseitig auf
        // eine bestehende Zeile binden.
        using JsonDocument document = JsonDocument.Parse(TanssJson.Serialize(Minimal));

        Assert.False(document.RootElement.TryGetProperty("id", out _));
        Assert.False(document.RootElement.TryGetProperty("fee", out _));
        Assert.False(document.RootElement.TryGetProperty("typeName", out _));
    }

    [Fact]
    public void Ein_Als_Object_Uebergebener_Rumpf_Bleibt_Vollstaendig()
    {
        // Serialize<object> wuerde ein leeres {} schreiben - eine Fernwartung ohne Inhalt,
        // die TANSS anstandslos mit 201 quittierte.
        object body = Minimal;

        string json = TanssJson.Serialize(body);

        Assert.Contains("remoteMaintenanceId", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Antworten_Werden_Ohne_Ruecksicht_Auf_Grossschreibung_Gelesen()
    {
        const string json = """{"ID":7,"RemoteMaintenanceId":"abc","TypeId":1003}""";

        RemoteSupportRead? read = JsonSerializer.Deserialize<RemoteSupportRead>(json, TanssJson.Options);

        Assert.NotNull(read);
        Assert.Equal(7, read.Id);
        Assert.Equal("abc", read.RemoteMaintenanceId);
    }

    [Fact]
    public void Zeit_Geht_Hin_Und_Zurueck()
    {
        DateTimeOffset moment = new(2026, 9, 11, 14, 30, 15, TimeSpan.Zero);

        long seconds = TanssTime.ToUnixSeconds(moment);

        Assert.Equal(1_789_137_015L, seconds);
        Assert.Equal(moment, TanssTime.FromUnixSeconds(seconds));
    }

    [Fact]
    public void Null_Bedeutet_Nicht_Gesetzt()
    {
        Assert.Null(TanssTime.FromUnixSeconds(0));
    }

    [Fact]
    public void Sekunden_Sind_Keine_Millisekunden()
    {
        // Millisekunden erzeugen kein Scheitern, sondern ein Datum im Jahr 51667. Genau
        // deshalb gibt es nur diesen einen Umrechner.
        DateTimeOffset moment = new(2026, 9, 11, 14, 30, 15, TimeSpan.Zero);

        long seconds = TanssTime.ToUnixSeconds(moment);

        Assert.True(seconds < 10_000_000_000L);
        Assert.Equal(2026, TanssTime.FromUnixSeconds(seconds)?.Year);

        // Derselbe Zeitpunkt in Millisekunden liegt so weit jenseits jedes Datums, dass ihn
        // .NET zurueckweist - TANSS dagegen speichert daraus klaglos das Jahr 51667.
        Assert.Throws<ArgumentOutOfRangeException>(
            () => TanssTime.FromUnixSeconds(moment.ToUnixTimeMilliseconds()));
    }

    [Fact]
    public void Ein_Laufender_Timer_Erkennt_Sich_An_Der_Startzeit()
    {
        // TANSS liefert kein Feld isRunning; der Zustand steckt in startTime.
        TanssTimer running = new() { StartTime = 1_757_500_000, Duration = 60 };
        TanssTimer stopped = new() { StartTime = 0, Duration = 60 };

        Assert.True(running.IsRunning);
        Assert.False(stopped.IsRunning);
        Assert.Equal(TimeSpan.FromSeconds(60), stopped.Elapsed(DateTimeOffset.UtcNow));
        Assert.Equal(TimeSpan.FromSeconds(160),
                     running.Elapsed(DateTimeOffset.FromUnixTimeSeconds(1_757_500_100)));
    }
}
