# Lists every temperature source TempTray could use on this machine, read-only.
# Run: right-click > "Run with PowerShell", or: powershell -ExecutionPolicy Bypass -File diagnose.ps1
# It asks for admin rights, then writes diagnose-<computer>.txt next to itself.
$ErrorActionPreference = 'Continue'

$admin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $admin) {
    Start-Process powershell -Verb RunAs -Wait -ArgumentList '-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`""
    return
}

$out = Join-Path $PSScriptRoot "diagnose-$env:COMPUTERNAME.txt"

& {
    '--- Machine'
    $cs = Get-CimInstance Win32_ComputerSystem
    $bios = Get-CimInstance Win32_BIOS
    '{0} {1}, BIOS {2}, {3}' -f $cs.Manufacturer, $cs.Model, $bios.SMBIOSBIOSVersion, (Get-CimInstance Win32_Processor | Select-Object -First 1).Name

    '--- Dell data vault interface (what TempTray uses)'
    $ddvClass = Get-CimClass -Namespace root/wmi -ClassName DDVWmiMethodFunction -ErrorAction SilentlyContinue
    if (-not $ddvClass) {
        'not present'
    } else {
        'methods: ' + ($ddvClass.CimClassMethods.Name -join ', ')
        try {
            $ddv = Get-CimInstance -Namespace root/wmi DDVWmiMethodFunction -ErrorAction Stop | Select-Object -First 1
            'version: ' + (Invoke-CimMethod -InputObject $ddv -MethodName ReturnVersion -Arguments @{ arg2 = [uint32]0 }).argr
            $t = Invoke-CimMethod -InputObject $ddv -MethodName ThermalSensorInformation -Arguments @{ arg2 = [uint32]0 }
            'thermal raw: ' + (($t.RawData | Select-Object -First $t.RawSize | ForEach-Object { '{0:X2}' -f $_ }) -join ' ')
        } catch { 'error: ' + $_.Exception.Message }
    }

    '--- ACPI thermal zones'
    try {
        Get-CimInstance -Namespace root/wmi MSAcpi_ThermalZoneTemperature -ErrorAction Stop |
            ForEach-Object { '{0}: {1:N1} C' -f $_.InstanceName, ($_.CurrentTemperature / 10 - 273.15) }
    } catch { 'error: ' + $_.Exception.Message }

    '--- Thermal zone performance counters'
    $zones = Get-CimInstance Win32_PerfFormattedData_Counters_ThermalZoneInformation -ErrorAction SilentlyContinue
    if (-not $zones) { 'none' }
    $zones | ForEach-Object { '{0}: {1:N1} C' -f $_.Name, ($_.HighPrecisionTemperature / 10 - 273.15) }

    '--- Dell, thermal and fan WMI classes in root\wmi'
    Get-CimClass -Namespace root/wmi | Where-Object CimClassName -match 'Dell|DDV|Therm|Temp|Fan|Sensor' | ForEach-Object CimClassName

    '--- Other WMI namespaces under root'
    (Get-CimInstance -Namespace root -ClassName __Namespace | ForEach-Object Name) -join ', '

    # Dell tools (Dell Command | Monitor, SupportAssist, Dell Power Manager) register their own namespaces; one may carry sensors.
    '--- Classes in the Dell namespaces'
    $pending = [Collections.Generic.Queue[string]]::new()
    'root/Dell', 'root/dcim' | ForEach-Object { $pending.Enqueue($_) }
    while ($pending.Count -gt 0) {
        $ns = $pending.Dequeue()
        Get-CimInstance -Namespace $ns -ClassName __Namespace -ErrorAction SilentlyContinue | ForEach-Object { $pending.Enqueue("$ns/$($_.Name)") }
        $classes = Get-CimClass -Namespace $ns -ErrorAction SilentlyContinue | Where-Object CimClassName -notmatch '^(__|CIM_|MSFT_)'
        if (-not $classes) { continue }
        "$ns"
        foreach ($c in $classes) {
            $line = '  ' + $c.CimClassName
            # Count instances only of sensor-like classes: enumerating everything can take minutes.
            if ($c.CimClassName -match 'Sensor|Therm|Temp|Fan') {
                $n = @(Get-CimInstance -Namespace $ns -ClassName $c.CimClassName -ErrorAction SilentlyContinue).Count
                $line += " ($n instances)"
            }
            $line
        }
    }

    '--- NVIDIA GPU (nvidia-smi, part of the NVIDIA driver)'
    $smi = Get-Command nvidia-smi -ErrorAction SilentlyContinue
    if (-not $smi) { $smi = Get-Item "$env:WINDIR\System32\nvidia-smi.exe" -ErrorAction SilentlyContinue }
    if ($smi) { & $smi.Source --query-gpu=name,temperature.gpu,fan.speed --format=csv 2>&1 } else { 'not found' }
} *>&1 | Out-File -FilePath $out -Encoding utf8

"Saved to $out"
Read-Host 'Press Enter to close'
