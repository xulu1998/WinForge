<#
.SYNOPSIS
    WinForge in-VM full-health validator (Phase 16 Stage 16.1, ADR-098).

.DESCRIPTION
    Copy this script (and, optionally, balanced-expected-state.json) into the
    installed WinForge-customized VM and run it from an ADMINISTRATOR prompt
    after reaching the desktop. It collects STRUCTURED evidence and writes
    full-health-report.json — it does not merely print "OK".

    The report schema is consumed by the WinForge health-report parser
    (src/WinForge.Infrastructure/Health/HealthReportParser.cs) which
    re-aggregates statuses authoritatively. Status vocabulary:
    Pass | Warning | Fail | NotTested. No binary false confidence: an offline
    VM, an unactivated Windows, or an unavailable VMware audio device are
    reported distinctly and never masquerade as product failures.

    Checks are NON-DESTRUCTIVE: DISM /CheckHealth and sfc /verifyonly only.
    /ScanHealth runs only with -ScanHealth. Nothing is repaired automatically.

.PARAMETER ProfileId
    Profile under validation (default Balanced).

.PARAMETER MediaId
    ISO identity string recorded in the report (e.g. the ISO file name).

.PARAMETER IsoSha256
    ISO SHA-256 recorded in the report when known (host-side computed).

.PARAMETER ExpectedJson
    Path to the profile expected-state JSON (default: script-dir\balanced-expected-state.json).

.PARAMETER OutputPath
    Report output path (default .\full-health-report.json).

.PARAMETER ScanHealth
    Also run DISM /ScanHealth (slower). Off by default.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File Validate-WinForgeInstallation.ps1 `
        -ProfileId Balanced -MediaId "WinForge-Balanced-Win11-25H2-Pro-zh-CN-x64.iso"
#>
[CmdletBinding()]
param(
    [string]$ProfileId = "Balanced",
    [string]$MediaId = "",
    [string]$IsoSha256 = "",
    [string]$ExpectedJson = "",
    [string]$OutputPath = ".\full-health-report.json",
    [switch]$ScanHealth
)

$ErrorActionPreference = "Continue"
$scriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $ExpectedJson) { $ExpectedJson = Join-Path $scriptDir "balanced-expected-state.json" }

$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole(
    [Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Warning "Not running elevated. DISM /CheckHealth, sfc /verifyonly and registry hive loads require an Administrator prompt."
}

function New-HealthSection {
    return @{ status = "NotTested"; checks = @() }
}

function Add-Check {
    param($Section, [string]$Name, [string]$Status, [string]$Detail)
    $check = @{ name = $Name; status = $Status; detail = $Detail }
    $Section.checks = @($Section.checks) + $check
}

function Resolve-SectionStatus {
    param([hashtable]$Section)
    # Fail > Warning > NotTested > Pass
    $rank = @{ "Fail" = 3; "Warning" = 2; "NotTested" = 1; "Pass" = 0 }
    $worst = "Pass"; $worstRank = 0
    foreach ($c in $Section.checks) {
        $r = $rank[[string]$c.status]
        if ($null -eq $r) { $r = 1 }
        if ($r -gt $worstRank) { $worstRank = $r; $worst = $c.status }
    }
    return $worst
}

function Get-DwordAsHex {
    param($Value)
    if ($null -eq $Value) { return $null }
    if ($Value -is [int]) { return ("0x{0:X}" -f $Value) }
    return $Value.ToString()
}

# =====================================================================
$report = @{
    sections = @{
        media                   = New-HealthSection
        profile                 = New-HealthSection
        windowsIdentity         = New-HealthSection
        bootAndShell            = New-HealthSection
        devices                 = New-HealthSection
        network                 = New-HealthSection
        servicing               = New-HealthSection
        windowsUpdate           = New-HealthSection
        security                = New-HealthSection
        storeAndAppPlatform     = New-HealthSection
        profileExpectedChanges  = New-HealthSection
    }
    overallStatus = "NotTested"
    warnings = @()
    failures = @()
    fullHealthValidated = $false
}

# ---- media ----
$m = $report.sections.media
$mediaDetail = if ($MediaId) { $MediaId } else { "(not provided — pass -MediaId for full evidence)" }
Add-Check $m "isoMedia" "Pass" $mediaDetail
if ($IsoSha256) { Add-Check $m "isoSha256" "Pass" $IsoSha256 } else { Add-Check $m "isoSha256" "Warning" "ISO SHA-256 not provided (host-side computed)" }
$m.status = Resolve-SectionStatus $m

# ---- profile ----
$p = $report.sections.profile
Add-Check $p "profileId" "Pass" $ProfileId
$p.status = Resolve-SectionStatus $p

# ---- windowsIdentity ----
$wi = $report.sections.windowsIdentity
$cv = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
if ($cv) {
    Add-Check $wi "edition" "Pass" ([string]$cv.ProductName)
    $buildText = "$($cv.CurrentBuildNumber).$($cv.UBR) ($($cv.DisplayVersion))"
    Add-Check $wi "build" "Pass" $buildText
} else {
    Add-Check $wi "edition" "NotTested" "Windows NT CurrentVersion key not readable"
}
$arch = (Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue).OSArchitecture
if ($arch) { Add-Check $wi "architecture" "Pass" $arch } else { Add-Check $wi "architecture" "NotTested" "OSArchitecture not readable" }
try { $lang = (Get-WinSystemLocale).Name; Add-Check $wi "language" "Pass" $lang } catch { Add-Check $wi "language" "NotTested" "Get-WinSystemLocale unavailable" }

# Activation: REPORT ONLY (activation is not required for validation).
$lic = Get-CimInstance SoftwareLicensingProduct -ErrorAction SilentlyContinue | Where-Object { $_.PartialProductKey } | Select-Object -First 1
if ($lic) {
    $map = @{ 1 = "Licensed"; 2 = "OOBGrace"; 3 = "OOTGrace"; 4 = "NonGenuineGrace"; 5 = "Notification"; 6 = "ExtendedGrace" }
    $state = if ($map.ContainsKey([int]$lic.LicenseStatus)) { $map[[int]$lic.LicenseStatus] } else { "Status $($lic.LicenseStatus)" }
    if ([int]$lic.LicenseStatus -eq 1) { Add-Check $wi "activation" "Pass" $state }
    else { Add-Check $wi "activation" "Warning" "$state (report only — activation not required for validation)" }
} else {
    Add-Check $wi "activation" "NotTested" "No licensing product with partial key found"
}
$boot = (Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue).LastBootUpTime
if ($boot) { Add-Check $wi "systemBoot" "Pass" "Last boot: $boot" } else { Add-Check $wi "systemBoot" "NotTested" "LastBootUpTime not readable" }
$wi.status = Resolve-SectionStatus $wi

# ---- bootAndShell ----
$bs = $report.sections.bootAndShell
if (Get-Process explorer -ErrorAction SilentlyContinue) { Add-Check $bs "explorer" "Pass" "Explorer shell process running" }
else { Add-Check $bs "explorer" "Fail" "Explorer shell process NOT running — desktop may not be reachable" }
if (Get-Process StartMenuExperienceHost -ErrorAction SilentlyContinue) { Add-Check $bs "startMenu" "Pass" "Start menu host process running" }
else { Add-Check $bs "startMenu" "Warning" "StartMenuExperienceHost not running (may start on demand)" }
$bs.status = Resolve-SectionStatus $bs

# ---- devices ----
$dv = $report.sections.devices
$problemDevices = @(Get-PnpDevice -PresentOnly -ErrorAction SilentlyContinue | Where-Object { $_.Status -ne "OK" -and $_.Class -notin @("SoftwareComponent", "SecurityDevices") })
if ($problemDevices.Count -eq 0) { Add-Check $dv "deviceProblems" "Pass" "No Device Manager problem devices" }
else {
    $names = ($problemDevices | Select-Object -First 5 | ForEach-Object { "$($_.FriendlyName) [$($_.Status)]" }) -join "; "
    Add-Check $dv "deviceProblems" "Fail" "$($problemDevices.Count) problem device(s): $names"
}
$display = Get-CimInstance Win32_VideoController -ErrorAction SilentlyContinue | Select-Object -First 1
if ($display) { Add-Check $dv "displayAdapter" "Pass" $display.Name } else { Add-Check $dv "displayAdapter" "Fail" "No display adapter reported" }
$netAdapters = @(Get-NetAdapter -ErrorAction SilentlyContinue | Where-Object { $_.Status -eq "Up" })
if ($netAdapters.Count -gt 0) { Add-Check $dv "networkAdapter" "Pass" (($netAdapters | ForEach-Object { $_.Name }) -join "; ") }
else { Add-Check $dv "networkAdapter" "Fail" "No connected network adapter" }
$audio = Get-CimInstance Win32_SoundDevice -ErrorAction SilentlyContinue | Select-Object -First 1
if ($audio) { Add-Check $dv "audioDevice" "Pass" $audio.Name }
else { Add-Check $dv "audioDevice" "Warning" "No audio device — expected for a VMware VM without an audio device; not a product failure" }
$dv.status = Resolve-SectionStatus $dv

# ---- network ----
$nw = $report.sections.network
$ip = @(Get-NetIPAddress -AddressFamily IPv4 -ErrorAction SilentlyContinue | Where-Object { $_.IPAddress -notlike "169.254.*" -and $_.IPAddress -ne "127.0.0.1" })
if ($ip.Count -gt 0) { Add-Check $nw "dhcpIp" "Pass" (($ip | ForEach-Object { "$($_.IPAddress) on $($_.InterfaceAlias)" }) -join "; ") }
else { Add-Check $nw "dhcpIp" "Fail" "No non-APIPA IPv4 address assigned" }
try {
    $dns = Resolve-DnsName www.microsoft.com -ErrorAction Stop | Where-Object { $_.Type -in @("A", "AAAA") } | Select-Object -First 1
    if ($dns) { Add-Check $nw "dnsResolution" "Pass" "Resolved www.microsoft.com -> $($dns.IPAddress)" }
    else { Add-Check $nw "dnsResolution" "Fail" "DNS resolution returned no A/AAAA record" }
} catch {
    Add-Check $nw "dnsResolution" "Warning" "DNS resolution failed: $($_.Exception.Message)"
}
try {
    $resp = Invoke-WebRequest "https://www.msftconnecttest.com/connecttest.txt" -TimeoutSec 8 -UseBasicParsing -ErrorAction Stop
    if ($resp.Content -match "Microsoft Connect Test") { Add-Check $nw "httpsConnectivity" "Pass" "HTTPS to msftconnecttest.com OK" }
    else { Add-Check $nw "httpsConnectivity" "Warning" "HTTPS endpoint reachable but unexpected content" }
} catch {
    Add-Check $nw "httpsConnectivity" "Warning" "HTTPS unavailable: $($_.Exception.Message). Deliberately-offline VM is NOT a product failure."
}
$nw.status = Resolve-SectionStatus $nw

# ---- servicing ----
$sv = $report.sections.servicing
$dism = & dism.exe /English /Online /Cleanup-Image /CheckHealth 2>&1
if ($LASTEXITCODE -eq 0 -and (($dism -join "`n") -match "No component store corruption detected")) {
    Add-Check $sv "dismCheckHealth" "Pass" "No component store corruption detected"
} else {
    $tail = ($dism | Select-Object -Last 3) -join "; "
    Add-Check $sv "dismCheckHealth" "Fail" "CheckHealth did not pass (exit $LASTEXITCODE): $tail"
}
if ($ScanHealth) {
    $scan = & dism.exe /English /Online /Cleanup-Image /ScanHealth 2>&1
    if ($LASTEXITCODE -eq 0 -and (($scan -join "`n") -match "No component store corruption detected")) {
        Add-Check $sv "dismScanHealth" "Pass" "ScanHealth: no component store corruption detected"
    } else {
        $tail = ($scan | Select-Object -Last 3) -join "; "
        Add-Check $sv "dismScanHealth" "Warning" "ScanHealth did not fully pass (exit $LASTEXITCODE): $tail"
    }
} else {
    Add-Check $sv "dismScanHealth" "NotTested" "Skipped (opt-in -ScanHealth)"
}
$sfc = & sfc.exe /verifyonly 2>&1
if ($LASTEXITCODE -eq 0 -and (($sfc -join "`n") -match "did not find any integrity violations")) {
    Add-Check $sv "sfcVerifyOnly" "Pass" "No integrity violations"
} else {
    $tail = ($sfc | Select-Object -Last 3) -join "; "
    Add-Check $sv "sfcVerifyOnly" "Fail" "sfc /verifyonly did not pass (exit $LASTEXITCODE): $tail"
}
$sv.status = Resolve-SectionStatus $sv

# ---- windowsUpdate ----
$wu = $report.sections.windowsUpdate
foreach ($svcName in @("wuauserv", "UsoSvc")) {
    $svc = Get-Service $svcName -ErrorAction SilentlyContinue
    if ($svc) { Add-Check $wu $svcName "Pass" "$svcName present (status $($svc.Status))" }
    else { Add-Check $wu $svcName "Fail" "$svcName missing" }
}
if (Get-Command winget -ErrorAction SilentlyContinue) { Add-Check $wu "winget" "Pass" "winget (App Installer) present" }
else { Add-Check $wu "winget" "Warning" "winget not on PATH" }
if (Get-AppxPackage *immersivecontrolpanel* -ErrorAction SilentlyContinue) { Add-Check $wu "settingsApp" "Pass" "Windows Settings app present" }
else { Add-Check $wu "settingsApp" "Warning" "Settings app package not found" }
$wu.status = Resolve-SectionStatus $wu

# ---- security ----
$se = $report.sections.security
if (Get-AppxPackage *SecHealthUI* -ErrorAction SilentlyContinue) { Add-Check $se "secHealthUi" "Pass" "Windows Security (SecHealthUI) present" }
else { Add-Check $se "secHealthUi" "Fail" "Windows Security UI missing" }
$def = Get-Service WinDefend -ErrorAction SilentlyContinue
if ($def) { Add-Check $se "defender" "Pass" "Windows Defender service present (status $($def.Status))" }
else { Add-Check $se "defender" "Fail" "Windows Defender service missing" }
$fw = Get-Service mpssvc -ErrorAction SilentlyContinue
if ($fw) { Add-Check $se "firewall" "Pass" "Windows Defender Firewall service present (status $($fw.Status))" }
else { Add-Check $se "firewall" "Fail" "Windows Firewall service missing" }
$profiles = @(Get-NetFirewallProfile -ErrorAction SilentlyContinue | Where-Object { $_.Enabled })
if ($profiles.Count -gt 0) { Add-Check $se "firewallEnabled" "Pass" "Firewall enabled on $($profiles.Count) profile(s)" }
else { Add-Check $se "firewallEnabled" "Warning" "No enabled firewall profile" }
# Defender signatures: report only — a VM without internet must not fail security.
try {
    $sig = (Get-MpComputerStatus -ErrorAction Stop)
    Add-Check $se "defenderSignatures" "Pass" "Antivirus signatures: $($sig.AntivirusSignatureVersion) (age $($sig.AntivirusSignatureAge) days)"
} catch {
    Add-Check $se "defenderSignatures" "NotTested" "Signature status unavailable (offline VM or module missing) — not a product failure"
}
$se.status = Resolve-SectionStatus $se

# ---- storeAndAppPlatform ----
$sa = $report.sections.storeAndAppPlatform
$store = Get-AppxPackage *WindowsStore* -ErrorAction SilentlyContinue
if ($store) { Add-Check $sa "microsoftStore" "Pass" "Microsoft Store present ($($store[0].Version))" }
else { Add-Check $sa "microsoftStore" "Fail" "Microsoft Store missing" }
$ai = Get-AppxPackage *DesktopAppInstaller* -ErrorAction SilentlyContinue
if ($ai) { Add-Check $sa "appInstaller" "Pass" "App Installer present ($($ai[0].Version))" }
else { Add-Check $sa "appInstaller" "Warning" "App Installer not found" }
$vc = Get-AppxPackage *VCLibs.140* -ErrorAction SilentlyContinue
if ($vc) { Add-Check $sa "vclibs" "Pass" "VC++ runtime framework present" }
else { Add-Check $sa "vclibs" "Fail" "VCLibs.140 framework missing" }
$net = Get-AppxPackage *NET.Native.Framework* -ErrorAction SilentlyContinue
if ($net) { Add-Check $sa "netNativeFramework" "Pass" ".NET Native framework present" }
else { Add-Check $sa "netNativeFramework" "Warning" ".NET Native framework not found" }
$sa.status = Resolve-SectionStatus $sa

# ---- profileExpectedChanges ----
$pe = $report.sections.profileExpectedChanges
$expected = $null
if (Test-Path $ExpectedJson) { $expected = Get-Content $ExpectedJson -Raw | ConvertFrom-Json }
if (-not $expected) {
    Add-Check $pe "expectedState" "NotTested" "Expected-state JSON not found at $ExpectedJson"
} else {
    foreach ($family in $expected.appxAbsent) {
        $found = Get-AppxPackage -AllUsers -Name "*$family*" -ErrorAction SilentlyContinue
        $found += Get-AppxPackage -Name "*$family*" -ErrorAction SilentlyContinue
        if ($found) { Add-Check $pe "appxAbsent_$family" "Fail" "$family is still present (expected removed by profile)" }
        else { Add-Check $pe "appxAbsent_$family" "Pass" "$family absent (removed as expected)" }
    }
    foreach ($reg in $expected.machineRegistry) {
        $keyPath = "HKLM:\$($reg.path)"
        $val = (Get-ItemProperty $keyPath -Name $reg.name -ErrorAction SilentlyContinue).$($reg.name)
        $expectedHex = "0x$($reg.expectedData)" -replace "0x0x", "0x"
        if ($null -eq $val) { Add-Check $pe "reg_$($reg.name)" "Fail" "$($reg.path)\$($reg.name) missing (expected $($reg.expectedData))" }
        elseif ((Get-DwordAsHex $val) -eq $expectedHex) { Add-Check $pe "reg_$($reg.name)" "Pass" "$($reg.path)\$($reg.name) = $val" }
        else { Add-Check $pe "reg_$($reg.name)" "Fail" "$($reg.path)\$($reg.name) = $val (expected $($reg.expectedData))" }
    }
    $hive = "WinForgeHealth"
    $hiveLoaded = $false
    if ($isAdmin) {
        & reg.exe load "HKU\$hive" "C:\Users\Default\NTUSER.DAT" 2>$null | Out-Null
        $hiveLoaded = ($LASTEXITCODE -eq 0)
    }
    if ($hiveLoaded) {
        foreach ($reg in $expected.defaultUserRegistry) {
            $keyPath = "HKU:\$hive\$($reg.path)"
            $val = (Get-ItemProperty $keyPath -Name $reg.name -ErrorAction SilentlyContinue).$($reg.name)
            $expectedHex = "0x$($reg.expectedData)" -replace "0x0x", "0x"
            if ($null -eq $val) { Add-Check $pe "defaultUser_$($reg.name)" "Fail" "DefaultUser $($reg.path)\$($reg.name) missing (expected $($reg.expectedData))" }
            elseif ((Get-DwordAsHex $val) -eq $expectedHex) { Add-Check $pe "defaultUser_$($reg.name)" "Pass" "DefaultUser $($reg.path)\$($reg.name) = $val" }
            else { Add-Check $pe "defaultUser_$($reg.name)" "Fail" "DefaultUser $($reg.path)\$($reg.name) = $val (expected $($reg.expectedData))" }
        }
        & reg.exe unload "HKU\$hive" 2>$null | Out-Null
    } else {
        Add-Check $pe "defaultUserRegistry" "NotTested" "Default User hive not loaded (requires Administrator) — re-run elevated for full evidence"
    }
}
$pe.status = Resolve-SectionStatus $pe

# ---- overall aggregation ----
$rank = @{ "Fail" = 3; "Warning" = 2; "NotTested" = 1; "Pass" = 0 }
$worst = "Pass"; $worstRank = 0
foreach ($name in @("media","profile","windowsIdentity","bootAndShell","devices","network","servicing","windowsUpdate","security","storeAndAppPlatform","profileExpectedChanges")) {
    $s = $report.sections[$name].status
    $r = $rank[$s]
    if ($null -eq $r) { $r = 1 }
    if ($r -gt $worstRank) { $worstRank = $r; $worst = $s }
}
$report.overallStatus = $worst

# Failures / warnings (from non-Pass checks)
$failures = @(); $warnings = @()
foreach ($name in @("media","profile","windowsIdentity","bootAndShell","devices","network","servicing","windowsUpdate","security","storeAndAppPlatform","profileExpectedChanges")) {
    foreach ($c in $report.sections[$name].checks) {
        if ($c.status -eq "Fail") { $failures += "${name}: $($c.name) — $($c.detail)" }
        elseif ($c.status -eq "Warning") { $warnings += "${name}: $($c.name) — $($c.detail)" }
    }
}
$report.failures = $failures
$report.warnings = $warnings

# ADR-084 FullHealthValidated gate: no Fail anywhere, and the critical sections
# (bootAndShell, servicing, security, network) actually Pass.
$critical = @("bootAndShell", "servicing", "security", "network")
$gate = ($report.overallStatus -eq "Pass")
foreach ($c in $critical) {
    if ($report.sections[$c].status -ne "Pass") { $gate = $false }
}
$report.fullHealthValidated = $gate

# ---- write ----
$report | ConvertTo-Json -Depth 8 | Set-Content -Path $OutputPath -Encoding UTF8
Write-Host ""
Write-Host "=== WinForge FULL HEALTH REPORT ==="
Write-Host "Overall : $($report.overallStatus)"
Write-Host "FullHealthValidated : $($report.fullHealthValidated)"
foreach ($name in @("media","profile","windowsIdentity","bootAndShell","devices","network","servicing","windowsUpdate","security","storeAndAppPlatform","profileExpectedChanges")) {
    Write-Host ("  {0,-24} {1}" -f $name, $report.sections[$name].status)
}
if ($failures.Count -gt 0) {
    Write-Host ""
    Write-Host "FAILURES:"
    $failures | ForEach-Object { Write-Host "  - $_" }
}
if ($warnings.Count -gt 0) {
    Write-Host ""
    Write-Host "WARNINGS:"
    $warnings | ForEach-Object { Write-Host "  - $_" }
}
Write-Host ""
Write-Host "Report written to: $OutputPath"
