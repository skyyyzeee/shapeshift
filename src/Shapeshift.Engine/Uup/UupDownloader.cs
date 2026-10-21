using System.Diagnostics;
using System.Net;
using System.Security.Cryptography;

namespace Shapeshift.Engine.Uup;

/// <summary>Скачивание не удалось (сеть, место, повреждённые данные); сообщение — для пользователя.</summary>
public sealed class UupDownloadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Сколько скачано за весь набор файлов и с какой скоростью (за этот запуск).</summary>
public sealed record TransferProgress(long Done, long Total, double BytesPerSecond)
{
    /// <summary>Оставшееся время; null — скорость ещё не известна.</summary>
    public TimeSpan? Left => BytesPerSecond > 1 ? TimeSpan.FromSeconds((Total - Done) / BytesPerSecond) : null;
}

/// <summary>
/// Файлы UUP в кэше рабочей папки: <c>cache\uup\files\&lt;SHA-1&gt;</c> — одинаковые файлы разных выпусков
/// хранятся один раз. Недокачанный файл — <c>.part</c> рядом; после обрыва или перезапуска докачивается с того же места.
/// Каждый файл проверяется по SHA-1 и SHA-256 из Windows Update; повреждённый скачивается заново.
/// </summary>
public sealed class UupDownloader(HttpClient http, string workspace)
{
    const int Parallel = 4;
    const int Attempts = 5;
    static readonly TimeSpan ReportEvery = TimeSpan.FromMilliseconds(250);

    public string Folder => Path.Combine(workspace, "cache", "uup", "files");

    public string PathOf(UupFile file) => Path.Combine(Folder, file.Sha1);

    public bool IsCached(UupFile file) => new FileInfo(PathOf(file)) is { Exists: true } info && info.Length == file.Size;

    /// <summary>
    /// Сколько ждать данных от сервера, прежде чем счесть соединение зависшим и переподключиться с докачкой.
    /// Таймаут HttpClient действует только до заголовков ответа, а сервер может принять соединение и замолчать.
    /// </summary>
    public TimeSpan StallTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Свободное место на диске кэша — проверяется до начала скачивания.</summary>
    public Func<string, long> FreeSpace { get; init; } = path => new DriveInfo(EngineHost.VolumeOf(path)).AvailableFreeSpace;

    /// <param name="links">Ссылки на файлы выпуска; общие для нескольких вызовов, обновляются, если истекли.</param>
    /// <exception cref="UupDownloadException"/>
    /// <exception cref="OperationCanceledException">Отменили; недокачанное остаётся для докачки.</exception>
    public async Task DownloadAsync(IReadOnlyList<UupFile> files, UupLinks links, Action<TransferProgress> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(Folder);
        var total = files.Sum(f => f.Size);
        var pending = files.Where(f => !IsCached(f)).DistinctBy(f => f.Sha1).ToList();
        long done = total - pending.Sum(f => f.Size);
        var already = pending.Sum(f => PartLength(f));
        done += already;

        var needed = pending.Sum(f => f.Size) - already;
        if (needed > 0 && FreeSpace(Folder) < needed + (256L << 20))
            throw new UupDownloadException($"not enough space for the download in {Folder}: needs {needed / (1 << 20)} MB more.");

        progress(new TransferProgress(done, total, 0));
        if (pending.Count == 0)
            return;

        var clock = Stopwatch.StartNew();
        long session = 0;
        var lastReport = TimeSpan.Zero;
        var gate = new object();
        void Advance(long bytes)
        {
            var now = Interlocked.Add(ref done, bytes);
            var transferred = Interlocked.Add(ref session, bytes);
            lock (gate)
            {
                if (clock.Elapsed - lastReport < ReportEvery && now < total)
                    return;
                lastReport = clock.Elapsed;
            }
            progress(new TransferProgress(now, total, transferred / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
        }

        using var throttle = new SemaphoreSlim(Parallel);
        var tasks = pending.Select(async file =>
        {
            await throttle.WaitAsync(ct);
            try
            {
                await DownloadFileAsync(file, links, Advance, ct);
            }
            finally
            {
                throttle.Release();
            }
        }).ToList();
        await Task.WhenAll(tasks);
        progress(new TransferProgress(total, total, session / Math.Max(0.001, clock.Elapsed.TotalSeconds)));
    }

    long PartLength(UupFile file) => new FileInfo(PathOf(file) + ".part") is { Exists: true } info && info.Length <= file.Size ? info.Length : 0;

    async Task DownloadFileAsync(UupFile file, UupLinks urls, Action<long> advance, CancellationToken ct)
    {
        var target = PathOf(file);
        var part = target + ".part";
        var corrupt = false;
        for (var attempt = 1; ; attempt++)
        {
            var have = PartLength(file);
            if (have == 0 && File.Exists(part))
                File.Delete(part); // длиннее нужного — мусор
            try
            {
                if (have < file.Size)
                    await FetchAsync(file, part, have, urls, advance, ct);
                if (await MatchesAsync(part, file, ct))
                {
                    FileRetry.Move(part, target);
                    return;
                }
                // Повреждено: скачиваем заново целиком, один раз.
                File.Delete(part);
                advance(-file.Size);
                if (corrupt)
                    throw new UupDownloadException($"{file.Name} arrives damaged (checksum mismatch) every time.");
                corrupt = true;
            }
            catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested && attempt < Attempts)
            {
                await Task.Delay(TimeSpan.FromSeconds(Math.Min(30, 2 * attempt)), ct); // обрыв — докачаем с места остановки
            }
            catch (Exception e) when (e is HttpRequestException or IOException && !ct.IsCancellationRequested)
            {
                throw new UupDownloadException($"cannot download {file.Name}: {e.Message}", e);
            }
        }
    }

    async Task FetchAsync(UupFile file, string part, long have, UupLinks urls, Action<long> advance, CancellationToken ct)
    {
        for (var refresh = 0; ; refresh++)
        {
            var url = await urls.GetAsync(file, refresh > 0, ct);
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (have > 0)
                request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(have, null);
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
            // Ссылки Windows Update живут несколько часов — истёкшую получаем заново.
            if (response.StatusCode is HttpStatusCode.Forbidden or HttpStatusCode.NotFound or HttpStatusCode.Gone && refresh < 2)
                continue;
            if (!response.IsSuccessStatusCode)
                throw new HttpRequestException($"the server answered {(int)response.StatusCode}", null, response.StatusCode);

            var resumed = response.StatusCode == HttpStatusCode.PartialContent;
            if (!resumed && have > 0)
                advance(-have); // сервер прислал файл целиком — начинаем сначала
            await using var output = new FileStream(part, resumed ? FileMode.Append : FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);
            await using var input = await response.Content.ReadAsStreamAsync(ct);
            var buffer = new byte[1 << 20];
            using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
            while (true)
            {
                stall.CancelAfter(StallTimeout);
                int n;
                try
                {
                    n = await input.ReadAsync(buffer, stall.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new IOException($"no data from the server for {StallTimeout.TotalSeconds:0} s");
                }
                if (n == 0)
                    return;
                await output.WriteAsync(buffer.AsMemory(0, n), ct);
                advance(n);
            }
        }
    }

    static async Task<bool> MatchesAsync(string path, UupFile file, CancellationToken ct)
    {
        if (new FileInfo(path).Length != file.Size)
            return false;
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1 << 20];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            sha1.AppendData(buffer, 0, n);
            sha256.AppendData(buffer, 0, n);
        }
        return string.Equals(Convert.ToHexStringLower(sha1.GetHashAndReset()), file.Sha1, StringComparison.OrdinalIgnoreCase)
            && (file.Sha256 is null || string.Equals(Convert.ToHexStringLower(sha256.GetHashAndReset()), file.Sha256, StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// Ссылки на файлы выпуска (SHA-1 → адрес): запрашиваются один раз на всё скачивание — ответ весит мегабайты —
/// и заново, только когда сервер сказал, что ссылка истекла (живут несколько часов).
/// </summary>
public sealed class UupLinks(Func<CancellationToken, Task<IReadOnlyDictionary<string, Uri>>> getUrls)
{
    readonly SemaphoreSlim _lock = new(1);
    IReadOnlyDictionary<string, Uri>? _urls;
    int _version;

    public async Task<Uri> GetAsync(UupFile file, bool stale, CancellationToken ct)
    {
        var seen = Volatile.Read(ref _version);
        await _lock.WaitAsync(ct);
        try
        {
            // Пока ждали, другой поток уже обновил ссылки — берём их.
            if (_urls is null || (stale && seen == _version))
            {
                _urls = await FetchAsync(ct);
                _version++;
            }
            return _urls.TryGetValue(file.Sha1, out var url)
                ? url
                : throw new UupDownloadException($"Windows Update gave no link for {file.Name}.");
        }
        finally
        {
            _lock.Release();
        }
    }

    /// <summary>Сколько раз просить ссылки, если служба ответила пусто или ошибкой (бывает на минуту-другую).</summary>
    public int Attempts { get; init; } = 3;

    public TimeSpan RetryDelay { get; init; } = TimeSpan.FromSeconds(10);

    async Task<IReadOnlyDictionary<string, Uri>> FetchAsync(CancellationToken ct)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return await getUrls(ct);
            }
            catch (WindowsUpdateException) when (attempt < Attempts)
            {
                await Task.Delay(RetryDelay * attempt, ct);
            }
        }
    }
}
