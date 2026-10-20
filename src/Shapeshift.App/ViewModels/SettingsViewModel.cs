using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;

namespace Shapeshift.App.ViewModels;

/// <summary>Страница настроек: язык интерфейса, рабочая папка, кэш.</summary>
public sealed partial class SettingsViewModel : ObservableObject
{
    public static IReadOnlyList<Choice> Languages { get; } =
    [
        new("", Text.Get("LanguageSystem")),
        new("ru-RU", "Русский"),
        new("en-US", "English"),
    ];

    public SettingsViewModel()
    {
        var settings = AppSettings.Current;
        Language = Languages.FirstOrDefault(l => l.Value == (settings.Language ?? "")) ?? Languages[0];
        _startupLanguage = Language.Value;
        WorkspacePath = settings.EffectiveWorkspace;
        IsDefaultWorkspace = settings.Workspace is null;
    }

    readonly string _startupLanguage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(NeedsRestart))]
    public partial Choice Language { get; set; }

    [ObservableProperty]
    public partial string WorkspacePath { get; private set; }

    [ObservableProperty]
    public partial bool IsDefaultWorkspace { get; private set; }

    [ObservableProperty]
    public partial string CacheSize { get; private set; } = "…";

    [ObservableProperty]
    public partial bool IsClearing { get; private set; }

    /// <summary>Строки интерфейса загружаются при запуске — новый язык виден после перезапуска.</summary>
    public bool NeedsRestart => Language.Value != _startupLanguage;

    public string Version => typeof(SettingsViewModel).Assembly.GetName().Version?.ToString(3) ?? "";

    // В конструкторе язык выставляется из настроек — сохранять тогда нечего.
    partial void OnLanguageChanged(Choice oldValue, Choice newValue)
    {
        // До первого присваивания в конструкторе поле ещё не задано.
        if ((Choice?)oldValue is not null)
            Save(AppSettings.Current with { Language = newValue.Value.Length == 0 ? null : newValue.Value });
    }

    public void SetWorkspace(string? path)
    {
        Save(AppSettings.Current with { Workspace = path });
        WorkspacePath = AppSettings.Current.EffectiveWorkspace;
        IsDefaultWorkspace = path is null;
        _ = RefreshCacheSizeAsync();
    }

    public async Task RefreshCacheSizeAsync()
    {
        var path = WorkspacePath;
        var bytes = await Task.Run(() => WorkspaceFolder.Size(path));
        CacheSize = Text.Size(bytes);
    }

    /// <returns>false — часть файлов занята (например, идёт сборка).</returns>
    public async Task<bool> ClearCacheAsync()
    {
        IsClearing = true;
        try
        {
            var path = WorkspacePath;
            var ok = await Task.Run(() => WorkspaceFolder.Clear(path));
            await RefreshCacheSizeAsync();
            return ok;
        }
        finally
        {
            IsClearing = false;
        }
    }

    static void Save(AppSettings settings)
    {
        AppSettings.Current = settings;
        settings.Save();
    }
}
