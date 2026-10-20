using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Shapeshift.Engine.Tests.Support;

/// <summary>
/// Windows Update из записанных ответов (<c>Fixtures/WindowsUpdate</c>): ответ выбирается по запросу —
/// действию SOAP, версии «устройства», каналу и признаку «только эта версия». Записаны живые ответы службы,
/// из списков файлов оставлены образы редакций и пара языковых пакетов.
/// </summary>
public sealed partial class FakeWindowsUpdate : HttpMessageHandler
{
    static readonly string Fixtures = Path.Combine(AppContext.BaseDirectory, "Fixtures", "WindowsUpdate");

    /// <summary>Каналы (FlightingBranchName или Retail), на которые служба отвечает ошибкой 500.</summary>
    public HashSet<string> FailingRings { get; init; } = [];

    /// <summary>Сети нет: каждый запрос — HttpRequestException.</summary>
    public bool Offline { get; init; }

    /// <summary>Все запросы — чтобы проверить, куда и что отправлено.</summary>
    public ConcurrentQueue<(Uri Uri, string UserAgent, string Body)> Requests { get; } = new();

    public EngineHost Host(string workspace) => new() { HttpHandler = () => this, DefaultWorkspace = workspace };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        var body = await request.Content!.ReadAsStringAsync(ct);
        Requests.Enqueue((request.RequestUri!, request.Headers.UserAgent.ToString(), body));
        if (Offline)
            throw new HttpRequestException("No such host is known.");

        var action = ActionPattern().Match(body).Groups[1].Value;
        if (action == "GetCookie")
            return Respond("cookie.xml");
        if (action != "SyncUpdates")
            return new HttpResponseMessage(HttpStatusCode.BadRequest);

        var decoded = WebUtility.HtmlDecode(body);
        var version = VersionPattern().Match(decoded).Groups[1].Value;
        var branch = FlightBranchPattern().Match(decoded).Groups[1].Value;
        var ring = branch.Length == 0 ? "Retail" : branch;
        if (FailingRings.Contains(ring))
            return new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = new StringContent("<fault/>") };
        var thisOnly = decoded.Contains("<SyncCurrentVersionOnly>true</SyncCurrentVersionOnly>", StringComparison.Ordinal);

        return (ring, version, thisOnly) switch
        {
            ("Retail", "10.0.26100.1", true) => Respond("sync-retail-24h2.xml"),
            ("Retail", "10.0.18363.1", _) => Respond("sync-win10.xml"),
            ("Retail", _, _) => Respond("sync-retail.xml"),
            ("ReleasePreview", _, _) => Respond("sync-rp.xml"),
            ("Beta", _, _) => Respond("sync-beta.xml"),
            ("Dev", _, _) => Respond("sync-dev.xml"),
            ("CanaryChannel", _, _) => Respond("sync-canary.xml"),
            _ => new HttpResponseMessage(HttpStatusCode.BadRequest),
        };
    }

    static HttpResponseMessage Respond(string fixture) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(File.ReadAllText(Path.Combine(Fixtures, fixture)), Encoding.UTF8, "application/soap+xml"),
    };

    [GeneratedRegex(@"ClientWebService/(\w+)</a:Action>")]
    private static partial Regex ActionPattern();

    [GeneratedRegex(@"PN=Client\.OS\.rs2\.amd64&Branch=[^&]+&PrimaryOSProduct=1&Repairable=1&V=([0-9.]+)&")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"FlightingBranchName=([^&<]*)")]
    private static partial Regex FlightBranchPattern();
}
