using Shapeshift.Core;

namespace Shapeshift.Engine.Images;

/// <summary>Образ нельзя использовать как источник: не установочный образ Windows или неподдерживаемый выпуск.</summary>
public sealed class ImageNotSupportedException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Исходный образ после проверки: что в нём лежит и где его загрузочные файлы.</summary>
public sealed record SourceImage(
    string IsoPath,
    string VolumeLabel,
    ImageInfo Info,
    string InstallImagePath,
    long ContentBytes,
    int FileCount,
    int LongestPathLength);

/// <summary>Проверяет, что ISO — установочный образ поддерживаемой Windows, и читает его содержимое.</summary>
public static class ImageInspector
{
    public const string BiosBootImage = @"boot\etfsboot.com";
    public const string UefiBootImage = @"efi\microsoft\boot\efisys.bin";
    const string SetupImage = @"sources\boot.wim";
    static readonly string[] InstallImages = [@"sources\install.wim", @"sources\install.esd"];

    // Значения ARCH в метаданных WIM.
    const int ArchX86 = 0;
    const int ArchAmd64 = 9;
    const int ArchArm64 = 12;

    public static SourceImage Inspect(string isoPath)
    {
        if (!File.Exists(isoPath))
            throw new ImageNotSupportedException($"Image not found: {isoPath}");

        IsoImage iso;
        try
        {
            iso = IsoImage.Open(isoPath);
        }
        catch (InvalidDataException e)
        {
            throw new ImageNotSupportedException($"{Path.GetFileName(isoPath)}: {e.Message}", e);
        }

        using (iso)
        {
            var installImage = InstallImages.FirstOrDefault(iso.FileExists);
            string[] required = [SetupImage, BiosBootImage, UefiBootImage];
            var missing = required.Where(p => !iso.FileExists(p)).ToList();
            if (installImage is null || missing.Count > 0)
            {
                if (installImage is null)
                    missing.Insert(0, @"sources\install.wim");
                throw new ImageNotSupportedException(
                    $"{Path.GetFileName(isoPath)} is not a Windows installation image (missing {string.Join(", ", missing)}).");
            }

            IReadOnlyList<WimImage> images;
            try
            {
                using var wim = iso.OpenFile(installImage);
                images = WimMetadata.Read(wim);
            }
            catch (InvalidDataException e)
            {
                throw new ImageNotSupportedException($"{installImage}: {e.Message}", e);
            }

            var info = ToImageInfo(images, installImage);
            var files = iso.Files().ToList();
            return new SourceImage(
                isoPath,
                iso.VolumeLabel,
                info,
                installImage,
                files.Sum(iso.FileLength),
                files.Count,
                files.Max(f => f.TrimStart('\\').Length));
        }
    }

    static ImageInfo ToImageInfo(IReadOnlyList<WimImage> images, string installImage)
    {
        if (images.Count == 0)
            throw new ImageNotSupportedException($"{installImage} contains no editions.");

        if (images.Any(i => !string.Equals(i.InstallationType, "Client", StringComparison.OrdinalIgnoreCase)))
            throw new ImageNotSupportedException(
                "Windows Server images are not supported; only Windows 10 and 11 client editions are.");

        var first = images[0];
        if (images.Any(i => i.Arch != first.Arch))
            throw new ImageNotSupportedException("Images with mixed architectures are not supported.");

        var architecture = first.Arch switch
        {
            ArchAmd64 => Architecture.X64,
            ArchArm64 => throw new ImageNotSupportedException("ARM64 images are not supported yet; only x64 is."),
            ArchX86 => throw new ImageNotSupportedException("32-bit (x86) images are not supported; only x64 is."),
            _ => throw new ImageNotSupportedException($"Unknown image architecture ({first.Arch?.ToString() ?? "none"})."),
        };

        if (first.Major != 10 || first.Build is not { } build)
            throw new ImageNotSupportedException("Only Windows 10 and 11 images are supported.");

        var release = new Release(build, first.SpBuild ?? 0);
        if (!release.IsSupported)
            throw new ImageNotSupportedException(
                $"{release} is too old; the oldest supported build is {Release.MinimumSupportedBuild} (Windows 10 2004).");

        var editions = images
            .Select(i => new Edition(i.Index, i.EditionId ?? "", i.Name))
            .ToList();

        return new ImageInfo(release, architecture, first.DefaultLanguage, editions);
    }
}
