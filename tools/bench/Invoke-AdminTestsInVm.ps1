<#
.SYNOPSIS
    Admin-тесты (DISM) внутри ВМ стенда — для хостов, где офлайн-обслуживание DISM не работает.

.DESCRIPTION
    Берёт ВМ, оставленную стендом (Invoke-Bench.ps1 -KeepVm), подключает к ней диск для данных, копирует туда
    самодостаточную публикацию тестов движка и ISO через PowerShell Direct и запускает тесты категории Admin
    под пользователем сценария. Результат — в <папка ВМ>\admin-tests.log, код выхода — как у тестов.

    Нужен, когда на хосте DISM не может открыть смонтированный образ (например, когда reg load не загружает
    кусты реестра). В ВМ — релизная Windows из того же ISO.

.EXAMPLE
    .\tools\bench\Invoke-AdminTestsInVm.ps1 -VmName shapeshift-bench-20261020-140000
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $VmName,
    [string] $Iso = $env:SHAPESHIFT_TEST_ISO,
    [string] $Scenario = 'full-unattended',
    # Фильтр тестов. Опубликованный exe запускает собственный раннер xunit, а не Microsoft.Testing.Platform.
    [string[]] $Filter = @('-trait', 'Category=Admin'),
    [int] $DataDiskGB = 120
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repo = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
if (-not $Iso) { throw 'Pass -Iso or set SHAPESHIFT_TEST_ISO.' }
$Iso = (Resolve-Path $Iso).Path
$vm = Get-VM -Name $VmName
$work = $vm.Path
$log = Join-Path $work 'admin-tests.log'

function Write-Step([string] $Message) {
    $line = '[{0:HH:mm:ss}] {1}' -f (Get-Date), $Message
    Write-Host $line
    Add-Content -Path $log -Value $line -Encoding UTF8
}

$profileText = (Get-Content (Join-Path $PSScriptRoot "scenarios\$Scenario\profile.json") -Raw -Encoding UTF8) -replace '(?m)^\s*//.*$', ''
$user = ($profileText | ConvertFrom-Json).setup.user
$credential = New-Object System.Management.Automation.PSCredential (
    $user.name, (ConvertTo-SecureString $user.password.value -AsPlainText -Force))

# 1. Самодостаточная публикация тестов: в ВМ нет ни .NET, ни сети.
Write-Step 'Publishing the engine tests (self-contained)'
$published = Join-Path $work 'tests'
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $env:ProgramFiles 'dotnet\dotnet.exe' }
& $dotnet publish (Join-Path $repo 'tests\Shapeshift.Engine.Tests') -c Release -r win-x64 --self-contained -o $published -v q -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE." }

# 2. Диск для данных: распаковка образа и сборка не помещаются на системный диск ВМ.
$dataDisk = Join-Path $work 'data.vhdx'
if (-not (Test-Path $dataDisk)) {
    Write-Step "Adding a $DataDiskGB GB data disk"
    New-VHD -Path $dataDisk -SizeBytes ($DataDiskGB * 1GB) -Dynamic | Out-Null
    Add-VMHardDiskDrive -VM $vm -Path $dataDisk
}
if ($vm.State -ne 'Running') { Start-VM -VM $vm }

$session = $null
$deadline = (Get-Date).AddMinutes(5)
while (-not $session) {
    try { $session = New-PSSession -VMName $VmName -Credential $credential -ErrorAction Stop }
    catch { if ((Get-Date) -gt $deadline) { throw }; Start-Sleep -Seconds 5 }
}

try {
    $drive = Invoke-Command -Session $session -ScriptBlock {
        $disk = Get-Disk | Where-Object PartitionStyle -eq 'RAW' | Select-Object -First 1
        if ($disk) {
            Initialize-Disk -Number $disk.Number -PartitionStyle GPT
            New-Partition -DiskNumber $disk.Number -UseMaximumSize -AssignDriveLetter |
                Format-Volume -FileSystem NTFS -NewFileSystemLabel 'Data' -Confirm:$false | Out-Null
        }
        (Get-Volume -FileSystemLabel 'Data').DriveLetter
    }
    $root = "${drive}:\"
    $elevated = Invoke-Command -Session $session -ScriptBlock {
        ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
            [Security.Principal.WindowsBuiltInRole]::Administrator)
    }
    Write-Step "VM data drive $root; elevated session: $elevated"

    Write-Step 'Copying the tests and the ISO into the VM'
    Invoke-Command -Session $session -ArgumentList $root -ScriptBlock {
        param($root)
        Remove-Item (Join-Path $root 'tests') -Recurse -Force -ErrorAction SilentlyContinue
        New-Item -ItemType Directory -Force (Join-Path $root 'tmp') | Out-Null
    }
    Copy-Item -ToSession $session -Path $published -Destination (Join-Path $root 'tests') -Recurse
    $vmIso = Join-Path $root (Split-Path $Iso -Leaf)
    $present = Invoke-Command -Session $session -ArgumentList $vmIso, (Get-Item $Iso).Length -ScriptBlock {
        param($path, $length)
        (Test-Path $path) -and (Get-Item $path).Length -eq $length
    }
    if (-not $present) {
        # Copy-Item -ToSession в Windows PowerShell не справляется с файлами в гигабайты — через службу гостевых служб.
        Invoke-Command -Session $session -ArgumentList $vmIso -ScriptBlock { param($path) Remove-Item $path -Force -ErrorAction SilentlyContinue }
        Enable-VMIntegrationService -VM $vm -Name (Get-VMIntegrationService -VM $vm | Where-Object Id -match '6C09BB55-D683-4DA0-8931-C9BF705F6480$').Name
        for ($attempt = 1; ; $attempt++) {
            # Служба в госте запускается не сразу после включения.
            try { Copy-VMFile -VM $vm -SourcePath $Iso -DestinationPath $vmIso -FileSource Host -CreateFullPath -Force; break }
            catch { if ($attempt -ge 12) { throw }; Start-Sleep -Seconds 10 }
        }
    }

    Write-Step "Running the tests: $($Filter -join ' ')"
    $result = Invoke-Command -Session $session -ArgumentList $root, $vmIso, $Filter -ScriptBlock {
        param($root, $iso, $filter)
        $env:SHAPESHIFT_TEST_ISO = $iso
        # Тесты кладут рабочие папки в корень диска временной папки — на диск данных.
        $env:TEMP = $env:TMP = Join-Path $root 'tmp'
        $output = & (Join-Path $root 'tests\Shapeshift.Engine.Tests.exe') @filter 2>&1 | ForEach-Object { "$_" }
        [pscustomobject]@{ Output = $output; ExitCode = $LASTEXITCODE }
    }
    $result.Output | Add-Content -Path $log -Encoding UTF8
    $result.Output | Write-Host
    Write-Step "Tests exit code: $($result.ExitCode)"
    exit $result.ExitCode
}
finally {
    Remove-PSSession $session
}
