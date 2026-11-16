using System.Net;
using System.Text;
using BirkNext.TechnicalHistory;
using BirkNext.Web.Services;
using FluentAssertions;

namespace BirkNext.Web.Tests.Services;

/// <summary>
/// The technical history client speaks the API's JSON: enums travel as numbers (the API has no string-enum converter, so a string status made
/// every follow-up change a 400 in the browser), and both number and string enums in responses are read.
/// </summary>
public sealed class TechnicalHistoryApiServiceTests
{
    private sealed class Capture(string response) : HttpMessageHandler
    {
        public string? Body;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") };
        }
    }

    [Fact]
    public async Task A_status_change_is_sent_with_a_numeric_status_and_the_answer_is_read()
    {
        var handler = new Capture("""{"id":"5b1d6f0e-0000-0000-0000-000000000001","status":2,"version":4,"history":[]}""");
        var api = new TechnicalHistoryApiService(new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") });

        var (finding, error) = await api.UpdateAsync("import:lendo", Guid.NewGuid(), new UpdateTechnicalFindingRequest(3, TechnicalFindingStatus.Confirmed, "n", null, null, null));

        error.Should().BeNull();
        handler.Body.Should().Contain("\"status\":2", "TechnicalFindingStatus.Confirmed as the number the API binds");
        finding!.Status.Should().Be(TechnicalFindingStatus.Confirmed);
    }
}
