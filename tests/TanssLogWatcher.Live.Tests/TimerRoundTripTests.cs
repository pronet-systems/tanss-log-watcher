using System.Globalization;
using System.Text.Json;
using Xunit;

namespace TanssLogWatcher.Live.Tests;

/// <summary>
/// Der Timer-Umlauf gegen die echte Instanz: anlegen, umschalten, löschen.
/// </summary>
/// <remarks>
/// <para><b>Der einzige schreibende Test dieses Projekts</b> — und er räumt hinter sich auf.
/// Timer eignen sich dafür, weil sie sich restlos löschen lassen; eine Fernwartung nicht: die
/// wäre gebuchte Arbeitszeit beim Kunden und bliebe stehen.</para>
///
/// <para>Geprüft werden drei Annahmen, die das Werkzeug über diese Routen trifft und die sich
/// gegen eine Attrappe nicht prüfen lassen: dass das Anlegen einen <b>laufenden</b> Timer
/// ergibt, dass es keinen getrennten Start und Stop gibt, sondern einen Umschalter, und dass
/// das Löschen die Kennung im <b>Rumpf</b> erwartet und nicht im Pfad.</para>
///
/// <para>Läuft nur mit <c>TANSS_LIVE_WRITES=1</c>. Ohne die Freigabe wird übersprungen.</para>
/// </remarks>
public sealed class TimerRoundTripTests
{
    [LiveWriteFact]
    public async Task Anlegen_Umschalten_Loeschen_laeuft_durch_und_raeumt_auf()
    {
        LiveSession session = await LiveInstance.GetAsync();
        string query = $"?loggedInUserId={session.EmployeeId}";
        int timerId = 0;

        try
        {
            // 1. Anlegen. TANSS setzt Startzeit und Dauer selbst - ein neuer Timer laeuft.
            (int created, string body) = await session.SendAsync(HttpMethod.Post,
                "/api/v1/timers" + query, session.MintedToken,
                """{"title":"Log-Watcher Selbsttest","ticketId":0,"companyId":0,"callbackId":0,"linkTypeId":0,"linkId":0}""");

            Assert.Equal(201, created);

            using (JsonDocument document = JsonDocument.Parse(body))
            {
                JsonElement content = document.RootElement.GetProperty("content");
                timerId = content.GetProperty("id").GetInt32();

                Assert.True(timerId > 0, "TANSS lieferte keine Timer-Kennung.");

                // Die Annahme hinter TanssTimer.IsRunning: Startzeit ungleich null heisst laufend.
                Assert.NotEqual(0, content.GetProperty("startTime").GetInt64());
            }

            // 2. Umschalten. Es gibt kein Start und kein Stop, nur diesen einen Weg.
            (int toggled, string afterToggle) = await session.SendAsync(HttpMethod.Put,
                string.Create(CultureInfo.InvariantCulture, $"/api/v1/timers/{timerId}{query}"),
                session.MintedToken);

            Assert.True(toggled is 200 or 202,
                $"Das Umschalten antwortete mit {toggled}.");

            using (JsonDocument document = JsonDocument.Parse(afterToggle))
            {
                JsonElement content = document.RootElement.GetProperty("content");

                // Angehalten: Startzeit zurueck auf null, die gelaufene Zeit steht in duration.
                Assert.Equal(0, content.GetProperty("startTime").GetInt64());
            }
        }
        finally
        {
            if (timerId > 0)
            {
                // Aufraeumen im finally: Auch ein gescheiterter Test soll nichts hinterlassen.
                // Die Kennung gehoert in den RUMPF - eine Route, die sie als Abfrageparameter
                // naehme, gibt es nicht.
                (int deleted, _) = await session.SendAsync(HttpMethod.Delete,
                    "/api/v1/timers" + query, session.MintedToken,
                    string.Create(CultureInfo.InvariantCulture, $$"""{"id":{{timerId}}}"""));

                Assert.True(deleted is 200 or 204,
                    $"Das Löschen antwortete mit {deleted}. In der Instanz bleibt Timer "
                    + $"{timerId} stehen und ist von Hand zu entfernen.");
            }
        }
    }
}
