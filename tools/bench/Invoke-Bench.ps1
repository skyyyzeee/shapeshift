<#
.SYNOPSIS
    Стенд сквозной проверки: профиль сценария -> build -> ВМ Hyper-V -> автоматическая установка -> проверки -> удаление ВМ.

.DESCRIPTION
    Сценарий — папка в scenarios\: profile.json (без source/output — их подставляет стенд) и expect.json
    (что проверить в установленной системе). ВМ: поколение 2, Secure Boot, без vTPM (проверка обхода
    ограничений) и без сети (проверка установки без аккаунта Microsoft). Результат проверяется через
    PowerShell Direct под пользователем из профиля, поэтому в сценарии пароль хранится (mode: store).

    Требования: Hyper-V и членство в группе «Администраторы Hyper-V» (или запуск от администратора).

.EXAMPLE
    .\tools\bench\Invoke-Bench.ps1 -Iso D:\win11.iso
.EXAMPLE
    .\tools\bench\Invoke-Bench.ps1 -Scenario full-unattended -KeepVm
#>
[CmdletBinding()]
param(
    [string] $Scenario = 'full-unattended',
    [string] $Iso = $env:SHAPESHIFT_TEST_ISO,
    [int] $TimeoutMinutes = 60,
    # Не удалять ВМ, диск и образ после прогона (для ручного разбора).
    [switch] $KeepVm
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$scenarioDir = Join-Path $PSScriptRoot "scenarios\$Scenario"
if (-not (Test-Path $scenarioDir)) { throw "Scenario not found: $scenarioDir" }
if (-not $Iso) { throw 'Pass -Iso or set SHAPESHIFT_TEST_ISO.' }
$Iso = (Resolve-Path $Iso).Path

$runId = 'shapeshift-bench-' + (Get-Date -Format 'yyyyMMdd-HHmmss')
$work = Join-Path $env:LOCALAPPDATA "Shapeshift\Bench\$runId"
New-Item -ItemType Directory -Force -Path $work | Out-Null
$log = Join-Path $work 'bench.log'

function Write-Step([string] $Message) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding UTF8
}

# Профили допускают комментарии //, а ConvertFrom-Json в Windows PowerShell — нет.
function Read-JsonWithComments([string] $Path) {
    ((Get-Content -Path $Path -Raw -Encoding UTF8) -replace '(?m)^\s*//.*$', '') | ConvertFrom-Json
}

# Снимок экрана ВМ (RGB565 от Hyper-V) в PNG — главное средство разбора зависшей установки.
function Save-VmScreenshot([string] $VmName, [string] $Path) {
    try {
        $ns = 'root\virtualization\v2'
        $vm = Get-CimInstance -Namespace $ns -ClassName Msvm_ComputerSystem -Filter "ElementName='$VmName'"
        $settings = Get-CimAssociatedInstance -InputObject $vm -ResultClassName Msvm_VirtualSystemSettingData |
            Where-Object VirtualSystemType -eq 'Microsoft:Hyper-V:System:Realized'
        $service = Get-CimInstance -Namespace $ns -ClassName Msvm_VirtualSystemManagementService
        $width = 1024; $height = 768
        $result = Invoke-CimMethod -InputObject $service -MethodName GetVirtualSystemThumbnailImage -Arguments @{
            TargetSystem = $settings; WidthPixels = [uint16]$width; HeightPixels = [uint16]$height
        }
        Add-Type -AssemblyName System.Drawing
        $bitmap = New-Object System.Drawing.Bitmap $width, $height, ([System.Drawing.Imaging.PixelFormat]::Format16bppRgb565)
        $rect = New-Object System.Drawing.Rectangle 0, 0, $width, $height
        $data = $bitmap.LockBits($rect, [System.Drawing.Imaging.ImageLockMode]::WriteOnly, $bitmap.PixelFormat)
        $bytes = [byte[]]$result.ImageData
        [System.Runtime.InteropServices.Marshal]::Copy($bytes, 0, $data.Scan0, [Math]::Min($bytes.Length, $data.Stride * $height))
        $bitmap.UnlockBits($data)
        $bitmap.Save($Path, [System.Drawing.Imaging.ImageFormat]::Png)
        $bitmap.Dispose()
    }
    catch {
        Write-Warning "Screenshot failed: $_"
    }
}

# Нажимает Enter в ВМ: загрузчик UEFI с образа ждёт «Press any key to boot from CD or DVD».
function Send-VmEnter([string] $VmId) {
    $keyboard = Get-CimInstance -Namespace root\virtualization\v2 -ClassName Msvm_Keyboard -Filter "SystemName='$VmId'" -ErrorAction SilentlyContinue
    if ($keyboard) {
        Invoke-CimMethod -InputObject $keyboard -MethodName TypeKey -Arguments @{ keyCode = [uint32]13 } | Out-Null
    }
}

$vmName = $runId
$passed = $false
try {
    # 1. Профиль сборки: сценарий + исходный и результирующий образы.
    $scenarioProfile = Read-JsonWithComments (Join-Path $scenarioDir 'profile.json')
    $expect = Read-JsonWithComments (Join-Path $scenarioDir 'expect.json')
    $user = $scenarioProfile.setup.user
    if (-not $user -or $user.password.mode -ne 'store') {
        throw 'Scenario profile must define setup.user with password mode "store" (the bench signs in with it).'
    }

    $outputIso = Join-Path $work 'bench.iso'
    $scenarioProfile | Add-Member -NotePropertyName source -NotePropertyValue @{ iso = $Iso } -Force
    $scenarioProfile | Add-Member -NotePropertyName output -NotePropertyValue @{ iso = $outputIso } -Force
    $buildProfile = Join-Path $work 'profile.json'
    $scenarioProfile | ConvertTo-Json -Depth 10 | Set-Content -Path $buildProfile -Encoding UTF8

    # 2. Сборка образа движком. Предупреждения движок пишет в stderr — это не ошибка.
    Write-Step "Building image from $Iso"
    $dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
    if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    & $dotnet run --project (Join-Path $repo 'src\Shapeshift.Engine') -c Release -- `
        build --profile $buildProfile --workspace (Join-Path $work 'ws') 2>&1 |
        ForEach-Object { "$_" } | Tee-Object -FilePath $log -Append | Write-Host
    $buildExit = $LASTEXITCODE
    $ErrorActionPreference = $previous
    if ($buildExit -ne 0) { throw "Engine build failed with exit code $buildExit." }

    # 3. ВМ: поколение 2, Secure Boot, без vTPM и без сети. Загрузка: сначала диск, потом DVD —
    #    после первой перезагрузки установка продолжится с диска, а не начнётся заново.
    Write-Step "Creating VM $vmName"
    $vm = New-VM -Name $vmName -Generation 2 -MemoryStartupBytes 4GB -Path $work `
        -NewVHDPath (Join-Path $work 'disk.vhdx') -NewVHDSizeBytes 64GB
    Set-VM -VM $vm -ProcessorCount 2 -StaticMemory -AutomaticCheckpointsEnabled $false -CheckpointType Disabled
    Set-VMFirmware -VM $vm -EnableSecureBoot On -SecureBootTemplate MicrosoftWindows
    $dvd = Add-VMDvdDrive -VM $vm -Path $outputIso -Passthru
    Set-VMFirmware -VM $vm -BootOrder (Get-VMHardDiskDrive -VM $vm), $dvd

    Write-Step 'Starting VM'
    Start-VM -VM $vm
    $vmId = $vm.Id.ToString().ToUpperInvariant()
    $pressUntil = (Get-Date).AddSeconds(60)
    while ((Get-Date) -lt $pressUntil) {
        Send-VmEnter $vmId
        Start-Sleep -Milliseconds 700
    }

    # 4. Ждём, пока установка закончится и пользователь войдёт (автовход): PowerShell Direct отвечает
    #    и в сеансе пользователя запущен Проводник.
    Write-Step "Waiting for the installation (timeout $TimeoutMinutes min)"
    $credential = New-Object System.Management.Automation.PSCredential (
        $user.name, (ConvertTo-SecureString $user.password.value -AsPlainText -Force))
    $deadline = (Get-Date).AddMinutes($TimeoutMinutes)
    $ready = $false
    $nextScreenshot = Get-Date
    while ((Get-Date) -lt $deadline) {
        if ((Get-Date) -ge $nextScreenshot) {
            Save-VmScreenshot $vmName (Join-Path $work 'progress.png')
            $nextScreenshot = (Get-Date).AddMinutes(2)
        }
        try {
            # Проводник в интерактивном сеансе (не 0) бывает только у вошедшего пользователя.
            $desktop = Invoke-Command -VMName $vmName -Credential $credential -ErrorAction Stop -ScriptBlock {
                [bool](Get-Process explorer -ErrorAction SilentlyContinue | Where-Object SessionId -gt 0)
            }
            if ($desktop) { $ready = $true; break }
        }
        catch {
            # Система ещё ставится или пользователя ещё нет.
        }
        Start-Sleep -Seconds 20
    }
    if (-not $ready) {
        Save-VmScreenshot $vmName (Join-Path $work 'timeout.png')
        throw "Installation did not finish in $TimeoutMinutes minutes; see timeout.png in $work."
    }
    Write-Step 'Installed; checking the result'

    # 5. Факты об установленной системе. Культуру читаем из реестра пользователя: Get-Culture
    #    в удалённом сеансе возвращает культуру хоста.
    $facts = Invoke-Command -VMName $vmName -Credential $credential -ErrorAction Stop -ScriptBlock {
        $version = Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion'
        $preload = Get-ItemProperty 'HKCU:\Keyboard Layout\Preload' -ErrorAction SilentlyContinue
        $layouts = @()
        if ($preload) {
            $layouts = @($preload.PSObject.Properties | Where-Object { $_.Name -match '^\d+$' } |
                Sort-Object { [int]$_.Name } | ForEach-Object { [string]$_.Value })
        }
        [pscustomobject]@{
            ComputerName = $env:COMPUTERNAME
            EditionId = $version.EditionID
            Build = "$($version.CurrentBuild).$($version.UBR)"
            UserLocale = (Get-ItemProperty 'HKCU:\Control Panel\International').LocaleName
            KeyboardLayouts = $layouts
            TimeZone = (Get-TimeZone).Id
            Administrators = @(Get-LocalGroupMember -SID S-1-5-32-544 | ForEach-Object { ($_.Name -split '\\')[-1] })
            Tpm = [bool](Get-CimInstance -Namespace root\cimv2\Security\MicrosoftTpm -ClassName Win32_Tpm -ErrorAction SilentlyContinue)
        }
    }
    Write-Step ("Installed system: build {0}, edition {1}, TPM present: {2}" -f $facts.Build, $facts.EditionId, $facts.Tpm)

    $checks = New-Object System.Collections.Generic.List[object]
    function Add-Check([string] $Name, $Expected, $Actual) {
        $ok = (@($Expected) -join '|') -eq (@($Actual) -join '|')
        $checks.Add([pscustomobject]@{ Check = $Name; Expected = (@($Expected) -join ', '); Actual = (@($Actual) -join ', '); Ok = $ok })
    }

    # Ставилось без vTPM — значит, обход ограничений сработал; проверяем, что TPM действительно не было.
    Add-Check 'no TPM (bypass worked)' $false $facts.Tpm
    foreach ($property in $expect.PSObject.Properties) {
        switch ($property.Name) {
            'computerName' { Add-Check 'computer name' $property.Value $facts.ComputerName }
            'editionId' { Add-Check 'edition' $property.Value $facts.EditionId }
            'userLocale' { Add-Check 'user locale' $property.Value $facts.UserLocale }
            'keyboardLayouts' { Add-Check 'keyboard layouts' $property.Value $facts.KeyboardLayouts }
            'timeZone' { Add-Check 'time zone' $property.Value $facts.TimeZone }
            'userIsAdministrator' { Add-Check 'user is administrator' $true ($facts.Administrators -contains $property.Value) }
            default { throw "Unknown expectation '$($property.Name)' in expect.json." }
        }
    }

    $checks | Format-Table -AutoSize | Out-String | ForEach-Object { Write-Host $_; Add-Content -Path $log -Value $_ -Encoding UTF8 }
    $passed = -not ($checks | Where-Object { -not $_.Ok })
    Write-Step ($(if ($passed) { 'PASSED' } else { 'FAILED' }))
}
catch {
    Write-Step "ERROR: $_"
    throw
}
finally {
    if (-not $KeepVm -and (Get-VM -Name $vmName -ErrorAction SilentlyContinue)) {
        Stop-VM -Name $vmName -TurnOff -Force -ErrorAction SilentlyContinue
        Remove-VM -Name $vmName -Force
    }
    if (-not $KeepVm) {
        # Образ и диск весят гигабайты; лог и снимки экрана оставляем при неудаче.
        Remove-Item -Path (Join-Path $work 'bench.iso'), (Join-Path $work 'disk.vhdx'), (Join-Path $work 'ws'), (Join-Path $work $vmName) `
            -Recurse -Force -ErrorAction SilentlyContinue
        if ($passed) { Remove-Item -Path $work -Recurse -Force -ErrorAction SilentlyContinue }
    }
    if (-not $passed) { Write-Host "Log and screenshots: $work" }
}

if (-not $passed) { exit 1 }
