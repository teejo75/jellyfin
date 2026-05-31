[CmdletBinding()]
param(
    [Parameter(Position = 0)]
    [ValidateSet('install','uninstall','start','stop','restart','status')]
    [string]$Action = 'status',

    [string]$DataDir = "$Env:JELLYFIN_DATA_DIR",
    [ValidateSet('LocalSystem','NetworkService')][string]$Account = 'NetworkService',
    [ValidateSet('Automatic','Manual','Disabled')][string]$StartType = 'Automatic',
    [string]$ServiceName = 'JellyfinServer',
    [string]$DisplayName = 'Jellyfin Server',
    [string]$Description = 'Jellyfin Server: The Free Software Media System'
)

$ErrorActionPreference = 'Stop'

if (-not $DataDir) {
    $DataDir = "$Env:ProgramData\Jellyfin\Server"
}

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

    Write-Host "Creating service '$ServiceName'"
    Write-Host "  binPath:   $binPath"
    Write-Host "  account:   $Account"
    Write-Host "  startType: $StartType"

    # Create the service as LocalSystem (New-Service default). On PS 5.1,
    # -Credential prompts for input even when passed a built-in account, so we
    # always create as LocalSystem and switch to NetworkService afterwards via
    # the Win32_Service.Change WMI method.
    New-Service `
        -Name           $ServiceName `
        -BinaryPathName $binPath `
        -DisplayName    $DisplayName `
        -Description    $Description `
        -StartupType    $StartType | Out-Null

    if ($Account -eq 'NetworkService') {
        Write-Host "Switching service account to NT AUTHORITY\NetworkService"
        $cimSvc = Get-CimInstance -ClassName Win32_Service -Filter "Name='$ServiceName'"
        $change = Invoke-CimMethod -InputObject $cimSvc -MethodName Change -Arguments @{
            StartName     = 'NT AUTHORITY\NetworkService'
            StartPassword = ''
        }
        if ($change.ReturnValue -ne 0) {
            throw "Win32_Service.Change returned $($change.ReturnValue); could not set account to NetworkService."
        }

        Write-Host "Granting NetworkService modify rights on $DataDir"
        $acl = Get-Acl $DataDir
        $rule = New-Object System.Security.AccessControl.FileSystemAccessRule(
            'NT AUTHORITY\NetworkService',
            'Modify','ContainerInherit,ObjectInherit','None','Allow')
        $acl.SetAccessRule($rule)
        Set-Acl $DataDir $acl
    }

    # Configure failure recovery so a non-zero exit (e.g. the dashboard
    # 'Restart' button, which makes the process exit 1) auto-restarts the
    # service after 5 seconds. Reset the failure counter after 1 day so
    # legitimate problems still surface as Stopped.
    Write-Host "Configuring failure recovery (auto-restart on non-zero exit)"
    & sc.exe failure $ServiceName reset= 86400 actions= restart/5000/restart/5000/restart/5000 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "sc.exe failure returned exit code $LASTEXITCODE; dashboard Restart will not auto-resume."
    }

    # By default the SCM only runs failure actions when the service process dies
    # WITHOUT reporting SERVICE_STOPPED. The dashboard Restart makes Jellyfin
    # report SERVICE_STOPPED with a non-zero exit code, which the SCM treats as a
    # clean stop unless this flag is set. Setting the failure-actions flag tells
    # the SCM to also run the recovery actions on a stop-with-error-code.
    Write-Host "Enabling failure actions on non-zero exit (failureflag)"
    & sc.exe failureflag $ServiceName 1 | Out-Host
    if ($LASTEXITCODE -ne 0) {
        Write-Warning "sc.exe failureflag returned exit code $LASTEXITCODE; dashboard Restart will not auto-resume."
    }

    Write-Host "Service installed. Run '.\service-control.ps1 start' to start it."
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
    # PS 5.1 has no Remove-Service. CimInstance does not expose a dynamic
    # Delete() method on this PS version, so shell out to sc.exe.
    & sc.exe delete $ServiceName | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "sc.exe delete returned exit code $LASTEXITCODE."
    }

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
