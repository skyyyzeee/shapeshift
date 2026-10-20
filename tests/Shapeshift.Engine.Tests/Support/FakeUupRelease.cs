using System.Diagnostics;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Shapeshift.Engine.Tests.Support;

/// <summary>
/// Маленький выпуск UUP для тестов скачивания: файлы с настоящими хешами и <c>AggregatedMetadata.cab</c>
/// (CompDB редакций и приложений), упакованный системным <c>makecab</c> так же, как у Microsoft: каждый CompDB —
/// отдельный CAB внутри общего CAB.
/// </summary>
public sealed class FakeUupRelease
{
    public const string UpdateId = "11111111-2222-3333-4444-555555555555";
    public const string Build = "26200.100";

    /// <summary>Файлы выпуска: имя → содержимое.</summary>
    public Dictionary<string, byte[]> Files { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Что нужно для ru-RU Professional — то, что должен выбрать движок.</summary>
    public static readonly string[] ProfessionalRussian =
    [
        "professional_ru-ru.esd",
        "Microsoft-Windows-Foundation-Package.ESD",
        "Microsoft-Windows-EditionSpecific-Professional-Package.ESD",
        "Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~ru-RU~.esd",
        "Microsoft.BingNews_neutral.msixbundle",
        "Microsoft.BingNews_split.language-ru.appx",
        "Microsoft.VCLibs.140.00_x64.appx",
        "Windows11.0-KB5000001-x64.msu",
        "Windows11.0-KB5000002-x64.cab",
        "DesktopDeployment.cab",
        $"{UpdateId}.AggregatedMetadata.cab",
    ];

    public FakeUupRelease(string scratch)
    {
        // Образ редакции — побольше, чтобы его можно было оборвать посередине.
        Add("professional_ru-ru.esd", 3 << 20);
        Add("core_ru-ru.esd", 4096);
        Add("professional_en-us.esd", 4096);
        Add("Microsoft-Windows-Foundation-Package.ESD", 2048);
        Add("Microsoft-Windows-EditionSpecific-Professional-Package.ESD", 2048);
        Add("Microsoft-Windows-EditionSpecific-Core-Package.ESD", 2048);
        Add("Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~ru-RU~.esd", 2048);
        Add("Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~en-US~.esd", 2048);
        Add("Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~de-DE~.esd", 2048);
        Add("Microsoft.BingNews_neutral.msixbundle", 2048);
        Add("Microsoft.BingNews_split.language-ru.appx", 512);
        Add("Microsoft.BingNews_split.language-de.appx", 512);
        Add("Microsoft.VCLibs.140.00_x64.appx", 1024);
        Add("Microsoft.Paint_neutral.msixbundle", 1024);
        Add("Windows11.0-KB5000001-x64.msu", 8192);
        Add("Windows11.0-KB5000002-x64.cab", 4096);
        Add("Windows11.0-KB5000002-x64.msu", 4096);
        Add("Windows11.0-KB5000002-x64-baseless.psf", 1024);
        Add("SSU-26100.1-x64.psf", 1024);
        Add("SSU-26100.1-x64-express.cab", 1024);
        Add("DesktopDeployment.cab", 1024);
        Files[$"{UpdateId}.AggregatedMetadata.cab"] = Metadata(scratch);
    }

    void Add(string name, int size)
    {
        var seed = Encoding.UTF8.GetBytes(name);
        Files[name] = Enumerable.Range(0, size).Select(i => (byte)(seed[i % seed.Length] ^ (i / seed.Length))).ToArray();
    }

    public static string Sha1(byte[] content) => Convert.ToHexStringLower(SHA1.HashData(content));

    static string Base64Sha1(byte[] content) => Convert.ToBase64String(SHA1.HashData(content));

    static string Base64Sha256(byte[] content) => Convert.ToBase64String(SHA256.HashData(content));

    byte[] Metadata(string scratch)
    {
        var folder = Path.Combine(scratch, "fake-uup-metadata");
        Directory.CreateDirectory(folder);
        string Payload(string name) => $"""<Package ID="{name}"><Payload><PayloadItem PayloadHash="{Base64Sha256(Files[name])}" PayloadSize="{Files[name].Length}" Path="UUP\{name}" /></Payload></Package>""";
        string Edition(string edition, string language, string specific, string languagePack) => $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CompDB xmlns="http://schemas.microsoft.com/embedded/2004/10/ImageUpdate" Name="Build~amd64~Desktop~{edition}~{language}">
              <Features><Feature Type="DesktopMedia" FeatureID="{edition}_{language}"><Dependencies>
                <Feature FeatureID="Microsoft.BingNews_8wekyb3d8bbwe" Group="PreinstalledApps" Type="Optional" />
              </Dependencies></Feature></Features>
              <Packages>
                {Payload($"{edition}_{language}.esd")}
                {Payload("Microsoft-Windows-Foundation-Package.ESD")}
                {Payload(specific)}
                {Payload(languagePack)}
              </Packages>
            </CompDB>
            """;
        const string apps = """
            <?xml version="1.0" encoding="utf-8"?>
            <CompDB xmlns="http://schemas.microsoft.com/embedded/2004/10/ImageUpdate" Name="Apps">
              <Features>
                <Feature FeatureID="Microsoft.BingNews_8wekyb3d8bbwe" Group="PreinstalledApps">
                  <Dependencies><Feature FeatureID="Microsoft.VCLibs.140.00_8wekyb3d8bbwe" Type="Required" /></Dependencies>
                  <Packages>
                    <Package ID="Microsoft.BingNews_1.0.0.0_neutral_~_8wekyb3d8bbwe" />
                    <Package ID="Microsoft.BingNews_1.0.0.0_neutral_split.language-ru_8wekyb3d8bbwe" />
                    <Package ID="Microsoft.BingNews_1.0.0.0_neutral_split.language-de_8wekyb3d8bbwe" />
                  </Packages>
                </Feature>
                <Feature FeatureID="Microsoft.VCLibs.140.00_8wekyb3d8bbwe"><Packages><Package ID="Microsoft.VCLibs.140.00_14.0.0.0_x64__8wekyb3d8bbwe" /></Packages></Feature>
                <Feature FeatureID="Microsoft.Paint_8wekyb3d8bbwe" Group="PreinstalledApps"><Packages><Package ID="Microsoft.Paint_1.0.0.0_neutral_~_8wekyb3d8bbwe" /></Packages></Feature>
              </Features>
              <Packages>
            """ + "\n";
        var appPackages = string.Concat(
            new[]
            {
                ("Microsoft.BingNews_1.0.0.0_neutral_~_8wekyb3d8bbwe", "Microsoft.BingNews_neutral.msixbundle"),
                ("Microsoft.BingNews_1.0.0.0_neutral_split.language-ru_8wekyb3d8bbwe", "Microsoft.BingNews_split.language-ru.appx"),
                ("Microsoft.BingNews_1.0.0.0_neutral_split.language-de_8wekyb3d8bbwe", "Microsoft.BingNews_split.language-de.appx"),
                ("Microsoft.VCLibs.140.00_14.0.0.0_x64__8wekyb3d8bbwe", "Microsoft.VCLibs.140.00_x64.appx"),
                ("Microsoft.Paint_1.0.0.0_neutral_~_8wekyb3d8bbwe", "Microsoft.Paint_neutral.msixbundle"),
            }.Select(p => Payload(p.Item2).Replace($"ID=\"{p.Item2}\"", $"ID=\"{p.Item1}\"")));

        var compDbs = new Dictionary<string, string>
        {
            ["DesktopTargetCompDB_professional_ru-ru"] = Edition("professional", "ru-ru", "Microsoft-Windows-EditionSpecific-Professional-Package.ESD", "Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~ru-RU~.esd"),
            ["DesktopTargetCompDB_core_ru-ru"] = Edition("core", "ru-ru", "Microsoft-Windows-EditionSpecific-Core-Package.ESD", "Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~ru-RU~.esd"),
            ["DesktopTargetCompDB_professional_en-us"] = Edition("professional", "en-us", "Microsoft-Windows-EditionSpecific-Professional-Package.ESD", "Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~en-US~.esd"),
            ["DesktopTargetCompDB_App_Neutral"] = apps + appPackages + "</Packages></CompDB>",
        };

        var inner = new List<string>();
        foreach (var (name, xml) in compDbs)
        {
            var source = Path.Combine(folder, name.ToLowerInvariant() + ".xml.cab");
            File.WriteAllText(source, xml.TrimStart());
            var cab = Path.Combine(folder, "cabs", name + ".xml.cab");
            MakeCab([source], cab);
            inner.Add(cab);
        }
        var outer = Path.Combine(folder, "AggregatedMetadata.cab");
        MakeCab(inner, outer);
        return File.ReadAllBytes(outer);
    }

    // makecab с файлом указаний: несколько файлов в один CAB, без служебных setup.inf / setup.rpt.
    static void MakeCab(IReadOnlyList<string> files, string cab)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(cab)!);
        var ddf = cab + ".ddf";
        File.WriteAllLines(ddf, [
            ".Set CabinetNameTemplate=" + Path.GetFileName(cab),
            ".Set DiskDirectoryTemplate=" + Path.GetDirectoryName(cab),
            ".Set Cabinet=on", ".Set Compress=on", ".Set MaxDiskSize=0",
            ".Set InfFileName=NUL", ".Set RptFileName=NUL",
            .. files.Select(f => $"\"{f}\" \"{Path.GetFileName(f)}\""),
        ]);
        var start = new ProcessStartInfo(Path.Combine(Environment.SystemDirectory, "makecab.exe"), ["/F", ddf])
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            WorkingDirectory = Path.GetDirectoryName(cab)!,
        };
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0 || !File.Exists(cab))
            throw new InvalidOperationException($"makecab failed for {cab}");
    }

    /// <summary>Ответ SyncUpdates с этим выпуском — по форме как у Windows Update.</summary>
    public string SyncResponse()
    {
        var files = string.Concat(Files.Select(f =>
            $"""<File FileName="{f.Key}" Digest="{Base64Sha1(f.Value)}" DigestAlgorithm="SHA1" Size="{f.Value.Length}"><AdditionalDigest Algorithm="SHA256">{Base64Sha256(f.Value)}</AdditionalDigest></File>"""));
        var info = $"""<UpdateIdentity UpdateID="{UpdateId}" RevisionNumber="1" /><ApplicabilityRules><IsInstalled><ProductReleaseInstalled Name="Client.OS.RS2.amd64" Version="10.0.{Build}" /></IsInstalled></ApplicabilityRules>""";
        var title = "<LocalizedProperties><Language>en</Language><Title>Windows 11, version 25H2</Title></LocalizedProperties>";
        var extended = $"""<ExtendedProperties ProductName="Client.OS.RS2.amd64" ReleaseVersion="10.0.{Build}" /><Files>{files}</Files>""";
        return $"""
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><SyncUpdatesResponse xmlns="http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService"><SyncUpdatesResult>
            <NewUpdates><UpdateInfo><ID>1</ID><IsLeaf>true</IsLeaf><Xml>{SecurityElement.Escape(info)}</Xml></UpdateInfo></NewUpdates>
            <ExtendedUpdateInfo><Updates><Update><ID>1</ID><Xml>{SecurityElement.Escape(title)}</Xml></Update><Update><ID>1</ID><Xml>{SecurityElement.Escape(extended)}</Xml></Update></Updates></ExtendedUpdateInfo>
            </SyncUpdatesResult></SyncUpdatesResponse></s:Body></s:Envelope>
            """;
    }

    /// <summary>Ответ GetExtendedUpdateInfo2: ссылки на все файлы (версия ссылок меняется при каждом запросе).</summary>
    public string UrlsResponse(int version)
    {
        var locations = string.Concat(Files.Values.Select(content =>
            $"<FileLocation><FileDigest>{Base64Sha1(content)}</FileDigest><Url>https://download.example.test/{Sha1(content)}?v={version}</Url></FileLocation>"));
        return $"""
            <s:Envelope xmlns:s="http://www.w3.org/2003/05/soap-envelope"><s:Body><GetExtendedUpdateInfo2Response xmlns="http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService"><GetExtendedUpdateInfo2Result><FileLocations>{locations}</FileLocations></GetExtendedUpdateInfo2Result></GetExtendedUpdateInfo2Response></s:Body></s:Envelope>
            """;
    }

    public byte[]? ContentBySha1(string sha1) => Files.Values.FirstOrDefault(c => Sha1(c) == sha1);
}
