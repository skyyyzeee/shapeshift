using System.Text.Json;
using System.Text.Json.Serialization;

namespace Shapeshift.App.Services;

/// <summary>Выборы мастера в виде профиля — того же формата, что принимает CLI.</summary>
public sealed record ProfileDraft(
    string SourceIso,
    string OutputIso,
    IReadOnlyList<string> Tweaks,
    IReadOnlyList<string>? Editions,
    SetupDraft? Setup,
    RegionalDraft? Regional,
    IReadOnlyList<string>? RemoveApps = null);

public sealed record SetupDraft(
    UserDraft? User,
    string? ComputerName,
    bool SkipOobe,
    bool BypassOnlineAccount,
    bool WipeDisk0,
    string? ProductKey,
    string? Edition = null);

/// <param name="Password">Только для режима «хранить»; иначе null.</param>
public sealed record UserDraft(string Name, bool StorePassword, string? Password, bool AutoLogon);

public sealed record RegionalDraft(string Formats, IReadOnlyList<string> Keyboards, string TimeZone);

/// <summary>Пишет профиль во временный файл для движка и удаляет его после.</summary>
public static class ProfileFile
{
    static readonly string Folder = Path.Combine(Path.GetTempPath(), "Shapeshift");

    static readonly JsonSerializerOptions Json = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static string Write(ProfileDraft draft)
    {
        Directory.CreateDirectory(Folder);
        var path = Path.Combine(Folder, $"profile-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, ToJson(draft));
        return path;
    }

    public static string ToJson(ProfileDraft draft) => JsonSerializer.Serialize(new
    {
        formatVersion = 1,
        tweaks = draft.Tweaks.Count > 0 ? draft.Tweaks : null,
        editions = draft.Editions,
        removeApps = draft.RemoveApps is { Count: > 0 } ? draft.RemoveApps : null,
        setup = draft.Setup is { } s
            ? new
            {
                user = s.User is { } u
                    ? new
                    {
                        name = u.Name,
                        password = u.StorePassword
                            ? new { mode = "store", value = u.Password }
                            : new { mode = "changeAtFirstLogon", value = (string?)null },
                        autoLogon = u.AutoLogon ? true : (bool?)null,
                    }
                    : null,
                computerName = s.ComputerName,
                skipOobe = s.SkipOobe,
                bypassOnlineAccount = s.BypassOnlineAccount,
                wipeDisk0 = s.WipeDisk0 ? true : (bool?)null,
                productKey = s.ProductKey,
                edition = s.Edition,
            }
            : null,
        regional = draft.Regional is { } r
            ? new { formats = r.Formats, keyboards = r.Keyboards, timeZone = r.TimeZone }
            : null,
        source = new { iso = draft.SourceIso },
        output = new { iso = draft.OutputIso },
    }, Json);

    public static void TryDelete(string? path)
    {
        try
        {
            if (path is not null)
                File.Delete(path);
        }
        catch (IOException)
        {
        }
    }
}
