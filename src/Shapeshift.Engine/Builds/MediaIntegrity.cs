using System.Security.Cryptography;
using System.Text.Json;
using Shapeshift.Engine.Images;

namespace Shapeshift.Engine.Builds;

/// <summary>
/// Проверка, что крупные файлы установки (install.wim/esd, boot.wim) дошли до ISO целыми. На стенде однажды
/// попался ISO с испорченным install.wim того же размера (Setup: 0x80070570), воспроизвести не удалось — поэтому
/// сборка сверяет: копию на диске сборки — с хешем, посчитанным при распаковке (если файл потом не меняли),
/// и файлы в готовом ISO — с копией на диске.
/// </summary>
static class MediaIntegrity
{
    /// <summary>Файлы носителя, без которых установка не пройдёт; проверяются только они — остальное мелочь.</summary>
    public static readonly string[] CriticalFiles = [@"sources\install.wim", @"sources\install.esd", @"sources\boot.wim"];

    const string LedgerName = "hashes.json";

    sealed record Entry(string Sha256, DateTime WrittenUtc);

    public static bool IsCritical(string relativePath) =>
        CriticalFiles.Contains(relativePath.TrimStart('\\'), StringComparer.OrdinalIgnoreCase);

    /// <summary>Поток-обёртка для распаковки: хеш считается по ходу записи, без повторного чтения.</summary>
    public static IncrementalHash StartHash() => IncrementalHash.CreateHash(HashAlgorithmName.SHA256);

    /// <summary>Запомнить хеш распакованного файла (и время записи — чтобы отличить порчу от изменения шагом сборки).</summary>
    public static void Record(string buildDirectory, string mediaDirectory, string relativePath, IncrementalHash hash)
    {
        var ledger = Load(buildDirectory);
        var key = CriticalFiles.First(f => string.Equals(f, relativePath.TrimStart('\\'), StringComparison.OrdinalIgnoreCase));
        var path = Path.Combine(mediaDirectory, key);
        ledger[key] = new Entry(Convert.ToHexString(hash.GetHashAndReset()), File.GetLastWriteTimeUtc(path));
        File.WriteAllText(Path.Combine(buildDirectory, LedgerName), JsonSerializer.Serialize(ledger));
    }

    /// <summary>
    /// Хеши критичных файлов на диске сборки. Файл, который после распаковки не меняли, должен совпасть с хешем
    /// распаковки — иначе он испорчен на диске (сбой диска или памяти), и собирать из него нельзя.
    /// </summary>
    /// <exception cref="IOException">Копия на диске сборки испорчена.</exception>
    public static IReadOnlyDictionary<string, string> HashMedia(string buildDirectory, string mediaDirectory, Action<double> progress, CancellationToken ct)
    {
        var ledger = Load(buildDirectory);
        var present = CriticalFiles.Where(f => File.Exists(Path.Combine(mediaDirectory, f))).ToList();
        var total = Math.Max(1, present.Sum(f => new FileInfo(Path.Combine(mediaDirectory, f)).Length));
        long done = 0;
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in present)
        {
            var path = Path.Combine(mediaDirectory, file);
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20))
                result[file] = Hash(stream, bytes => progress((double)(done += bytes) / total), ct);
            if (ledger.TryGetValue(file, out var extracted) && File.GetLastWriteTimeUtc(path) == extracted.WrittenUtc &&
                !string.Equals(extracted.Sha256, result[file], StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException(
                    $"{file} was damaged on the build disk after it was copied from the source image (its contents changed by themselves). " +
                    "This points to a disk or memory problem; check the disk with the workspace and build again.");
            }
        }
        return result;
    }

    /// <summary>Файлы в готовом ISO, которые не совпали с копией на диске сборки.</summary>
    public static IReadOnlyList<string> Mismatches(string isoPath, IReadOnlyDictionary<string, string> expected, Action<double> progress, CancellationToken ct)
    {
        using var iso = IsoImage.Open(isoPath);
        var total = Math.Max(1, expected.Keys.Sum(f => iso.FileExists(f) ? iso.FileLength(f) : 0));
        long done = 0;
        var damaged = new List<string>();
        foreach (var (file, sha256) in expected)
        {
            if (!iso.FileExists(file))
            {
                damaged.Add(file);
                continue;
            }
            using var stream = iso.OpenFile(file);
            if (!string.Equals(Hash(stream, bytes => progress((double)(done += bytes) / total), ct), sha256, StringComparison.OrdinalIgnoreCase))
                damaged.Add(file);
        }
        return damaged;
    }

    static string Hash(Stream stream, Action<long> advance, CancellationToken ct)
    {
        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[4 << 20];
        int n;
        while ((n = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            ct.ThrowIfCancellationRequested();
            sha.AppendData(buffer, 0, n);
            advance(n);
        }
        return Convert.ToHexString(sha.GetHashAndReset());
    }

    static Dictionary<string, Entry> Load(string buildDirectory)
    {
        var path = Path.Combine(buildDirectory, LedgerName);
        return File.Exists(path) && JsonSerializer.Deserialize<Dictionary<string, Entry>>(File.ReadAllText(path)) is { } saved
            ? new(saved, StringComparer.OrdinalIgnoreCase)
            : new(StringComparer.OrdinalIgnoreCase);
    }
}
