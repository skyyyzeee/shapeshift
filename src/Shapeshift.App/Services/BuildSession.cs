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
/// Одна сборка из окна: окно поднимает именованный канал, запускает движок с повышением (один запрос UAC)
/// и читает его сообщения. Отмена — командой по каналу; закрытие окна рвёт канал, и движок тоже отменяет сборку.
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

    /// <summary>Запускает движок с повышением. null — пользователь отказал в UAC.</summary>
    public static async Task<BuildSession?> StartAsync(string enginePath, string profilePath, CancellationToken ct)
    {
        var session = new BuildSession();
        var start = new ProcessStartInfo(enginePath)
        {
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden,
            Arguments = $"build --profile \"{profilePath}\" --pipe {session._pipeName}",
        };

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
