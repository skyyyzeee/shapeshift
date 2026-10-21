using System.Diagnostics;
using ManagedWimLib;
using Shapeshift.Engine.Images;
using System.Security;
using System.Security.Cryptography;
using System.Text;

namespace Shapeshift.Engine.Tests.Support;

/// <summary>
/// Маленький выпуск UUP для тестов: файлы с настоящими хешами и <c>AggregatedMetadata.cab</c>
/// (CompDB редакций и приложений), упакованный системным <c>makecab</c> так же, как у Microsoft: каждый CompDB —
/// отдельный CAB внутри общего CAB. Образы редакций — настоящие WIM из трёх образов (установщик, среда восстановления,
/// Windows); данные Windows в них не лежат, а берутся по ссылкам из пакетов (WIM и CAB) — как у Microsoft.
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
        "Microsoft-Windows-Fake-FOD-Package~31bf3856ad364e35~amd64~~.cab",
        "Microsoft.BingNews_neutral.msixbundle",
        "Microsoft.BingNews_split.language-ru.appx",
        "Microsoft.VCLibs.140.00_x64.appx",
        "Windows11.0-KB5000001-x64.msu",
        "Windows11.0-KB5000002-x64.cab",
        "Windows11.0-KB5000003-x64.cab",
        "Windows11.0-KB5000004-x64-NDP481.cab",
        "DesktopDeployment.cab",
        $"{UpdateId}.AggregatedMetadata.cab",
    ];

    /// <summary>Возможности языка (FOD) для языков с ними в выпуске: базовые, затем рукописный ввод.</summary>
    public static string[] LanguageFeatures(string language) =>
    [
        $"Microsoft-Windows-LanguageFeatures-Basic-{language.ToLowerInvariant()}-Package~31bf3856ad364e35~amd64~~.cab",
        $"Microsoft-Windows-LanguageFeatures-Handwriting-{language.ToLowerInvariant()}-Package~31bf3856ad364e35~amd64~~.cab",
    ];

    /// <summary>Пакет .NET Framework 3.5 среди возможностей выпуска (функция NetFX3~).</summary>
    public const string NetFx3 = "Microsoft-Windows-NetFx3-OnDemand-Package~31bf3856ad364e35~amd64~~.cab";

    /// <summary>Языки, для которых есть языковой пакет; возможности — только у de-DE и ru-RU.</summary>
    public static readonly string[] LanguagePacks = ["de-DE", "en-US", "ru-RU"];

    /// <summary>Большой файл — чтобы скачивание можно было оборвать посередине.</summary>
    public const string Big = "Windows11.0-KB5000001-x64.msu";

    /// <summary>Содержимое файлов Windows в образах редакций: путь в образе → данные.</summary>
    public static readonly Dictionary<string, string> WindowsFiles = new()
    {
        [@"Windows\System32\kernel.dll"] = "foundation",
        [@"Windows\System32\fod.dll"] = "feature on demand from a CAB",
    };

    public FakeUupRelease(string scratch)
    {
        WimLib.EnsureInitialized();
        var wims = Path.Combine(scratch, "fake-uup-wims");
        Package("Microsoft-Windows-Foundation-Package.ESD", wims, (@"Windows\System32\kernel.dll", WindowsFiles[@"Windows\System32\kernel.dll"]));
        Package("Microsoft-Windows-EditionSpecific-Professional-Package.ESD", wims, (@"Windows\System32\professional.dll", "pro"));
        Package("Microsoft-Windows-EditionSpecific-Core-Package.ESD", wims, (@"Windows\System32\core.dll", "home"));
        foreach (var language in new[] { "ru-RU", "en-US", "de-DE" })
            Package($"Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~{language}~.esd", wims, ($@"Windows\System32\{language}\shell.mui", $"shell {language}"));
        var fod = Path.Combine(wims, "fod");
        Directory.CreateDirectory(fod);
        File.WriteAllText(Path.Combine(fod, "fod.dll"), WindowsFiles[@"Windows\System32\fod.dll"]);
        MakeCab([Path.Combine(fod, "fod.dll")], Path.Combine(wims, "fod.cab"));
        Files["Microsoft-Windows-Fake-FOD-Package~31bf3856ad364e35~amd64~~.cab"] = Retry(() => File.ReadAllBytes(Path.Combine(wims, "fod.cab")));
        var fodWim = WimOf(fod, Path.Combine(wims, "fod-ref.wim"));

        EditionImage("professional", "ru-RU", wims, fodWim);
        EditionImage("core", "ru-RU", wims, fodWim);
        EditionImage("professional", "en-US", wims, fodWim);
        Add("Microsoft.BingNews_neutral.msixbundle", 2048);
        Add("Microsoft.BingNews_split.language-ru.appx", 512);
        Add("Microsoft.BingNews_split.language-de.appx", 512);
        Add("Microsoft.VCLibs.140.00_x64.appx", 1024);
        Add("Microsoft.Paint_neutral.msixbundle", 1024);
        Add(Big, 3 << 20);
        Add("Windows11.0-KB5000002-x64.cab", 4096);
        Add("Windows11.0-KB5000002-x64.msu", 4096);
        Add("Windows11.0-KB5000002-x64-baseless.psf", 1024);
        Add("SSU-26100.1-x64.psf", 1024);
        Add("SSU-26100.1-x64-express.cab", 1024);
        Add("DesktopDeployment.cab", 1024);
        // Обновление среды восстановления (не для Windows) и .NET.
        Add("Windows11.0-KB5000003-x64.cab", 1024);
        Add("Windows11.0-KB5000004-x64-NDP481.cab", 1024);
        foreach (var feature in LanguageFeatures("de-DE").Concat(LanguageFeatures("ru-RU")))
            Add(feature, 768);
        Add(NetFx3, 1536);
        Files[$"{UpdateId}.AggregatedMetadata.cab"] = Metadata(scratch);
    }

    // Пакет с данными Windows: WIM с этими файлами.
    void Package(string name, string folder, params (string Path, string Content)[] files)
    {
        var dir = Path.Combine(folder, "pkg-" + Files.Count);
        foreach (var (path, content) in files)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(dir, path))!);
            File.WriteAllText(Path.Combine(dir, path), content);
        }
        Files[name] = Retry(() => File.ReadAllBytes(WimOf(dir, Path.Combine(folder, $"pkg-{Files.Count}.wim"))));
    }

    static string WimOf(string dir, string path)
    {
        using var wim = Wim.CreateNewWim(CompressionType.XPRESS);
        wim.AddImage(dir, "package", null!, AddFlags.NoAcls);
        Retry(() => wim.Write(path, Wim.AllImages, WriteFlags.None, 1));
        return path;
    }

    // Образ редакции: установщик, среда восстановления и Windows, чьи данные остаются в пакетах (SkipExternalWims).
    void EditionImage(string edition, string language, string folder, string fodWim)
    {
        var root = Path.Combine(folder, $"edition-{edition}-{language}");
        void Write(string file, string content)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(root, file))!);
            File.WriteAllText(Path.Combine(root, file), content);
        }

        Write(@"setup\setup.exe", "setup");
        Write(@"setup\bootmgr", "bootmgr");
        Write(@"setup\boot\etfsboot.com", new string('b', 4096));
        Write(@"setup\efi\microsoft\boot\efisys.bin", new string('e', 16384));
        Write(@"setup\sources\setup.dll", "setup engine");
        Write(@"setup\sources\background_cli.png", "background");
        Write(@"setup\sources\inf\setup.cfg", "[setup]");
        Write($@"setup\sources\{language.ToLowerInvariant()}\setup.exe.mui", $"setup {language}");
        Write(@"winre\Windows\System32\winpeshl.ini", "[LaunchApps]");
        Write(@"winre\Windows\System32\winre.txt", "recovery");
        foreach (var (file, content) in WindowsFiles)
            Write($@"windows\{file}", content);
        Write($@"windows\Windows\System32\{edition}.dll", edition == "professional" ? "pro" : "home");
        Write($@"windows\Windows\System32\{language}\shell.mui", $"shell {language}");
        Write(@"windows\Windows\edition.txt", edition);

        var path = Path.Combine(folder, $"{edition}_{language}.esd");
        using (var wim = Wim.CreateNewWim(CompressionType.XPRESS))
        {
            wim.AddImage(Path.Combine(root, "setup"), "Windows Setup Media", null!, AddFlags.NoAcls);
            wim.AddImage(Path.Combine(root, "winre"), "Microsoft Windows Recovery Environment (amd64)", null!, AddFlags.NoAcls);
            wim.AddImage(Path.Combine(root, "windows"), edition == "professional" ? "Windows 11 Pro" : "Windows 11 Home", null!, AddFlags.NoAcls);
            wim.SetImageProperty(3, "WINDOWS/EDITIONID", edition == "professional" ? "Professional" : "Core");
            // Данные Windows — в пакетах: образ редакции на них только ссылается.
            var packages = Files.Where(f => f.Key.EndsWith(".esd", StringComparison.OrdinalIgnoreCase) && f.Key.StartsWith("Microsoft-Windows-"))
                .Select((f, i) => { var p = Path.Combine(folder, $"ref-{edition}-{language}-{i}.wim"); Retry(() => File.WriteAllBytes(p, f.Value)); return p; })
                .Append(fodWim).ToList();
            wim.ReferenceResourceFiles(packages, RefFlags.None, OpenFlags.None);
            Retry(() => wim.Write(path, Wim.AllImages, WriteFlags.SkipExternalWims, 1));
        }
        Files[$"{edition}_{language.ToLowerInvariant()}.esd"] = Retry(() => File.ReadAllBytes(path));
    }

    // Антивирус ненадолго держит только что записанные файлы — несколько попыток.
    static T Retry<T>(Func<T> action)
    {
        for (var attempt = 1; ; attempt++)
        {
            try
            {
                return action();
            }
            catch (Exception e) when (e is IOException or WimLibException && attempt < 10)
            {
                Thread.Sleep(100 * attempt);
            }
        }
    }

    static void Retry(Action action) => Retry(() => { action(); return 0; });

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
            <CompDB xmlns="http://schemas.microsoft.com/embedded/2004/10/ImageUpdate" Name="Build~amd64~Desktop~{edition}~{language}" OSVersion="10.0.26100.1">
              <Features><Feature Type="DesktopMedia" FeatureID="{edition}_{language}"><Dependencies>
                <Feature FeatureID="Microsoft.BingNews_8wekyb3d8bbwe" Group="PreinstalledApps" Type="Optional" />
              </Dependencies></Feature></Features>
              <Packages>
                {Payload($"{edition}_{language}.esd")}
                {Payload("Microsoft-Windows-Foundation-Package.ESD")}
                {Payload(specific)}
                {Payload(languagePack)}
                {Payload("Microsoft-Windows-Fake-FOD-Package~31bf3856ad364e35~amd64~~.cab")}
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
            ["DesktopTargetCompDB_FOD_Neutral"] = FodNeutral(Payload),
        };
        // Языковые пакеты — по CompDB на язык, как у Microsoft (DesktopTargetCompDB_ru-RU).
        foreach (var language in LanguagePacks)
        {
            compDbs[$"DesktopTargetCompDB_{language}"] = $"""
                <?xml version="1.0" encoding="utf-8"?>
                <CompDB xmlns="http://schemas.microsoft.com/embedded/2004/10/ImageUpdate" Name="Build~amd64~Desktop~~{language}" OSVersion="10.0.26100.1">
                  <Features><Feature Type="LanguagePack" FeatureID="Language.UI.Client~{language}"><Packages><Package ID="Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~{language}~" /></Packages></Feature></Features>
                  <Packages>{Payload($"Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~{language}~.esd").Replace($"ID=\"Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~{language}~.esd\"", $"ID=\"Microsoft-Windows-Client-LanguagePack-Package~31bf3856ad364e35~amd64~{language}~\"")}</Packages>
                </CompDB>
                """;
        }

        var inner = new List<string>();
        foreach (var (name, xml) in compDbs)
        {
            var source = Path.Combine(folder, name.ToLowerInvariant() + ".xml.cab");
            File.WriteAllText(source, xml.TrimStart());
            var cab = Path.Combine(folder, "cabs", name + ".xml.cab");
            MakeCab([source], cab);
            inner.Add(cab);
        }
        // Метаданные обновлений: по их именам движок узнаёт накопительное, пакет включения и SafeOS.
        foreach (var name in new[] { "outer.AggregatedMetadata_KB5000001.cab", "EKBUpdateCompDB_KB5000002.xml.cab", "SafeOSDUCompDB_KB5000003.xml.cab" })
        {
            var source = Path.Combine(folder, "update-" + name + ".xml");
            File.WriteAllText(source, "<CompDB />");
            var cab = Path.Combine(folder, "cabs", name);
            MakeCab([source], cab);
            inner.Add(cab);
        }
        var outer = Path.Combine(folder, "AggregatedMetadata.cab");
        MakeCab(inner, outer);
        return File.ReadAllBytes(outer);
    }

    // Возможности языков: Handwriting зависит от Basic; посторонняя возможность (не языковая) не выбирается.
    static string FodNeutral(Func<string, string> payload)
    {
        var features = new System.Text.StringBuilder();
        var packages = new System.Text.StringBuilder();
        packages.Append(payload(NetFx3).Replace($"ID=\"{NetFx3}\"", "ID=\"Microsoft-Windows-NetFx3-OnDemand-Package\""));
        foreach (var language in new[] { "de-de", "ru-ru" })
        {
            var basic = $"Microsoft-Windows-LanguageFeatures-Basic-{language}-Package";
            var handwriting = $"Microsoft-Windows-LanguageFeatures-Handwriting-{language}-Package";
            features.Append($"""
                <Feature Type="OnDemandFeature" FeatureID="Language.Handwriting~{language}~1.0"><Dependencies><Feature FeatureID="Language.Basic~{language}~1.0" Type="Required" /></Dependencies><Packages><Package ID="{handwriting}" /></Packages></Feature>
                <Feature Type="OnDemandFeature" FeatureID="Language.Basic~{language}~1.0"><Packages><Package ID="{basic}" /></Packages></Feature>
                """);
            packages.Append(payload(basic + "~31bf3856ad364e35~amd64~~.cab").Replace($"ID=\"{basic}~31bf3856ad364e35~amd64~~.cab\"", $"ID=\"{basic}\""));
            packages.Append(payload(handwriting + "~31bf3856ad364e35~amd64~~.cab").Replace($"ID=\"{handwriting}~31bf3856ad364e35~amd64~~.cab\"", $"ID=\"{handwriting}\""));
        }
        return $"""
            <?xml version="1.0" encoding="utf-8"?>
            <CompDB xmlns="http://schemas.microsoft.com/embedded/2004/10/ImageUpdate" Name="Build~amd64~Desktop_FOD~~" OSVersion="10.0.26100.1">
              <Features>
                <Feature Type="OnDemandFeature" FeatureID="Microsoft.Windows.Notepad~~~~0.0.1.0"><Packages><Package ID="Microsoft-Windows-Notepad-FoD-Package" /></Packages></Feature>
                <Feature Type="OnDemandFeature" FeatureID="NetFX3~"><Packages><Package ID="Microsoft-Windows-NetFx3-OnDemand-Package" /></Packages></Feature>
                {features}
              </Features>
              <Packages>{packages}</Packages>
            </CompDB>
            """;
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
