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
}
