using System;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Winisland.Services;

namespace Winisland;

// Runs only within the existing guarded self-test. HTTP fixtures are in-memory
// and organizer data lives beneath that run's isolated output directory.
internal static partial class DataBugVerification
{
    internal static async Task RunAsync(string output, Action<bool, string> check)
    {
        var calendar = "BEGIN:VCALENDAR\r\nVERSION:2.0\r\nBEGIN:VEVENT\r\nUID:quoted-parameters\r\n"
            + "DTSTART;TZID=\"W. Europe Standard Time\":20270312T143000\r\nDURATION:PT45M\r\n"
            + "SUMMARY;LANGUAGE=en:Quoted parameter review\r\n"
            + "DESCRIPTION;ALTREP=\"https://example.test/notes;section=1\":Actual notes\\nSecond line\r\n"
            + "END:VEVENT\r\nEND:VCALENDAR\r\n";
        var parsed = CalendarInterchange.Parse(calendar);
        var zone = TimeZoneInfo.FindSystemTimeZoneById("W. Europe Standard Time");
        var wallTime = new DateTime(2027, 3, 12, 14, 30, 0, DateTimeKind.Unspecified);
        check(parsed.Skipped == 0 && parsed.Entries.Single().Start == new DateTimeOffset(wallTime, zone.GetUtcOffset(wallTime))
            && parsed.Entries.Single().Description == "Actual notes\nSecond line",
            "Quoted calendar timezone and URI parameters preserve the actual event time and description");
        var cancelled = CalendarInterchange.Parse(calendar.Replace("END:VEVENT", "STATUS:CANCELLED\r\nEND:VEVENT"));
        check(cancelled.Entries.Count == 0 && cancelled.Skipped == 1,
            "Cancelled iCalendar entries are reported as skipped instead of becoming active agenda events");
        var directory = Path.Combine(output, "data-bugs-" + Guid.NewGuid().ToString("N"));
        var store = new OrganizerStore(directory);
        check(store.ImportEvents(parsed.Entries) == 1
            && new OrganizerStore(directory).Events.Single().Description == "Actual notes\nSecond line",
            "Imported quoted-parameter notes persist and reload without URI header contamination");

        foreach (var offset in new[] { TimeSpan.FromHours(14), TimeSpan.FromHours(-12) })
        {
            var start = new DateTimeOffset(2027, 3, 12, 0, 0, 0, offset);
            var allDay = new CalendarEntry(Guid.NewGuid(), "Civil date " + offset, start, 2880, AllDay: true);
            store.ImportEvents(new[] { allDay });
            check(store.EventsOn(new DateOnly(2027, 3, 12)).Any(e => e.Id == allDay.Id)
                && store.EventsOn(new DateOnly(2027, 3, 13)).Any(e => e.Id == allDay.Id)
                && !store.EventsOn(new DateOnly(2027, 3, 11)).Any(e => e.Id == allDay.Id)
                && !store.EventsOn(new DateOnly(2027, 3, 14)).Any(e => e.Id == allDay.Id),
                "All-day agenda entries preserve their two civil dates regardless of their saved timezone offset");
            var exported = CalendarInterchange.Export(new[] { allDay });
            check(exported.Contains("DTSTART;VALUE=DATE:20270312\r\n")
                && exported.Contains("DTEND;VALUE=DATE:20270314\r\n"),
                "All-day calendar exports preserve the original civil start and exclusive end dates");
            var reloaded = new OrganizerStore(directory).Events.Single(e => e.Id == allDay.Id);
            check(DateOnly.FromDateTime(reloaded.Start.DateTime) == new DateOnly(2027, 3, 12)
                && new OrganizerStore(directory).EventsOn(new DateOnly(2027, 3, 12)).Any(e => e.Id == allDay.Id),
                "All-day calendar civil dates survive persistence even with a foreign saved timezone offset");
        }

        Directory.CreateDirectory(directory);
        var file = Path.Combine(directory, "upload.txt");
        File.WriteAllText(file, "Share-only response fixture.");
        var configuration = new UploadConfiguration { ServerUrl = "https://upload.example.test/files" };
        foreach (var body in new[]
        {
            "{\"url\":\"https://share.example.test/item\"}",
            "{\"url\":\"https://share.example.test/item\",\"file\":\"javascript:invalid\"}"
        })
        {
            using var client = new UploadClient(new HttpClient(new Handler(body)));
            var result = await client.UploadAsync(file, configuration, null, CancellationToken.None);
            check(result.ShareUrl == "https://share.example.test/item" && result.FileUrl.Length == 0,
                "A share-only upload response keeps its share URL without inventing a direct download URL");
        }
        using (var client = new UploadClient(new HttpClient(new Handler("{\"file\":\"https://cdn.example.test/upload.txt\"}"))))
        {
            var result = await client.UploadAsync(file, configuration, null, CancellationToken.None);
            check(result.ShareUrl == result.FileUrl && result.FileUrl == "https://cdn.example.test/upload.txt",
                "A direct-file-only response remains available as both a direct link and the default share link");
        }
        using (var client = new UploadClient(new HttpClient(new Handler("{\"url\":\"javascript:invalid\",\"file\":\"http://invalid.example.test/file\"}"))))
        {
            var rejected = false;
            try { await client.UploadAsync(file, configuration, null, CancellationToken.None); }
            catch (InvalidOperationException) { rejected = true; }
            check(rejected, "An upload response with no valid HTTPS share or direct URL is still rejected");
        }
    }

    private sealed class Handler : HttpMessageHandler
    {
        private readonly string _body;
        internal Handler(string body) => _body = body;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Created) { Content = new StringContent(_body) });
    }
}
