param([switch]$Json)

function Get-VeilPhase34Guest {
    $ErrorActionPreference = 'Stop'
    $system = Get-CimInstance Win32_ComputerSystem -ErrorAction Stop
    $product = Get-CimInstance Win32_ComputerSystemProduct -ErrorAction Stop
    $os = Get-CimInstance Win32_OperatingSystem -ErrorAction Stop
    $identity = [Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()
    $gateways = @(Get-NetRoute -ErrorAction Stop | Where-Object {
        $_.DestinationPrefix -in @('0.0.0.0/0', '::/0')
    })
    @{
        virtualBox = $system.Model -eq 'VirtualBox'
        uuid = [string]$product.UUID
        boot = $os.LastBootUpTime.ToUniversalTime().ToString('o')
        systemDrive = [string]$os.SystemDrive
        windowsBuild = [string]$os.BuildNumber
        elevated = $identity.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
        noDefaultRoute = $gateways.Count -eq 0
    }
}

if ($Json) { Get-VeilPhase34Guest | ConvertTo-Json -Compress }
