using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shapeshift.Core;

/// <summary>Профиль не удалось прочитать: битый JSON, неизвестная версия формата или недопустимое значение.</summary>
public sealed class ProfileFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Читает профиль из JSON. Формат версионирован полем <c>formatVersion</c>.</summary>
public static class ProfileReader
{
    public const int CurrentFormatVersion = 1;

    static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <param name="json">Текст профиля.</param>
    /// <param name="baseDirectory">Относительно чего разрешать относительные пути в профиле (обычно папка профиля).</param>
    public static Profile Parse(string json, string? baseDirectory = null)
    {
        // Версию читаем отдельно и первой: профиль из будущей версии должен давать ошибку
        // про версию, а не про незнакомые поля.
        var version = ReadFormatVersion(json);
        if (version != CurrentFormatVersion)
        {
            throw new ProfileFormatException(
                $"Unsupported profile format version {version}; this build understands version {CurrentFormatVersion}.");
        }

        ProfileV1 dto;
        try
        {
            dto = JsonSerializer.Deserialize<ProfileV1>(json, Options)
                ?? throw new ProfileFormatException("Profile is empty.");
        }
        catch (JsonException e)
        {
            throw new ProfileFormatException($"Profile is invalid: {e.Message}", e);
        }

        return new Profile(
            ParseArchitecture(dto.Architecture),
            dto.Bypass is { } b
                ? new BypassOptions(b.Tpm ?? false, b.SecureBoot ?? false, b.Ram ?? false, b.Cpu ?? false)
                : BypassOptions.None,
            ParseSetup(dto.Setup),
            new SourceOptions(ResolvePath(dto.Source?.Iso, baseDirectory)),
            new OutputOptions(ResolvePath(dto.Output?.Iso, baseDirectory)));
    }

    static SetupOptions ParseSetup(SetupV1? setup)
    {
        if (setup is null)
            return SetupOptions.None;

        if (setup.ComputerName is { } computerName && NameRules.CheckComputerName(computerName) is { } nameError)
            throw new ProfileFormatException($"'setup.computerName': {nameError}.");

        return new SetupOptions(
            ParseUser(setup.User),
            setup.ComputerName,
            setup.SkipOobe ?? false,
            setup.BypassOnlineAccount ?? false,
            setup.WipeDisk0 ?? false);
    }

    static UserAccount? ParseUser(UserV1? user)
    {
        if (user is null)
            return null;

        if (NameRules.CheckUserName(user.Name) is { } nameError)
            throw new ProfileFormatException($"'setup.user.name': {nameError}.");

        var mode = user.Password?.Mode switch
        {
            null or "changeAtFirstLogon" => PasswordMode.ChangeAtFirstLogon,
            "store" => PasswordMode.Store,
            var other => throw new ProfileFormatException(
                $"'setup.user.password.mode': unknown mode '{other}'; expected 'changeAtFirstLogon' or 'store'."),
        };
        var password = user.Password?.Value;

        if (mode == PasswordMode.ChangeAtFirstLogon && password is not null)
        {
            throw new ProfileFormatException(
                "'setup.user.password.value' is only used with mode 'store'; with 'changeAtFirstLogon' the password is set at first sign-in.");
        }
        if (mode == PasswordMode.Store && NameRules.CheckPassword(password) is { } passwordError)
            throw new ProfileFormatException($"'setup.user.password.value': {passwordError}.");

        var autoLogon = user.AutoLogon ?? false;
        if (autoLogon && mode != PasswordMode.Store)
        {
            throw new ProfileFormatException(
                "'setup.user.autoLogon' needs password mode 'store': automatic sign-in cannot work while the password must be changed.");
        }

        return new UserAccount(user.Name!, mode, password, autoLogon);
    }

    static string? ResolvePath(string? path, string? baseDirectory)
    {
        if (string.IsNullOrWhiteSpace(path))
            return null;
        return Path.GetFullPath(Environment.ExpandEnvironmentVariables(path), baseDirectory ?? Environment.CurrentDirectory);
    }

    static int ReadFormatVersion(string json)
    {
        JsonDocument doc;
        try
        {
            doc = JsonDocument.Parse(json, new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });
        }
        catch (JsonException e)
        {
            throw new ProfileFormatException($"Profile is not valid JSON: {e.Message}", e);
        }

        using (doc)
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new ProfileFormatException("Profile must be a JSON object.");

            if (!doc.RootElement.TryGetProperty("formatVersion", out var v))
                throw new ProfileFormatException("Profile has no 'formatVersion'.");

            if (v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version))
                throw new ProfileFormatException("'formatVersion' must be an integer.");

            return version;
        }
    }

    static Architecture ParseArchitecture(string? value) => value?.ToLowerInvariant() switch
    {
        null or "x64" => Architecture.X64,
        "arm64" => throw new ProfileFormatException("Architecture 'arm64' is not supported yet; only 'x64' is."),
        _ => throw new ProfileFormatException($"Unknown architecture '{value}'; expected 'x64'."),
    };

    sealed record ProfileV1(
        int FormatVersion, string? Architecture, BypassV1? Bypass, SetupV1? Setup, IsoV1? Source, IsoV1? Output);

    sealed record BypassV1(bool? Tpm, bool? SecureBoot, bool? Ram, bool? Cpu);

    sealed record SetupV1(UserV1? User, string? ComputerName, bool? SkipOobe, bool? BypassOnlineAccount, bool? WipeDisk0);

    sealed record UserV1(string? Name, PasswordV1? Password, bool? AutoLogon);

    sealed record PasswordV1(string? Mode, string? Value);

    sealed record IsoV1(string? Iso);
}
