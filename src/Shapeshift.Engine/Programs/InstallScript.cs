using System.Text;
using Shapeshift.Core;

namespace Shapeshift.Engine.Programs;

/// <summary>
/// Сценарий PowerShell, который при первом входе ставит программы по порядку. Чистая функция профиля:
/// одинаковые программы — одинаковый текст. Сбой одной программы не останавливает остальные;
/// всё пишется в <c>programs.log</c> рядом со сценарием.
/// </summary>
public static class InstallScript
{
    /// <summary>Команда первого входа, которая запускает сценарий (файл ответов).</summary>
    public const string Command =
        $@"cmd.exe /c powershell.exe -NoProfile -ExecutionPolicy Bypass -File ""{ProgramPayload.TargetFolder}\{ProgramPayload.ScriptName}""";

    /// <summary>Сколько ждать одну программу, прежде чем прервать её установку.</summary>
    public const int TimeoutMinutes = 30;

    // Windows PowerShell 5.1 читает сценарий без BOM в кодировке ANSI — названия по-русски испортились бы.
    public static readonly Encoding FileEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    public static string Generate(ProgramPayload payload)
    {
        var table = new StringBuilder();
        foreach (var item in payload.Items)
        {
            table.Append("    @{ Name = ").Append(Quote(item.Program.Name));
            switch (item.Program)
            {
                case WingetProgram winget:
                    table.Append("; Winget = ").Append(Quote(winget.Id));
                    break;
                case LocalProgram local:
                    table.Append("; Folder = ").Append(Quote(item.Folder!))
                        .Append("; Run = ").Append(Quote(item.Launch!))
                        .Append("; Args = ").Append(Quote(local.Args ?? ""));
                    break;
            }
            table.Append(" }\r\n");
        }

        return Template
            .Replace("@@PROGRAMS@@", table.ToString().TrimEnd())
            .Replace("@@TIMEOUT@@", TimeoutMinutes.ToString())
            .ReplaceLineEndings("\r\n");
    }

    // Строка PowerShell в одинарных кавычках: внутри особые только сами кавычки — и типографские тоже,
    // PowerShell считает их такими же. Каждую удваиваем.
    static string Quote(string text)
    {
        var quoted = new StringBuilder("'");
        foreach (var c in text)
        {
            quoted.Append(c);
            if (c is '\'' or '‘' or '’' or '‚' or '‛')
                quoted.Append(c);
        }
        return quoted.Append('\'').ToString();
    }

    const string Template = """
        # Shapeshift: установка программ при первом входе. Сценарий создан движком Shapeshift.
        # Журнал — programs.log рядом. Сбой одной программы не останавливает остальные.
        $ErrorActionPreference = 'Continue'
        $root = $PSScriptRoot
        $log = Join-Path $root 'programs.log'
        $timeoutMinutes = @@TIMEOUT@@

        $programs = @(
        @@PROGRAMS@@
        )

        function Write-Log([string] $Text) {
            $line = '{0:yyyy-MM-dd HH:mm:ss} {1}' -f (Get-Date), $Text
            Add-Content -Path $log -Value $line -Encoding UTF8
            Write-Host $line
        }

        # Ждёт процесс не дольше таймаута; код выхода или $null, если пришлось прервать.
        function Wait-Installer($Process) {
            $null = $Process.Handle  # без этого Start-Process -PassThru не отдаёт код выхода
            if ($Process.WaitForExit($timeoutMinutes * 60 * 1000)) { return $Process.ExitCode }
            try { $Process.Kill() } catch { }
            return $null
        }

        function Install-Local($Program) {
            $folder = Join-Path $root "Programs\$($Program.Folder)"
            $path = Join-Path $folder $Program.Run
            $extra = $Program.Args
            switch ([IO.Path]::GetExtension($path).ToLowerInvariant()) {
                '.msi' { $file = 'msiexec.exe'; $arguments = "/i `"$path`" /qn /norestart $extra" }
                { $_ -in '.cmd', '.bat' } { $file = 'cmd.exe'; $arguments = "/c `"`"$path`" $extra`"" }
                '.ps1' { $file = 'powershell.exe'; $arguments = "-NoProfile -ExecutionPolicy Bypass -File `"$path`" $extra" }
                default { $file = $path; $arguments = $extra }
            }
            $start = @{ FilePath = $file; WorkingDirectory = (Split-Path $path -Parent); PassThru = $true }
            if ($arguments.Trim()) { $start.ArgumentList = $arguments.Trim() }
            $code = Wait-Installer (Start-Process @start)
            if ($null -eq $code) { return "stopped after $timeoutMinutes min" }
            # 3010 и 1641 — установлено, нужна перезагрузка.
            if ($code -in 0, 3010, 1641) { return $null }
            return "exit code $code"
        }

        $script:winget = $null
        $script:online = $null

        # winget регистрируется у нового пользователя не сразу после входа — ждём и подталкиваем.
        function Find-Winget {
            if ($script:winget) { return $script:winget }
            $candidate = Join-Path $env:LOCALAPPDATA 'Microsoft\WindowsApps\winget.exe'
            for ($i = 0; $i -lt 24; $i++) {
                if (Test-Path $candidate) { $script:winget = $candidate; return $candidate }
                if ($i -eq 0) {
                    try { Add-AppxPackage -RegisterByFamilyName -MainPackage 'Microsoft.DesktopAppInstaller_8wekyb3d8bbwe' -ErrorAction Stop } catch { }
                }
                Start-Sleep -Seconds 5
            }
            return $null
        }

        # Сеть могла ещё не подняться — ждём до минуты.
        function Test-Online {
            if ($null -ne $script:online) { return $script:online }
            for ($i = 0; $i -lt 12; $i++) {
                try { $null = [Net.Dns]::GetHostAddresses('cdn.winget.microsoft.com'); $script:online = $true; return $true } catch { }
                Start-Sleep -Seconds 5
            }
            $script:online = $false
            return $false
        }

        function Install-Winget($Program) {
            if (-not (Test-Online)) { return 'skipped: no internet connection' }
            $winget = Find-Winget
            if (-not $winget) { return 'skipped: winget (App Installer) is not available' }
            & $winget install --id $Program.Winget --exact --source winget --silent --disable-interactivity `
                --accept-package-agreements --accept-source-agreements | Out-Null
            # 0x8A15002B: уже установлено и обновлять нечего.
            if ($LASTEXITCODE -in 0, -1978335189) { return $null }
            return ('exit code 0x{0:X8}' -f $LASTEXITCODE)
        }

        Write-Log "Installing $($programs.Count) program(s)"
        foreach ($program in $programs) {
            Write-Log "Installing $($program.Name)"
            try {
                $problem = if ($program.Winget) { Install-Winget $program } else { Install-Local $program }
            }
            catch {
                $problem = "$_"
            }
            if (-not $problem) { Write-Log "OK $($program.Name)" }
            elseif ($problem -like 'skipped: *') { Write-Log "SKIPPED $($program.Name): $($problem.Substring(9))" }
            else { Write-Log "FAILED $($program.Name): $problem" }
        }

        # Установщики больше не нужны, а места занимают много.
        Remove-Item (Join-Path $root 'Programs') -Recurse -Force -ErrorAction SilentlyContinue
        Write-Log 'Done'
        """;
}
