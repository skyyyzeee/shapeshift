using Microsoft.Windows.ApplicationModel.Resources;

namespace Shapeshift.App.Services;

/// <summary>Строки интерфейса из Strings/&lt;язык&gt;/Resources.resw. Язык — по настройкам Windows.</summary>
public static class Text
{
    static readonly ResourceLoader Loader = new();

    public static string Get(string key)
    {
        var value = Loader.GetString(key);
        return string.IsNullOrEmpty(value) ? key : value;
    }

    public static string Format(string key, params object[] args) => string.Format(Get(key), args);

    /// <summary>Размер по-человечески: «9,1 ГБ» или «350,0 МБ».</summary>
    public static string Size(long bytes) => bytes >= 1L << 30
        ? string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.0} {1}", bytes / (double)(1L << 30), Get("UnitGB"))
        : string.Format(System.Globalization.CultureInfo.CurrentCulture, "{0:0.0} {1}", bytes / (double)(1L << 20), Get("UnitMB"));
}
