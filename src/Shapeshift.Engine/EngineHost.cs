namespace Shapeshift.Engine;

/// <summary>
/// Всё, что движок берёт у окружения. Командная строка использует <see cref="Default"/>;
/// тесты подменяют отдельные части, не обходя шов CLI.
/// </summary>
public sealed class EngineHost
{
    public static EngineHost Default { get; } = new();

    /// <summary>Свободное место (байт) на томе, где лежит путь.</summary>
    public Func<string, long> AvailableFreeSpace { get; init; } =
        path => new DriveInfo(VolumeOf(path)).AvailableFreeSpace;

    /// <summary>Рабочая папка, если не указана явно.</summary>
    public string DefaultWorkspace { get; init; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Shapeshift", "Workspace");

    /// <summary>Каталог твиков, если не указан явно: поставляется рядом с движком.</summary>
    public string DefaultCatalog { get; init; } = Path.Combine(AppContext.BaseDirectory, "catalog", "tweaks.json");

    /// <summary>Сетевой обработчик для запросов в интернет (Windows Update); тесты подставляют записанные ответы.</summary>
    public Func<HttpMessageHandler> HttpHandler { get; init; } =
        () => new SocketsHttpHandler { AutomaticDecompression = System.Net.DecompressionMethods.All };

    public HttpClient CreateHttpClient() => new(HttpHandler(), disposeHandler: true) { Timeout = TimeSpan.FromSeconds(60) };

    public static string VolumeOf(string path) => Path.GetPathRoot(Path.GetFullPath(path))!.ToUpperInvariant();
}
