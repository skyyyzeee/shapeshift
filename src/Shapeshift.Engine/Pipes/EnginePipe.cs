using System.IO.Pipes;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Shapeshift.Engine.Builds;

namespace Shapeshift.Engine.Pipes;

/// <summary>
/// Канал движок ↔ окно (ADR-0003). Окно — сервер именованного канала, движок подключается к нему.
/// Сообщения — по одному объекту JSON в строке (UTF-8). Движок шлёт: <c>step</c>, <c>progress</c>,
/// <c>log</c>, <c>warning</c>, <c>error</c>, <c>done</c>, <c>cancelled</c>, последним — <c>exit</c>.
/// Окно шлёт <c>cancel</c>; обрыв канала (окно закрыли) тоже означает отмену.
/// </summary>
public sealed class EnginePipe : IAsyncDisposable
{
    static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    readonly NamedPipeClientStream _pipe;
    readonly StreamWriter _writer;
    readonly Lock _gate = new();

    EnginePipe(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false)) { AutoFlush = true, NewLine = "\n" };
    }

    public static async Task<EnginePipe> ConnectAsync(string name, CancellationToken ct)
    {
        var pipe = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        await pipe.ConnectAsync(timeout.Token);
        return new EnginePipe(pipe);
    }

    /// <summary>Отправляет сообщение; если окно уже отключилось — молча ничего не делает.</summary>
    public void Send(string type, object? payload = null)
    {
        var message = new Dictionary<string, object?> { ["type"] = type };
        if (payload is not null)
        {
            foreach (var property in JsonSerializer.SerializeToElement(payload, Json).EnumerateObject())
                message[property.Name] = property.Value;
        }

        var line = JsonSerializer.Serialize(message, Json);
        lock (_gate)
        {
            try
            {
                if (_pipe.IsConnected)
                    _writer.WriteLine(line);
            }
            catch (IOException)
            {
                // Окно закрыли — сборку отменит чтение команд.
            }
        }
    }

    /// <summary>Читает команды окна, пока канал открыт; на <c>cancel</c> или обрыв вызывает <paramref name="cancel"/>.</summary>
    public async Task ListenAsync(Action cancel, CancellationToken ct)
    {
        using var reader = new StreamReader(_pipe, new UTF8Encoding(false), leaveOpen: true);
        try
        {
            while (await reader.ReadLineAsync(ct) is { } line)
            {
                using var doc = JsonDocument.Parse(line);
                if (doc.RootElement.TryGetProperty("type", out var type) && type.GetString() == "cancel")
                {
                    cancel();
                    return;
                }
            }
        }
        catch (Exception e) when (e is IOException or JsonException)
        {
        }
        catch (OperationCanceledException)
        {
            return; // сборка закончилась сама
        }

        cancel(); // канал закрыт: окна больше нет
    }

    public async ValueTask DisposeAsync()
    {
        // Если окно уже закрыло канал, дописать буфер некуда — это не ошибка сборки.
        try
        {
            await _writer.DisposeAsync();
        }
        catch (IOException)
        {
        }
        await _pipe.DisposeAsync();
    }
}

/// <summary>Ход сборки — сообщениями в канал. Прогресс — не чаще, чем раз в полпроцента.</summary>
public sealed class PipeBuildReporter(EnginePipe pipe) : IBuildReporter
{
    double _lastProgress = -1;

    public void StepStarted(int index, int total, BuildStep step)
    {
        _lastProgress = -1;
        pipe.Send("step", new { index, total, kind = step.Kind.ToString(), description = step.Description });
    }

    public void Progress(double fraction)
    {
        if (fraction - _lastProgress < 0.005 && fraction < 1)
            return;
        _lastProgress = fraction;
        pipe.Send("progress", new { fraction = Math.Round(fraction, 4) });
    }

    public void Log(string text) => pipe.Send("log", new { text });

    public void Done(string outputIso) => pipe.Send("done", new { outputIso });
}

/// <summary>
/// Текст, который команда пишет в консоль, — строками в канал: обычный вывод как <c>log</c>,
/// строки «warning: …» и «error: …» потока ошибок — как <c>warning</c> и <c>error</c>.
/// </summary>
public sealed class PipeTextWriter(EnginePipe pipe, bool isErrorStream) : TextWriter
{
    readonly StringBuilder _line = new();

    public override Encoding Encoding => Encoding.UTF8;

    public override void Write(char value)
    {
        if (value == '\n')
            Flush();
        else if (value != '\r')
            _line.Append(value);
    }

    public override void Flush()
    {
        if (_line.Length == 0)
            return;
        var text = _line.ToString();
        _line.Clear();

        if (!isErrorStream)
            pipe.Send("log", new { text });
        else if (text.StartsWith("warning: ", StringComparison.Ordinal))
            pipe.Send("warning", new { text = text["warning: ".Length..] });
        else
            pipe.Send("error", new { text = text.StartsWith("error: ", StringComparison.Ordinal) ? text["error: ".Length..] : text });
    }
}
