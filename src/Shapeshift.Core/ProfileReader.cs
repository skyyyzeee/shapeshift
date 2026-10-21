using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shapeshift.Core;

/// <summary>Профиль не удалось прочитать: битый JSON, неизвестная версия формата или недопустимое значение.</summary>
public sealed class ProfileFormatException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Читает профиль из JSON. Формат версионирован полем <c>formatVersion</c>.</summary>
public static partial class ProfileReader
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
            ParseTweaks(dto.Tweaks),
            ParseEditions(dto.Editions),
            ParseNames(dto.RemoveApps, "removeApps"),
            ParsePrograms(dto.Programs, baseDirectory),
            ParseSetup(dto.Setup),
            ParseRegional(dto.Regional),
            ParseSource(dto.Source, baseDirectory),
            new OutputOptions(ResolvePath(dto.Output?.Iso, baseDirectory)),
            dto.LatestUpdate,
            ParseWallpaper(dto.Wallpaper, baseDirectory),
            ParseLanguages(dto.Languages),
            dto.DotNet35 ?? false);
    }

    // Существование твиков в каталоге проверяет движок: профиль читается и без каталога.
    static IReadOnlyList<string> ParseTweaks(List<string>? tweaks)
    {
        if (tweaks is null)
            return [];
        if (tweaks.Any(string.IsNullOrWhiteSpace))
            throw new ProfileFormatException("'tweaks' contains an empty id.");
        if (tweaks.Distinct(StringComparer.Ordinal).Count() != tweaks.Count)
            throw new ProfileFormatException("'tweaks' lists the same tweak twice.");
        return tweaks;
    }

    // Список имён без пустых и повторов; что из них есть в образе, решает движок.
    static IReadOnlyList<string> ParseNames(List<string>? names, string field)
    {
        if (names is null)
            return [];
        if (names.Any(string.IsNullOrWhiteSpace))
            throw new ProfileFormatException($"'{field}' contains an empty name.");
        if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count)
            throw new ProfileFormatException($"'{field}' lists the same name twice.");
        return names;
    }

    // Что лежит по путям, проверяет движок при сборке: профиль читается и на другом компьютере.
    static IReadOnlyList<ProgramSpec> ParsePrograms(List<ProgramV1?>? programs, string? baseDirectory)
    {
        if (programs is null)
            return [];

        var result = new List<ProgramSpec>();
        for (var i = 0; i < programs.Count; i++)
        {
            var where = $"'programs[{i}]'";
            var p = programs[i] ?? throw new ProfileFormatException($"{where} is empty.");
            if (p.Name is { } name && (string.IsNullOrWhiteSpace(name) || name.Length > ProgramRules.MaxNameLength || !ProgramRules.IsSingleLine(name)))
                throw new ProfileFormatException($"{where}.name must be one line of 1 to {ProgramRules.MaxNameLength} characters.");
            if (p.Args is { } args && (args.Length > ProgramRules.MaxArgsLength || !ProgramRules.IsSingleLine(args)))
                throw new ProfileFormatException($"{where}.args must be one line of at most {ProgramRules.MaxArgsLength} characters.");
            if (p.Winget is not null == p.Path is not null)
                throw new ProfileFormatException($"{where} needs exactly one of 'winget' (package id) or 'path' (installer file or folder).");

            if (p.Winget is { } id)
            {
                if (!ProgramRules.IsWingetId(id))
                    throw new ProfileFormatException($"{where}.winget: '{id}' is not a winget package id like Telegram.TelegramDesktop.");
                if (p.Run is not null || p.Args is not null)
                    throw new ProfileFormatException($"{where}: 'run' and 'args' are only for 'path'; winget installs silently by itself.");
                result.Add(new WingetProgram(id, p.Name ?? id));
                continue;
            }

            var path = ResolvePath(p.Path, baseDirectory) ?? throw new ProfileFormatException($"{where}.path is empty.");
            if (p.Run is { } run)
            {
                if (string.IsNullOrWhiteSpace(run) || Path.IsPathRooted(run) || !ProgramRules.IsSingleLine(run) ||
                    run.Split('\\', '/').Any(part => part is ".." or "."))
                    throw new ProfileFormatException($"{where}.run must be a path inside the folder, like setup.exe or bin\\setup.exe.");
                if (!ProgramRules.Runnable.Contains(Path.GetExtension(run).ToLowerInvariant()))
                    throw new ProfileFormatException($"{where}.run: only {string.Join(", ", ProgramRules.Runnable)} files can be run.");
            }
            result.Add(new LocalProgram(p.Name ?? Path.GetFileNameWithoutExtension(p.Run ?? path), path, p.Run, p.Args));
        }

        var winget = result.OfType<WingetProgram>().Select(w => w.Id).ToList();
        if (winget.Distinct(StringComparer.OrdinalIgnoreCase).Count() != winget.Count)
            throw new ProfileFormatException("'programs' lists the same winget package twice.");
        var local = result.OfType<LocalProgram>().Select(l => $"{l.Path}|{l.Run}").ToList();
        if (local.Distinct(StringComparer.OrdinalIgnoreCase).Count() != local.Count)
            throw new ProfileFormatException("'programs' lists the same installer twice.");
        return result;
    }

    // Существование редакций в образе проверяет движок: профиль читается и без образа.
    static IReadOnlyList<string> ParseEditions(List<string>? editions)
    {
        if (editions is null)
            return [];
        if (editions.Count == 0)
            throw new ProfileFormatException("'editions' is empty; leave it out to keep every edition.");
        if (editions.Any(string.IsNullOrWhiteSpace))
            throw new ProfileFormatException("'editions' contains an empty id.");
        if (editions.Distinct(StringComparer.OrdinalIgnoreCase).Count() != editions.Count)
            throw new ProfileFormatException("'editions' lists the same edition twice.");
        return editions;
    }

    static SetupOptions ParseSetup(SetupV1? setup)
    {
        if (setup is null)
            return SetupOptions.None;

        if (setup.ComputerName is { } computerName && NameRules.CheckComputerName(computerName) is { } nameError)
            throw new ProfileFormatException($"'setup.computerName': {nameError}.");

        if (setup.Edition is { } edition && string.IsNullOrWhiteSpace(edition))
            throw new ProfileFormatException("'setup.edition' is empty.");

        string? productKey = null;
        if (setup.ProductKey is { } key)
        {
            productKey = key.Trim().ToUpperInvariant();
            if (!ProductKeyPattern().IsMatch(productKey))
                throw new ProfileFormatException("'setup.productKey' must look like XXXXX-XXXXX-XXXXX-XXXXX-XXXXX.");
        }

        return new SetupOptions(
            ParseUser(setup.User),
            setup.ComputerName,
            setup.SkipOobe ?? false,
            setup.BypassOnlineAccount ?? false,
            setup.WipeDisk0 ?? false,
            setup.Edition,
            productKey);
    }

    static RegionalOptions ParseRegional(RegionalV1? regional)
    {
        if (regional is null)
            return RegionalOptions.None;

        if (regional.Formats is { } formats && RegionalRules.CheckCulture(formats) is { } formatsError)
            throw new ProfileFormatException($"'regional.formats': {formatsError}.");

        var keyboards = regional.Keyboards ?? [];
        foreach (var keyboard in keyboards)
        {
            if (RegionalRules.CheckKeyboard(keyboard) is { } keyboardError)
                throw new ProfileFormatException($"'regional.keyboards': {keyboardError}.");
        }
        if (keyboards.Distinct(StringComparer.OrdinalIgnoreCase).Count() != keyboards.Count)
            throw new ProfileFormatException("'regional.keyboards' lists the same keyboard twice.");

        if (regional.TimeZone is { } timeZone && RegionalRules.CheckTimeZone(timeZone) is { } timeZoneError)
            throw new ProfileFormatException($"'regional.timeZone': {timeZoneError}.");

        return new RegionalOptions(regional.Formats, keyboards, regional.TimeZone);
    }

    [System.Text.RegularExpressions.GeneratedRegex("^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$")]
    private static partial System.Text.RegularExpressions.Regex ProductKeyPattern();

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

    // Свой ISO или выпуск UUP — что-то одно. Существование канала и языка проверяет движок (по Windows Update).
    static SourceOptions ParseSource(SourceV1? source, string? baseDirectory)
    {
        if (source?.Uup is not { } uup)
            return new SourceOptions(ResolvePath(source?.Iso, baseDirectory));
        if (!string.IsNullOrWhiteSpace(source.Iso))
            throw new ProfileFormatException("'source' needs either 'iso' or 'uup', not both.");
        if (string.IsNullOrWhiteSpace(uup.Channel))
            throw new ProfileFormatException("'source.uup.channel' is empty; for example, windows11.");
        if (string.IsNullOrWhiteSpace(uup.Language) || RegionalRules.CheckCulture(uup.Language) is not null)
            throw new ProfileFormatException("'source.uup.language' must be a Windows language like ru-RU.");
        if (uup.UpdateId is { } id && !Guid.TryParse(id, out _))
            throw new ProfileFormatException("'source.uup.updateId' must be an update id (GUID) from 'uup releases'.");
        return new SourceOptions(null, new UupSourceOptions(uup.Channel, uup.Language, uup.UpdateId));
    }

    // Есть ли файл, проверяет движок при сборке: профиль читается и на другом компьютере.
    // Имена — как у Windows (en-US): регистр из CultureInfo, чтобы сравнивать с языками образа и выпуска.
    static LanguageOptions? ParseLanguages(LanguagesV1? languages)
    {
        if (languages is null)
            return null;

        string Culture(string? name, string field) => RegionalRules.CheckCulture(name) is { } error
            ? throw new ProfileFormatException($"'{field}': {error}.")
            : System.Globalization.CultureInfo.GetCultureInfo(name!, predefinedOnly: true).Name;

        var add = (languages.Add ?? []).Select(l => Culture(l, "languages.add")).ToList();
        if (add.Distinct(StringComparer.OrdinalIgnoreCase).Count() != add.Count)
            throw new ProfileFormatException("'languages.add' lists the same language twice.");
        var display = languages.Display is null ? null : Culture(languages.Display, "languages.display");
        return new LanguageOptions(add, display);
    }

    static WallpaperOptions? ParseWallpaper(WallpaperV1? wallpaper, string? baseDirectory)
    {
        if (wallpaper is null)
            return null;
        var path = ResolvePath(wallpaper.Path, baseDirectory) ?? throw new ProfileFormatException("'wallpaper.path' is empty.");
        if (!WallpaperOptions.Extensions.Contains(Path.GetExtension(path).ToLowerInvariant()))
            throw new ProfileFormatException($"'wallpaper.path' must be a picture: {string.Join(", ", WallpaperOptions.Extensions)}.");
        var style = wallpaper.Style?.ToLowerInvariant() switch
        {
            null or "fill" => WallpaperStyle.Fill,
            "fit" => WallpaperStyle.Fit,
            "stretch" => WallpaperStyle.Stretch,
            "center" => WallpaperStyle.Center,
            "tile" => WallpaperStyle.Tile,
            "span" => WallpaperStyle.Span,
            var other => throw new ProfileFormatException($"'wallpaper.style': unknown style '{other}'; expected fill, fit, stretch, center, tile or span."),
        };
        return new WallpaperOptions(path, wallpaper.Locked ?? true, style);
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
        int FormatVersion, string? Architecture, List<string>? Tweaks, List<string>? Editions, List<string>? RemoveApps, List<ProgramV1?>? Programs, SetupV1? Setup, RegionalV1? Regional, SourceV1? Source, IsoV1? Output, bool? LatestUpdate, WallpaperV1? Wallpaper, LanguagesV1? Languages, bool? DotNet35);

    sealed record LanguagesV1(List<string>? Add, string? Display);

    sealed record WallpaperV1(string? Path, bool? Locked, string? Style);

    sealed record ProgramV1(string? Winget, string? Path, string? Run, string? Args, string? Name);

    sealed record SetupV1(
        UserV1? User, string? ComputerName, bool? SkipOobe, bool? BypassOnlineAccount, bool? WipeDisk0,
        string? Edition, string? ProductKey);

    sealed record RegionalV1(string? Formats, List<string>? Keyboards, string? TimeZone);

    sealed record UserV1(string? Name, PasswordV1? Password, bool? AutoLogon);

    sealed record PasswordV1(string? Mode, string? Value);

    sealed record IsoV1(string? Iso);

    sealed record SourceV1(string? Iso, UupV1? Uup);

    sealed record UupV1(string? Channel, string? Language, string? UpdateId);
}
