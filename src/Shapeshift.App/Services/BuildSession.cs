using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace Shapeshift.App.Services;

/// <summary>Сообщение движка из канала (см. EnginePipe в движке).</summary>
public sealed record EngineMessage(string Type, JsonElement Body)
{
    public string? Text => Body.TryGetProperty("text", out var t) ? t.GetString() : null;
}

/// <summary>
/// Долгая работа движка для окна (сборка, скачивание): окно поднимает именованный канал, запускает движок
/// и читает его сообщения. Сборка — с повышением (один запрос UAC), скачивание — без. Отмена — командой по каналу;
/// закрытие окна рвёт канал, и движок тоже отменяет работу.
/// </summary>
public sealed class BuildSession : IAsyncDisposable
{
    const int ErrorCancelled = 1223; // пользователь отказал в UAC

    readonly NamedPipeServerStream _pipe;
    readonly string _pipeName = "shapeshift-" + Guid.NewGuid().ToString("N");

    BuildSession()
    {
        _pipe = new NamedPipeServerStream(_pipeName, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
    }

    /// <summary>Сборка: движок с повышением. null — пользователь отказал в UAC.</summary>
    public static Task<BuildSession?> StartBuildAsync(string enginePath, string profilePath, string workspace, CancellationToken ct) =>
        StartAsync(enginePath, ["build", "--profile", profilePath, "--workspace", workspace], elevated: true, ct);

    /// <summary>Скачивание выпуска UUP в кэш: движок без повышения.</summary>
    public static async Task<BuildSession> StartDownloadAsync(
        string enginePath, string updateId, string language, IReadOnlyList<string> editions, string workspace, CancellationToken ct) =>
        (await StartAsync(enginePath,
            ["uup", "download", "--update", updateId, "--lang", language, .. editions.SelectMany(e => new[] { "--edition", e }), "--workspace", workspace],
            elevated: false, ct))!;

    static async Task<BuildSession?> StartAsync(string enginePath, IReadOnlyList<string> arguments, bool elevated, CancellationToken ct)
    {
        var session = new BuildSession();
        ProcessStartInfo start;
        if (elevated)
        {
            start = new ProcessStartInfo(enginePath)
            {
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
                Arguments = string.Join(" ", arguments.Select(a => a.StartsWith("--", StringComparison.Ordinal) ? a : Quote(a))) + $" --pipe {session._pipeName}",
            };
        }
        else
        {
            start = new ProcessStartInfo(enginePath) { UseShellExecute = false, CreateNoWindow = true };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            start.ArgumentList.Add("--pipe");
            start.ArgumentList.Add(session._pipeName);
        }

        try
        {
            Process.Start(start);
        }
        catch (Win32Exception e) when (e.NativeErrorCode == ErrorCancelled)
        {
            await session.DisposeAsync();
            return null;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await session._pipe.WaitForConnectionAsync(timeout.Token);
        return session;
    }

    // Для запуска с повышением аргументы — одна строка. Путь в кавычках не должен кончаться на «\»
    // (например, D:\): обратный слэш перед кавычкой её экранирует.
    static string Quote(string path) => $"\"{(path.EndsWith('\\') ? path + "." : path)}\"";

    /// <summary>Сообщения движка до <c>exit</c> включительно.</summary>
    public async IAsyncEnumerable<EngineMessage> ReadAsync([System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        using var reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
        while (await reader.ReadLineAsync(ct) is { } line)
        {
            using var doc = JsonDocument.Parse(line);
            var message = new EngineMessage(doc.RootElement.GetProperty("type").GetString() ?? "", doc.RootElement.Clone());
            yield return message;
            if (message.Type == "exit")
                yield break;
        }
    }

    public async Task CancelAsync()
    {
        if (!_pipe.IsConnected)
            return;
        try
        {
            await _pipe.WriteAsync(Encoding.UTF8.GetBytes("{\"type\":\"cancel\"}\n"));
            await _pipe.FlushAsync();
        }
        catch (IOException)
        {
            // Движок уже закончил.
        }
    }

    public ValueTask DisposeAsync() => _pipe.DisposeAsync();
}
