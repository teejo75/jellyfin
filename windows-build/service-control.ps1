[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('install','uninstall','start','stop','restart','status')]
    [string]$Action = 'status',

    [string]$DataDir = "$env:ProgramData\Jellyfin\Server",
    [ValidateSet('LocalSystem','NetworkService')][string]$Account = 'NetworkService',
    [ValidateSet('Automatic','Manual','Disabled')][string]$StartType = 'Automatic',
    [string]$ServiceName = 'JellyfinServer',
    [string]$DisplayName = 'Jellyfin Server',
    [string]$Description = 'Jellyfin Server: The Free Software Media System'
)

$ErrorActionPreference = 'Stop'

function Assert-Admin {
    $id = [System.Security.Principal.WindowsIdentity]::GetCurrent()
    $pr = New-Object System.Security.Principal.WindowsPrincipal($id)
    if (-not $pr.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
        throw "Run this script from an elevated PowerShell (Administrator)."
    }
}

function Get-JellyfinExe {
    $exe = Join-Path $PSScriptRoot 'jellyfin.exe'
    if (-not (Test-Path $exe)) {
        throw "jellyfin.exe not found beside this script ($exe). Place service-control.ps1 in the install folder."
    }
    return $exe
}

function Get-Service-Safe {
    Get-Service -Name $ServiceName -ErrorAction SilentlyContinue
}

function Action-Status {
    $svc = Get-Service-Safe
    if (-not $svc) {
        Write-Host "Service '$ServiceName' is not installed."
        return
    }

    $info = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
    [pscustomobject]@{
        Name        = $svc.Name
        DisplayName = $svc.DisplayName
        Status      = $svc.Status
        StartType   = $svc.StartType
        Account     = $info.StartName
        BinPath     = $info.PathName
    } | Format-List
}

function Action-Install {
    Assert-Admin
    $exe = Get-JellyfinExe

    if (Get-Service-Safe) {
        throw "Service '$ServiceName' already installed. Run 'uninstall' first."
    }

    if (-not (Test-Path $DataDir)) {
        Write-Host "Creating data dir: $DataDir"
        New-Item -ItemType Directory -Force -Path $DataDir | Out-Null
    }

    $binPath = '"{0}" --service --datadir "{1}"' -f $exe, $DataDir
    $accountId = if ($Account -eq 'LocalSystem') { 'LocalSystem' } else { 'NT AUTHORITY\NetworkService' }
    $startArg  = switch ($StartType) {
        'Automatic' { 'auto' }
        'Manual'    { 'demand' }
        'Disabled'  { 'disabled' }
    }

    Write-Host "Creating service '$ServiceName'"
    Write-Host "  binPath: $binPath"
    Write-Host "  account: $accountId"
    Write-Host "  start:   $startArg"

    & sc.exe create $ServiceName binPath= $binPath DisplayName= $DisplayName start= $startArg obj= $accountId | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe create failed (exit $LASTEXITCODE)" }

    & sc.exe description $ServiceName $Description | Out-Host

    if ($Account -eq 'NetworkService') {
        Write-Host "Granting NetworkService modify rights on $DataDir"
        $acl = Get-Acl $DataDir
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            'NT AUTHORITY\NetworkService',
            'Modify','ContainerInherit,ObjectInherit','None','Allow')
        $acl.SetAccessRule($rule)
        Set-Acl $DataDir $acl
    }

    Write-Host "Service installed. Starting..."
    Start-Service -Name $ServiceName
    Action-Status
}

function Action-Uninstall {
    Assert-Admin
    $svc = Get-Service-Safe
    if (-not $svc) {
        Write-Host "Service '$ServiceName' is not installed."
        return
    }

    if ($svc.Status -ne 'Stopped') {
        Write-Host "Stopping service..."
        Stop-Service -Name $ServiceName -Force -ErrorAction SilentlyContinue
        $svc.WaitForStatus('Stopped', '00:00:30')
    }

    Write-Host "Deleting service..."
    & sc.exe delete $ServiceName | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "sc.exe delete failed (exit $LASTEXITCODE)" }
    Write-Host "Removed."
}

function Action-Start   { Assert-Admin; Start-Service   -Name $ServiceName; Action-Status }
function Action-Stop    { Assert-Admin; Stop-Service    -Name $ServiceName -Force; Action-Status }
function Action-Restart { Assert-Admin; Restart-Service -Name $ServiceName -Force; Action-Status }

switch ($Action) {
    'status'    { Action-Status }
    'install'   { Action-Install }
    'uninstall' { Action-Uninstall }
    'start'     { Action-Start }
    'stop'      { Action-Stop }
    'restart'   { Action-Restart }
}
