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

    /// <summary>Синтетический выпуск вместо записанных ответов: его файлы можно и скачать.</summary>
    public FakeUupRelease? Release { get; init; }

    /// <summary>Скачивания: имя файла и с какого байта просили (null — целиком).</summary>
    public ConcurrentQueue<(string File, long? From)> Downloads { get; } = new();

    /// <summary>Обрыв связи после стольких байт — один раз для каждого из этих файлов.</summary>
    public Dictionary<string, int> DropOnceAfter { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Сервер замолкает после стольких байт (соединение не рвётся) — один раз для каждого из этих файлов.</summary>
    public Dictionary<string, int> StallOnceAfter { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Эти файлы первый раз приходят испорченными.</summary>
    public HashSet<string> CorruptOnce { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Ссылки первой выдачи уже истекли (сервер отвечает 403).</summary>
    public bool FirstLinksExpired { get; init; }

    /// <summary>Сервер не умеет докачку: на запрос с Range отдаёт файл целиком.</summary>
    public bool IgnoresRange { get; init; }

    /// <summary>Перед отдачей файла — ждать этот сигнал (чтобы успеть отменить посреди скачивания).</summary>
    public SemaphoreSlim? HoldDownloads { get; init; }

    int _urlVersion;
    readonly HashSet<string> _dropped = new(StringComparer.OrdinalIgnoreCase);
    readonly HashSet<string> _corrupted = new(StringComparer.OrdinalIgnoreCase);

    public EngineHost Host(string workspace) => new() { HttpHandler = () => this, DefaultWorkspace = workspace };

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (Offline)
            throw new HttpRequestException("No such host is known.");
        if (request.Method == HttpMethod.Get)
            return await DownloadAsync(request, ct);

        var body = await request.Content!.ReadAsStringAsync(ct);
        Requests.Enqueue((request.RequestUri!, request.Headers.UserAgent.ToString(), body));

        var action = ActionPattern().Match(body).Groups[1].Value;
        if (action == "GetCookie")
            return Respond("cookie.xml");
        if (action == "GetExtendedUpdateInfo2" && Release is { } withLinks)
            return Xml(withLinks.UrlsResponse(Interlocked.Increment(ref _urlVersion)));
        if (action != "SyncUpdates")
            return new HttpResponseMessage(HttpStatusCode.BadRequest);
        if (Release is { } release)
            return Xml(release.SyncResponse());

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

    static HttpResponseMessage Respond(string fixture) => Xml(File.ReadAllText(Path.Combine(Fixtures, fixture)));

    static HttpResponseMessage Xml(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8, "application/soap+xml") };

    /// <summary>Сколько раз выдавались ссылки (GetExtendedUpdateInfo2).</summary>
    public int LinkRequests => Volatile.Read(ref _urlVersion);

    async Task<HttpResponseMessage> DownloadAsync(HttpRequestMessage request, CancellationToken ct)
    {
        if (HoldDownloads is { } hold)
        {
            await hold.WaitAsync(ct);
            hold.Release();
        }
        var sha1 = request.RequestUri!.AbsolutePath.Trim('/');
        var content = Release?.ContentBySha1(sha1);
        if (content is null)
            return new HttpResponseMessage(HttpStatusCode.NotFound);
        var name = Release!.Files.First(f => f.Value == content).Key;
        var from = request.Headers.Range?.Ranges.FirstOrDefault()?.From;
        Downloads.Enqueue((name, from));

        if (FirstLinksExpired && request.RequestUri.Query == "?v=1")
            return new HttpResponseMessage(HttpStatusCode.Forbidden);

        var body = content;
        lock (_corrupted)
        {
            if (CorruptOnce.Contains(name) && _corrupted.Add(name))
            {
                body = (byte[])content.Clone();
                body[^1] ^= 0xFF;
            }
        }
        var partial = from is > 0 && !IgnoresRange;
        if (partial)
            body = body[(int)from!.Value..];

        Stream stream = new MemoryStream(body);
        lock (_dropped)
        {
            if (DropOnceAfter.TryGetValue(name, out var after) && _dropped.Add(name))
                stream = new DroppingStream(body, after);
            else if (StallOnceAfter.TryGetValue(name, out var silentAfter) && _dropped.Add(name))
                stream = new StallingStream(body, silentAfter);
        }
        var response = new HttpResponseMessage(partial ? HttpStatusCode.PartialContent : HttpStatusCode.OK) { Content = new StreamContent(stream) };
        response.Content.Headers.ContentLength = body.Length;
        return response;
    }

    /// <summary>Отдаёт первые байты и рвёт соединение.</summary>
    sealed class DroppingStream(byte[] content, int after) : MemoryStream(content)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            if (Position >= after)
                throw new IOException("The connection was reset.");
            return base.Read(buffer, offset, (int)Math.Min(count, after - Position));
        }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= after)
                throw new IOException("The connection was reset.");
            return base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, after - Position)], cancellationToken);
        }
    }

    /// <summary>Отдаёт первые байты и замолкает: соединение открыто, данных нет.</summary>
    sealed class StallingStream(byte[] content, int after) : MemoryStream(content)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position >= after)
                await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.ReadAsync(buffer[..(int)Math.Min(buffer.Length, after - Position)], cancellationToken);
        }
    }

    [GeneratedRegex(@"ClientWebService/(\w+)</a:Action>")]
    private static partial Regex ActionPattern();

    [GeneratedRegex(@"PN=Client\.OS\.rs2\.amd64&Branch=[^&]+&PrimaryOSProduct=1&Repairable=1&V=([0-9.]+)&")]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"FlightingBranchName=([^&<]*)")]
    private static partial Regex FlightBranchPattern();
}
