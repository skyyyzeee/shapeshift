using CommunityToolkit.Mvvm.ComponentModel;
using Shapeshift.App.Services;
using Shapeshift.Core;

namespace Shapeshift.App.ViewModels;

/// <summary>Обои рабочего стола: картинка копируется в образ; закреплённые нельзя сменить в Windows.</summary>
public sealed partial class WallpaperViewModel : ObservableObject
{
    /// <summary>Порядок совпадает с <see cref="WallpaperStyle"/>.</summary>
    public IReadOnlyList<string> StyleChoices { get; } = Enum.GetValues<WallpaperStyle>().Select(s => Text.Get($"WallpaperStyle_{s}")).ToList();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasPicture), nameof(FileName))]
    public partial string? Path { get; set; }

    public bool HasPicture => Path is not null;

    public string FileName => Path is null ? "" : System.IO.Path.GetFileName(Path);

    [ObservableProperty]
    public partial bool Locked { get; set; } = true;

    [ObservableProperty]
    public partial int StyleIndex { get; set; }

    WallpaperStyle Style => StyleIndex >= 0 && StyleIndex < StyleChoices.Count ? (WallpaperStyle)StyleIndex : WallpaperStyle.Fill;

    public void Clear() => Path = null;

    public void ApplyProfile(WallpaperOptions? wallpaper)
    {
        Path = wallpaper?.Path;
        Locked = wallpaper?.Locked ?? true;
        StyleIndex = (int)(wallpaper?.Style ?? WallpaperStyle.Fill);
    }

    public WallpaperDraft? ToDraft() => Path is null ? null : new WallpaperDraft(Path, Locked, Style.ToString().ToLowerInvariant());

    /// <summary>Строка сводки перед сборкой; null — обои не заданы.</summary>
    public string? Summary => Path is null
        ? null
        : Text.Format(Locked ? "CustomizeSummary_WallpaperLocked" : "CustomizeSummary_Wallpaper", FileName, StyleChoices[(int)Style]);
}
