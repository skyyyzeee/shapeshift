using DiscUtils;
using DiscUtils.Iso9660;
using DiscUtils.Udf;

namespace Shapeshift.Engine.Images;

/// <summary>Открытый на чтение ISO. Установочные образы Windows хранят файлы в UDF; ISO 9660 — запасной вариант.</summary>
public sealed class IsoImage : IDisposable
{
    readonly FileStream _stream;
    readonly DiscFileSystem _fs;

    IsoImage(FileStream stream, DiscFileSystem fs)
    {
        _stream = stream;
        _fs = fs;
    }

    /// <exception cref="InvalidDataException">Файл не является образом диска.</exception>
    public static IsoImage Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            if (UdfReader.Detect(stream))
                return new IsoImage(stream, new UdfReader(stream));
            if (CDReader.Detect(stream))
                return new IsoImage(stream, new CDReader(stream, joliet: true));
        }
        catch (Exception e) when (e is IOException or InvalidDataException or NotSupportedException)
        {
            stream.Dispose();
            throw new InvalidDataException("File is not a readable disc image.", e);
        }

        stream.Dispose();
        throw new InvalidDataException("File is not a disc image (no UDF or ISO 9660 file system).");
    }

    public string VolumeLabel => _fs.VolumeLabel ?? "";

    public bool FileExists(string path) => _fs.FileExists(path);

    public Stream OpenFile(string path) => _fs.OpenFile(path, FileMode.Open, FileAccess.Read);

    public IEnumerable<string> Directories() => _fs.GetDirectories("", "*.*", SearchOption.AllDirectories);

    public IEnumerable<string> Files() => _fs.GetFiles("", "*.*", SearchOption.AllDirectories);

    public long FileLength(string path) => _fs.GetFileLength(path);

    public void Dispose()
    {
        (_fs as IDisposable)?.Dispose();
        _stream.Dispose();
    }
}
