using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Xml.Linq;

namespace Shapeshift.Engine.Uup;

/// <summary>Windows Update не ответил или ответил ошибкой; сообщение — для пользователя.</summary>
public sealed class WindowsUpdateException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>Что спросить у Windows Update: «устройство» какой версии и в каком канале ищет обновление.</summary>
/// <param name="Ring">RETAIL, RP (Release Preview), WIS (Beta), WIF (Dev), CANARY.</param>
/// <param name="Build">Версия «устройства», например 10.0.22631.1.</param>
/// <param name="Branch">Ветка разработки этой версии, например ni_release.</param>
/// <param name="ThisVersionOnly">Только обновления этой же версии, без перехода на следующую.</param>
/// <param name="MaxMajor">Не брать сборки новее (например, 19999 — только Windows 10); null — любые.</param>
public sealed record UpdateQuery(string Ring, string Build, string Branch, bool ThisVersionOnly, int? MaxMajor = null);

/// <summary>Файл выпуска UUP: имя, размер и хеши из ответа Windows Update.</summary>
public sealed record UupFile(string Name, long Size, string Sha1, string? Sha256);

/// <summary>Выпуск, найденный в Windows Update: обновление ОС и его файлы.</summary>
/// <param name="UpdateId">Идентификатор обновления (GUID) — по нему потом получаются ссылки на файлы.</param>
/// <param name="Build">Номер сборки, например 26200.9457.</param>
public sealed record FoundRelease(string UpdateId, int Revision, string Title, string Build, IReadOnlyList<UupFile> Files);

/// <summary>
/// Прямые запросы к службе Windows Update (FE3, <c>client.asmx</c>) — без сторонних сайтов (ADR-0001).
/// Клиент представляется анонимным устройством x64 с нужной версией Windows и каналом.
/// </summary>
public sealed class WindowsUpdateClient(HttpClient http)
{
    public const string Endpoint = "https://fe3.delivery.mp.microsoft.com/ClientWebService/client.asmx";
    const string UserAgent = "Windows-Update-Agent/10.0.10011.16384 Client-Protocol/2.50";

    static readonly XNamespace Ws = "http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService";

    string? _cookie;

    /// <summary>Получает сессию службы. Нужно один раз до параллельных запросов.</summary>
    public async Task<string> ConnectAsync(CancellationToken ct) => _cookie ??= await GetCookieAsync(ct);

    /// <summary>Самый новый выпуск ОС, который Windows Update предложил бы такому устройству; null — ничего не предложил.</summary>
    public async Task<FoundRelease?> FindReleaseAsync(UpdateQuery query, CancellationToken ct)
    {
        var cookie = await ConnectAsync(ct);
        var response = await PostAsync("SyncUpdates", SyncUpdatesBody(query, cookie), ct);
        return ParseRelease(response, query.MaxMajor);
    }

    async Task<string> GetCookieAsync(CancellationToken ct)
    {
        var now = Timestamp(DateTime.UtcNow);
        var response = await PostAsync("GetCookie", $"""
            <GetCookie xmlns="{Ws}"><oldCookie><Expiration>{now}</Expiration></oldCookie><lastChange>{now}</lastChange><currentTime>{now}</currentTime><protocolVersion>2.0</protocolVersion></GetCookie>
            """, ct);
        return response.Descendants(Ws + "EncryptedData").FirstOrDefault()?.Value
            ?? throw new WindowsUpdateException("Windows Update returned no session cookie.");
    }

    async Task<XDocument> PostAsync(string action, string body, CancellationToken ct)
    {
        var envelope = $"""
            <s:Envelope xmlns:a="http://www.w3.org/2005/08/addressing" xmlns:s="http://www.w3.org/2003/05/soap-envelope">{Header(action)}<s:Body>{body}</s:Body></s:Envelope>
            """;
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "application/soap+xml"),
        };
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent);

        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, ct);
        }
        catch (HttpRequestException e)
        {
            throw new WindowsUpdateException($"cannot reach Windows Update ({e.Message}).", e);
        }
        catch (TaskCanceledException e) when (!ct.IsCancellationRequested)
        {
            throw new WindowsUpdateException("Windows Update did not answer in time.", e);
        }

        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(ct);
            // Для отладки и записи ответов в тестовые данные: SHAPESHIFT_WU_DUMP=<папка>.
            if (Environment.GetEnvironmentVariable("SHAPESHIFT_WU_DUMP") is { Length: > 0 } dump)
            {
                Directory.CreateDirectory(dump);
                await File.WriteAllTextAsync(Path.Combine(dump, $"{DateTime.UtcNow:HHmmssfff}-{action}-{Guid.NewGuid():N}.xml"), text, ct);
            }
            if (response.StatusCode != HttpStatusCode.OK)
                throw new WindowsUpdateException($"Windows Update answered {(int)response.StatusCode} to {action}.");
            try
            {
                return XDocument.Parse(text);
            }
            catch (System.Xml.XmlException e)
            {
                throw new WindowsUpdateException($"Windows Update sent an unreadable answer to {action}.", e);
            }
        }
    }

    // Среди «листовых» обновлений ответа — обновление ОС (ProductReleaseInstalled Client.OS…): номер сборки,
    // название и список файлов берутся из его фрагментов.
    static FoundRelease? ParseRelease(XDocument response, int? maxMajor)
    {
        var result = response.Descendants(Ws + "SyncUpdatesResult").FirstOrDefault();
        if (result is null)
            return null;

        var fragments = result.Element(Ws + "ExtendedUpdateInfo")?.Element(Ws + "Updates")?.Elements(Ws + "Update")
            .Select(u => (Id: u.Element(Ws + "ID")?.Value, Xml: Fragment(u.Element(Ws + "Xml")?.Value)))
            .Where(f => f.Id is not null && f.Xml is not null)
            .ToList() ?? [];

        var candidates = new List<FoundRelease>();
        foreach (var info in result.Element(Ws + "NewUpdates")?.Elements(Ws + "UpdateInfo") ?? [])
        {
            if (info.Element(Ws + "IsLeaf")?.Value != "true")
                continue;
            var id = info.Element(Ws + "ID")?.Value;
            var xml = Fragment(info.Element(Ws + "Xml")?.Value);
            var product = xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "ProductReleaseInstalled");
            var identity = xml?.Descendants().FirstOrDefault(e => e.Name.LocalName == "UpdateIdentity");
            var productName = (string?)product?.Attribute("Name");
            if (productName is null || !productName.StartsWith("Client.OS.", StringComparison.OrdinalIgnoreCase) || identity is null)
                continue;

            var mine = fragments.Where(f => f.Id == id).Select(f => f.Xml!).ToList();
            var title = mine.SelectMany(x => x.DescendantsAndSelf()).FirstOrDefault(e => e.Name.LocalName == "Title")?.Value ?? "Windows";
            var files = mine.SelectMany(x => x.DescendantsAndSelf()).Where(e => e.Name.LocalName == "File").Select(ToFile).ToList();
            var version = (string?)product!.Attribute("Version") ?? "";
            candidates.Add(new FoundRelease(
                (string)identity.Attribute("UpdateID")!,
                int.Parse((string?)identity.Attribute("RevisionNumber") ?? "1", CultureInfo.InvariantCulture),
                title,
                version.StartsWith("10.0.", StringComparison.Ordinal) ? version["10.0.".Length..] : version,
                files));
        }
        // Если предложено несколько (например, Windows 10 и Windows 11), берётся с самой новой сборкой;
        // какие нужны, решает тот, кто спрашивает (UupChannels).
        return candidates
            .Where(c => maxMajor is null || BuildKey(c.Build).Major <= maxMajor)
            .OrderByDescending(c => BuildKey(c.Build))
            .FirstOrDefault();
    }

    /// <summary>Сборка «26200.9457» → (26200, 9457) для сравнения.</summary>
    internal static (int Major, int Minor) BuildKey(string build)
    {
        var parts = build.Split('.');
        return (Parse(parts.ElementAtOrDefault(0)), Parse(parts.ElementAtOrDefault(1)));
        static int Parse(string? s) => int.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
    }

    static UupFile ToFile(XElement file) => new(
        (string)file.Attribute("FileName")!,
        long.Parse((string?)file.Attribute("Size") ?? "0", CultureInfo.InvariantCulture),
        Hex((string?)file.Attribute("Digest")),
        file.Elements().FirstOrDefault(e => e.Name.LocalName == "AdditionalDigest" && (string?)e.Attribute("Algorithm") == "SHA256") is { } sha256
            ? Hex(sha256.Value)
            : null);

    static string Hex(string? base64) => base64 is null ? "" : Convert.ToHexStringLower(Convert.FromBase64String(base64));

    // Фрагменты — XML без общего корня, переданный строкой.
    static XElement? Fragment(string? xml)
    {
        if (string.IsNullOrEmpty(xml))
            return null;
        try
        {
            return XElement.Parse($"<Fragment>{xml}</Fragment>");
        }
        catch (System.Xml.XmlException)
        {
            return null;
        }
    }

    static string Header(string action)
    {
        var now = DateTime.UtcNow;
        return $"""
            <s:Header><a:Action s:mustUnderstand="1">http://www.microsoft.com/SoftwareDistribution/Server/ClientWebService/{action}</a:Action><a:MessageID>urn:uuid:{Guid.NewGuid()}</a:MessageID><a:To s:mustUnderstand="1">{Endpoint}</a:To><o:Security s:mustUnderstand="1" xmlns:o="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd"><Timestamp xmlns="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd"><Created>{Timestamp(now)}</Created><Expires>{Timestamp(now.AddMinutes(2))}</Expires></Timestamp><wuws:WindowsUpdateTicketsToken wsu:id="ClientMSA" xmlns:wsu="http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd" xmlns:wuws="http://schemas.microsoft.com/msus/2014/10/WindowsUpdateAuthorization"><TicketType Name="MSA" Version="1.0" Policy="MBI_SSL"><Device>{AnonymousDevice()}</Device></TicketType></wuws:WindowsUpdateTicketsToken></o:Security></s:Header>
            """;
    }

    static string Timestamp(DateTime utc) => utc.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);

    // Билет «устройства» без учётной записи: служебный заголовок, случайное тело. Windows Update его принимает
    // как анонимное устройство; так же делают известные открытые клиенты UUP.
    static string AnonymousDevice()
    {
        var hex = "13003002c377040014d5bcac7a66de0d50beddf9bba16c87edb9e019898000" +
            Convert.ToHexStringLower(System.Security.Cryptography.RandomNumberGenerator.GetBytes(527)) +
            "b401";
        var token = $"t={Convert.ToBase64String(Convert.FromHexString(hex))}&p=";
        return Convert.ToBase64String(Encoding.Unicode.GetBytes(token));
    }

    static string SyncUpdatesBody(UpdateQuery query, string cookie)
    {
        var products = Escape($"PN=Client.OS.rs2.amd64&Branch={query.Branch}&PrimaryOSProduct=1&Repairable=1&V={query.Build}&ReofferUpdate=1");
        var caller = Escape("E:Profile=AUv2&Acquisition=1&Interactive=1&IsSeeker=1&SheddingAware=1&Id=MoUpdateOrchestrator");
        var installed = string.Concat(InstalledNonLeafUpdateIds.Select(id => $"<int>{id}</int>"));
        return $"""
            <SyncUpdates xmlns="{Ws}"><cookie><Expiration>{Timestamp(DateTime.UtcNow.AddDays(7))}</Expiration><EncryptedData>{cookie}</EncryptedData></cookie><parameters><ExpressQuery>false</ExpressQuery><InstalledNonLeafUpdateIDs>{installed}</InstalledNonLeafUpdateIDs><OtherCachedUpdateIDs/><SkipSoftwareSync>false</SkipSoftwareSync><NeedTwoGroupOutOfScopeUpdates>true</NeedTwoGroupOutOfScopeUpdates><AlsoPerformRegularSync>true</AlsoPerformRegularSync><ComputerSpec/><ExtendedUpdateInfoParameters><XmlUpdateFragmentTypes><XmlUpdateFragmentType>Extended</XmlUpdateFragmentType><XmlUpdateFragmentType>LocalizedProperties</XmlUpdateFragmentType></XmlUpdateFragmentTypes><Locales><string>en-US</string></Locales></ExtendedUpdateInfoParameters><ClientPreferredLanguages/><ProductsParameters><SyncCurrentVersionOnly>{(query.ThisVersionOnly ? "true" : "false")}</SyncCurrentVersionOnly><DeviceAttributes>{Escape(DeviceAttributes(query))}</DeviceAttributes><CallerAttributes>{caller}</CallerAttributes><Products>{products}</Products></ProductsParameters></parameters></SyncUpdates>
            """;
    }

    static string Escape(string text) => SecurityElement.Escape(text);

    // «Устройство», которому Windows Update предлагает обновление: совместимое (TPM 2.0, Secure Boot), с согласием
    // на обновление версии и с нужным каналом. Без этих признаков служба ничего не предлагает.
    internal static string DeviceAttributes(UpdateQuery query)
    {
        var retail = query.Ring == "RETAIL";
        var (flightRing, flightBranch) = query.Ring switch
        {
            "RETAIL" => ("Retail", ""),
            "RP" => ("External", "ReleasePreview"),
            "WIS" => ("External", "Beta"),
            "WIF" => ("External", "Dev"),
            "CANARY" => ("External", "CanaryChannel"),
            _ => throw new ArgumentOutOfRangeException(nameof(query), query.Ring, "unknown ring"),
        };
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var expires = (now + 82800).ToString(CultureInfo.InvariantCulture);
        var stamp = (now - 3600).ToString(CultureInfo.InvariantCulture);
        string[] releases = ["GE25H2", "GE24H2", "GE24H2Setup", "CU23H2", "CU23H2Setup", "NI22H2", "NI22H2Setup", "CO21H2", "CO21H2Setup", "23H2", "22H2", "21H2", "21H1", "20H1", "19H1"];
        string[] green = ["GE25H2", "GE24H2Setup", "GE24H2", "CU23H2", "NI23H2", "NI22H2", "CO21H2", "23H2", "22H2", "21H2", "21H1", "20H1", "19H1", "RS5"];
        string[] gstatus = ["GE25H2", "GE24H2", "GE24H2Setup", "CU23H2", "CU23H2Setup", "NI23H2", "NI22H2", "NI22H2Setup", "CO21H2", "CO21H2Setup", "22H2", "21H2", "21H1", "20H1", "20H1Setup", "19H1", "19H1Setup", "RS5"];

        var attributes = new List<string>
        {
            "App=WU_OS", $"AppVer={query.Build}", "AttrDataVer=331", "AllowInPlaceUpgrade=1", "AllowOptionalContent=1",
            "AllowUpgradesWithUnsupportedTPMOrCPU=1", "BlockFeatureUpdates=0", "BranchReadinessLevel=CB", "CIOptin=1",
            $"CurrentBranch={query.Branch}",
        };
        attributes.AddRange(releases.Select(r => $"DataExpDateEpoch_{r}={expires}"));
        attributes.AddRange([
            "DataVer_RS5=2000000000", "DefaultUserRegion=191", "DeviceFamily=Windows.Desktop", "DeviceInfoGatherSuccessful=1",
            "EKB19H2InstallCount=1", "EKB19H2InstallTimeEpoch=1255000000", $"FlightingBranchName={flightBranch}",
            $"FlightRing={flightRing}", "Free=gt64",
        ]);
        attributes.AddRange(gstatus.Select(r => $"GStatus_{r}=2"));
        attributes.AddRange([
            $"GenTelRunTimestamp_19H1={stamp}", "InstallDate=1438196400", "InstallLanguage=en-US", "InstallationType=Client",
            "IsDeviceRetailDemo=0", $"IsFlightingEnabled={(retail ? 0 : 1)}", $"IsRetailOS={(retail ? 1 : 0)}", "LCUVer=0.0.0.0",
            $"MediaBranch={(query.ThisVersionOnly ? query.Branch : "")}", $"MediaVersion={query.Build}", "CloudPBR=1", "DUScan=1",
            "OEMModel=21F6CTO1WW", "OEMModelBaseBoard=21F6CTO1WW", "OEMName_Uncleaned=LENOVO", "OemPartnerRing=UPSFlighting",
            "OSArchitecture=amd64", "OSSkuId=48", "OSUILocale=en-US", $"OSVersion={query.Build}",
            "ProcessorIdentifier=Intel64 Family 6 Model 186 Stepping 3", "ProcessorManufacturer=GenuineIntel",
            "ProcessorModel=13th Gen Intel(R) Core(TM) i7-1355U", "ProductType=WinNT", "ReleaseType=Production",
            "SdbVer_20H1=2000000000", "SdbVer_19H1=2000000000", "SecureBootCapable=1", "TelemetryLevel=3",
        ]);
        attributes.AddRange(releases.Select(r => $"TimestampEpochString_{r}={stamp}"));
        attributes.AddRange(["TPMVersion=2", "UpdateManagementGroup=2", "UpdateOfferedDays=0"]);
        attributes.AddRange(green.Select(r => $"UpgEx_{r}=Green"));
        attributes.AddRange([
            "UpgradeAccepted=1", "UpgradeEligible=1", "UserInPlaceUpgrade=1", "VBSState=2", "Version_RS5=2000000000",
            $"WuClientVer={query.Build}",
        ]);
        return "E:" + string.Join("&", attributes);
    }

    // Обновления, которые «уже стоят» на устройстве (категории и инфраструктура WU), — без них служба
    // не доходит до выпусков ОС. Список общий для всех открытых клиентов UUP.
    static readonly long[] InstalledNonLeafUpdateIds =
    [
        1, 10, 105939029, 105995585, 106017178, 107825194, 10809856, 11, 117765322, 129905029, 130040030, 130040031,
        130040032, 130040033, 133399034, 138372035, 138372036, 139536037, 139536038, 139536039, 139536040, 142045136,
        158941041, 158941042, 158941043, 158941044, 159776047, 160733048, 160733049, 160733050, 160733051, 160733055,
        160733056, 161870057, 161870058, 161870059, 17, 19, 2, 23110993, 23110994, 23110995, 23110996, 23110999,
        23111000, 23111001, 23111002, 23111003, 23111004, 2359974, 2359977, 24513870, 28880263, 296374060, 3, 30077688,
        30486944, 5143990, 5169043, 5169044, 5169047, 59830006, 59830007, 59830008, 60484010, 62450018, 62450019,
        62450020, 69801474, 8788830, 8806526, 9125350, 9154769, 98959022, 98959023, 98959024, 98959025, 98959026,
    ];
}
