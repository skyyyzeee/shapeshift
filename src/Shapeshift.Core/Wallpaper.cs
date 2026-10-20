namespace Shapeshift.Core;

/// <summary>Как картинка ложится на экран.</summary>
public enum WallpaperStyle
{
    Fill,
    Fit,
    Stretch,
    Center,
    Tile,
    Span,
}

/// <summary>Обои рабочего стола из своей картинки.</summary>
/// <param name="Path">Полный путь к картинке (.jpg, .jpeg, .png, .bmp) на компьютере, где собирается образ.</param>
/// <param name="Locked">
/// Закрепить политиками: у всех пользователей эти обои, сменить их нельзя («Управляется вашей организацией»).
/// Иначе — обои первого пользователя по умолчанию, которые можно поменять.
/// </param>
public sealed record WallpaperOptions(string Path, bool Locked, WallpaperStyle Style)
{
    public static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".bmp"];

    /// <summary>Где картинка окажется в установленной Windows (диск Windows при установке — C:).</summary>
    public string TargetPath => $@"C:\Windows\Web\Wallpaper\Shapeshift\wallpaper{System.IO.Path.GetExtension(Path).ToLowerInvariant()}";
}
