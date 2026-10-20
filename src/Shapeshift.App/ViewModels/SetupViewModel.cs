using System.Collections.ObjectModel;
using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;
using Shapeshift.Core;

namespace Shapeshift.App.ViewModels;

/// <summary>Вариант выбора: значение для профиля и подпись для человека.</summary>
public sealed record Choice(string Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// Шаг «Установка»: всё, что делает установку автоматической. Значения по умолчанию — «удобно и безопасно»:
/// обход требований Windows 11, локальный пользователь с паролем при первом входе, без экранов OOBE
/// и аккаунта Microsoft, регион как у этого компьютера. Стирание диска — только в продвинутом режиме.
/// </summary>
public sealed partial class SetupViewModel : ObservableObject
{
    public static readonly string[] BypassTweaks = ["bypass.tpm", "bypass.secure-boot", "bypass.ram", "bypass.cpu"];

    public SetupViewModel()
    {
        Formats = CultureChoices.FirstOrDefault(c => c.Value == CultureInfo.CurrentCulture.Name) ?? CultureChoices.First(c => c.Value == "en-US");
        foreach (var keyboard in DefaultKeyboards(Formats.Value))
            Keyboards.Add(keyboard);
        TimeZone = TimeZoneChoices.FirstOrDefault(t => t.Value == TimeZoneInfo.Local.Id) ?? TimeZoneChoices[0];
        Keyboards.CollectionChanged += (_, _) => OnPropertyChanged(nameof(IsValid));
    }

    /// <summary>Выпуск образа — Windows 11 (обход требований нужен только ему).</summary>
    [ObservableProperty]
    public partial bool IsWindows11 { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ShowSimpleBypass))]
    public partial bool AdvancedMode { get; set; }

    public bool ShowSimpleBypass => !AdvancedMode;

    /// <summary>0 — задать пароль при первом входе, 1 — хранить в образе (для RadioButtons).</summary>
    public int PasswordModeIndex
    {
        get => StorePassword ? 1 : 0;
        set => StorePassword = value == 1;
    }

    // Требования
    [ObservableProperty] public partial bool BypassTpm { get; set; } = true;
    [ObservableProperty] public partial bool BypassSecureBoot { get; set; } = true;
    [ObservableProperty] public partial bool BypassRam { get; set; } = true;
    [ObservableProperty] public partial bool BypassCpu { get; set; } = true;

    /// <summary>Все четыре проверки разом — так это видит обычный режим.</summary>
    public bool BypassAll
    {
        get => BypassTpm && BypassSecureBoot && BypassRam && BypassCpu;
        set
        {
            BypassTpm = BypassSecureBoot = BypassRam = BypassCpu = value;
            OnPropertyChanged();
        }
    }

    // Учётная запись
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(UserNameError), nameof(HasUserNameError), nameof(CanAutoLogon))]
    public partial bool CreateUser { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(UserNameError), nameof(HasUserNameError))]
    public partial string UserName { get; set; } = "";

    /// <summary>Хранить пароль в образе (иначе — задать при первом входе).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(CanAutoLogon), nameof(PasswordError), nameof(HasPasswordError), nameof(PasswordModeIndex))]
    public partial bool StorePassword { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(PasswordError), nameof(HasPasswordError))]
    public partial string Password { get; set; } = "";

    [ObservableProperty]
    public partial bool AutoLogon { get; set; }

    // Первый запуск
    [ObservableProperty] public partial bool SkipOobe { get; set; } = true;
    [ObservableProperty] public partial bool BypassOnlineAccount { get; set; } = true;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(ComputerNameError), nameof(HasComputerNameError))]
    public partial string ComputerName { get; set; } = "";

    // Регион
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial bool SetRegional { get; set; } = true;

    [ObservableProperty]
    public partial Choice Formats { get; set; }

    [ObservableProperty]
    public partial Choice TimeZone { get; set; }

    public ObservableCollection<Choice> Keyboards { get; } = [];

    // Ключ и диск
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid), nameof(ProductKeyError), nameof(HasProductKeyError))]
    public partial string ProductKey { get; set; } = "";

    [ObservableProperty]
    public partial bool WipeDisk0 { get; set; }

    public static IReadOnlyList<Choice> CultureChoices { get; } = CultureInfo.GetCultures(CultureTypes.SpecificCultures)
        .Where(c => !string.IsNullOrEmpty(c.Name) && RegionalRules.CheckCulture(c.Name) is null)
        .Select(c => new Choice(c.Name, $"{c.DisplayName} — {c.Name}"))
        .OrderBy(c => c.Label, StringComparer.CurrentCulture)
        .ToList();

    public static IReadOnlyList<Choice> TimeZoneChoices { get; } = TimeZoneInfo.GetSystemTimeZones()
        .Select(t => new Choice(t.Id, t.DisplayName))
        .ToList();

    public bool CanAutoLogon => CreateUser && StorePassword;

    public string? UserNameError => CreateUser && NameRules.CheckUserName(UserName) is not null ? Text.Get("UserNameHint") : null;

    public bool HasUserNameError => UserNameError is not null;

    public string? PasswordError => CreateUser && StorePassword && NameRules.CheckPassword(Password) is not null ? Text.Get("PasswordHint") : null;

    public bool HasPasswordError => PasswordError is not null;

    public string? ComputerNameError => ComputerName.Length > 0 && NameRules.CheckComputerName(ComputerName) is not null
        ? Text.Get("ComputerNameHint")
        : null;

    public bool HasComputerNameError => ComputerNameError is not null;

    public string? ProductKeyError => ProductKey.Trim().Length > 0 && !IsProductKey(ProductKey) ? Text.Get("ProductKeyHint") : null;

    public bool HasProductKeyError => ProductKeyError is not null;

    public bool IsValid =>
        UserNameError is null && PasswordError is null && ComputerNameError is null && ProductKeyError is null &&
        (!SetRegional || Keyboards.Count > 0);

    public void AddKeyboard(Choice keyboard)
    {
        if (Keyboards.All(k => k.Value != keyboard.Value))
            Keyboards.Add(keyboard);
    }

    /// <summary>Первая раскладка в списке — основная.</summary>
    public void MakeDefaultKeyboard(Choice keyboard)
    {
        var index = Keyboards.IndexOf(keyboard);
        if (index > 0)
            Keyboards.Move(index, 0);
    }

    partial void OnStorePasswordChanged(bool value)
    {
        if (!value)
            AutoLogon = false;
    }

    /// <summary>Выборы шага для профиля: твики обхода, раздел setup и regional.</summary>
    public (IReadOnlyList<string> Tweaks, SetupDraft Setup, RegionalDraft? Regional) ToDraft()
    {
        var tweaks = !IsWindows11
            ? []
            : BypassTweaks.Where((_, i) => i switch
            {
                0 => BypassTpm,
                1 => BypassSecureBoot,
                2 => BypassRam,
                _ => BypassCpu,
            }).ToList();

        var user = CreateUser
            ? new UserDraft(UserName.Trim(), StorePassword, StorePassword ? Password : null, CanAutoLogon && AutoLogon)
            : null;
        var setup = new SetupDraft(
            user,
            ComputerName.Length > 0 ? ComputerName : null,
            SkipOobe,
            BypassOnlineAccount,
            AdvancedMode && WipeDisk0,
            ProductKey.Trim().Length > 0 ? ProductKey.Trim().ToUpperInvariant() : null);
        var regional = SetRegional
            ? new RegionalDraft(Formats.Value, Keyboards.Select(k => k.Value).ToList(), TimeZone.Value)
            : null;
        return (tweaks, setup, regional);
    }

    /// <summary>Что будет выбрано — строками для сводки на шаге «Результат».</summary>
    public IEnumerable<string> Describe()
    {
        var (tweaks, setup, regional) = ToDraft();
        if (tweaks.Count == BypassTweaks.Length)
            yield return Text.Get("SetupSummary_BypassAll");
        else if (tweaks.Count > 0)
            yield return Text.Format("SetupSummary_BypassSome", tweaks.Count);
        if (setup.User is { } u)
            yield return Text.Format(u.StorePassword ? "SetupSummary_UserStored" : "SetupSummary_UserChange", u.Name);
        if (setup.SkipOobe)
            yield return Text.Get("SetupSummary_SkipOobe");
        if (setup.BypassOnlineAccount)
            yield return Text.Get("SetupSummary_NoMicrosoftAccount");
        if (setup.ComputerName is { } name)
            yield return Text.Format("SetupSummary_ComputerName", name);
        if (regional is not null)
            yield return Text.Format("SetupSummary_Regional", Formats.Label, string.Join(", ", Keyboards.Select(k => k.Value)), TimeZone.Label);
        if (setup.WipeDisk0)
            yield return Text.Get("SetupSummary_WipeDisk");
    }

    static bool IsProductKey(string key) =>
        System.Text.RegularExpressions.Regex.IsMatch(key.Trim().ToUpperInvariant(), "^[A-Z0-9]{5}(-[A-Z0-9]{5}){4}$");

    // Свои форматы и английская раскладка — самый частый выбор (для русских форматов: русская + английская).
    static IEnumerable<Choice> DefaultKeyboards(string formats)
    {
        Choice? Find(string name) => CultureChoices.FirstOrDefault(c => c.Value == name);
        if (Find(formats) is { } own)
            yield return own;
        if (formats != "en-US" && Find("en-US") is { } english)
            yield return english;
    }
}
