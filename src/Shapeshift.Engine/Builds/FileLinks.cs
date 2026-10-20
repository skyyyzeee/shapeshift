using System.Runtime.InteropServices;

namespace Shapeshift.Engine.Builds;

/// <summary>Жёсткая ссылка вместо копии, когда файл и цель на одном томе (гигабайтные обновления); иначе — копия.</summary>
static class FileLinks
{
    public static void LinkOrCopy(string source, string target)
    {
        if (!CreateHardLink(target, source, IntPtr.Zero))
            File.Copy(source, target, overwrite: true);
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateHardLink(string fileName, string existingFileName, IntPtr securityAttributes);
}
