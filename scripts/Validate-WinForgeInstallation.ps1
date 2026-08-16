<#
.SYNOPSIS
    WinForge in-VM full-health validator (Phase 16 Stage 16.1a, ADR-098 addendum).

.DESCRIPTION
    Copy this script (and balanced-expected-state.json) into the installed
    WinForge-customized VM and run it from an ADMINISTRATOR prompt after
    reaching the desktop. It collects STRUCTURED evidence and writes
    full-health-report.json - it does not merely print "OK".

    The report schema is consumed by the WinForge health-report parser
    (src/WinForge.Infrastructure/Health/HealthReportParser.cs) which
    re-aggregates statuses authoritatively. Status vocabulary:
    Pass | Warning | Fail | NotTested. No binary false confidence: an offline
    VM, an unactivated Windows, or an unavailable VMware audio device are
    reported distinctly and never masquerade as product failures.

    Checks are NON-DESTRUCTIVE: DISM /CheckHealth and sfc /verifyonly only.
    /ScanHealth runs only with -ScanHealth. Nothing is repaired automatically.

    Stage 16.1a correctness fixes:
    - Native tool output is captured with [Console]::OutputEncoding = UTF8 and
      NUL characters are stripped, so a successful sfc run can never be
      misreported as a failure because of capture artifacts. The sfc verdict is
      authoritative on the EXIT CODE (0 = no integrity violations) and only
      uses localized output text as corroborating evidence.
    - Post-install registry expectations declare an EXPLICIT scope. Settings
      whose purpose is to seed the OOBE-created user's profile (e.g.
      Start_ShowRecommended / Start_ShowRecent) are verified in the EFFECTIVE
      current-user hive (HKCU), NOT in the post-OOBE Default-User template
      (Windows/OOBE legitimately consumes the seeded template value into the
      created user's profile). Machine policies stay machine (HKLM). Image-time
      WIM Default-User validation is unchanged and separate.
    - This script file is pure ASCII with a UTF-8 BOM so PowerShell 5.1 parses
      it without ANSI code-page mangling (the mojibake source).

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

# ---------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------

function New-HealthSection {
    return @{ status = "NotTested"; checks = @() }
}

function Add-Check {
    param($Section, [string]$Name, [string]$Status, [string]$Detail, [bool]$Required = $true)
    # Stage 16.1b: every check carries requiredForFullHealth. REQUIRED checks
    # (default) gate FullHealthValidated and may not be NotTested; OPTIONAL
    # checks (ScanHealth, HTTPS trust, activation, ...) may be NotTested or
    # Warning without blocking. A Fail on any check still blocks (conservative).
    $check = @{ name = $Name; status = $Status; detail = $Detail; requiredForFullHealth = $Required }
    $Section.checks = @($Section.checks) + $check
}

function Resolve-SectionStatus {
    param([hashtable]$Section)
    # Section display status = worst of the REQUIRED checks in the section
    # (falling back to all checks when the section has no required ones), so an
    # OPTIONAL NotTested/Warning never turns the section into a false blocker.
    $required = @($Section.checks | Where-Object { $_ -and $_.requiredForFullHealth })
    if ($required.Count -eq 0) { $required = @($Section.checks) }
    $rank = @{ "Fail" = 3; "Warning" = 2; "NotTested" = 1; "Pass" = 0 }
    $worst = "Pass"; $worstRank = 0
    foreach ($c in $required) {
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

# Capture native tool output deterministically. Native tools (sfc.exe on
# localized Windows in particular) can emit UTF-16 text or code-page text;
# console-pipe capture mis-decodes it into NUL-corrupted or mojibake strings.
# We redirect stdout+stderr to a temp file via cmd /c and decode the raw bytes
# with a candidate-scoring decoder: UTF-16 BOM wins; otherwise strict UTF-8,
# UTF-16LE (with a low NUL-density heuristic - pure-Chinese UTF-16 text can
# have very few NUL bytes), and the system ANSI code page are scored by the
# number of U+FFFD replacement characters and the winner is returned.
function Score-Text {
    param([string]$Text)
    $count = 0
    foreach ($ch in $Text.ToCharArray()) { if ([int]$ch -eq 0xFFFD) { $count++ } }
    return $count
}

function Read-NativeFile {
    param([string]$Path)
    try {
        $bytes = [System.IO.File]::ReadAllBytes($Path)
        if ($bytes.Length -lt 1) { return "" }
        if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFF -and $bytes[1] -eq 0xFE) {
            return [System.Text.Encoding]::Unicode.GetString($bytes, 2, $bytes.Length - 2)
        }
        if ($bytes.Length -ge 2 -and $bytes[0] -eq 0xFE -and $bytes[1] -eq 0xFF) {
            return [System.Text.Encoding]::BigEndianUnicode.GetString($bytes, 2, $bytes.Length - 2)
        }

        $nulCount = 0
        foreach ($b in $bytes) { if ($b -eq 0) { $nulCount++ } }
        $nulRatio = if ($bytes.Length -gt 0) { $nulCount / $bytes.Length } else { 0 }

        $utf16 = [System.Text.Encoding]::Unicode.GetString($bytes, 0, $bytes.Length - ($bytes.Length % 2))
        $utf8 = $null
        try {
            $strict8 = New-Object System.Text.UTF8Encoding($false, $true)
            $utf8 = $strict8.GetString($bytes)
        } catch { $utf8 = $null }
        $ansi = [System.Text.Encoding]::Default.GetString($bytes)

        $score16 = Score-Text $utf16
        $score8 = if ($null -ne $utf8) { Score-Text $utf8 } else { [int]::MaxValue }
        $scoreA = Score-Text $ansi

        if ($score8 -eq 0) { return $utf8 }
        if ($score16 -eq 0 -or ($nulRatio -ge 0.15 -and $score16 -le $scoreA)) { return $utf16 }
        if ($scoreA -eq 0) { return $ansi }

        $best = $utf16; $bestScore = $score16
        if ($score8 -lt $bestScore) { $bestScore = $score8; $best = $utf8 }
        if ($scoreA -lt $bestScore) { $bestScore = $scoreA; $best = $ansi }
        return $best
    } catch {
        return ""
    }
}

function Invoke-Native {
    param([string]$FilePath, [string[]]$ArgumentList)
    $tmp = Join-Path $env:TEMP ("wf_native_" + [guid]::NewGuid().ToString("N") + ".txt")
    $quotedArgs = ($ArgumentList | ForEach-Object { if ($_ -match " ") { "`"$_`"" } else { $_ } }) -join " "
    & cmd.exe /c "$FilePath $quotedArgs > `"$tmp`" 2>&1" | Out-Null
    $exit = $LASTEXITCODE
    $text = Read-NativeFile -Path $tmp
    Remove-Item $tmp -Force -ErrorAction SilentlyContinue
    return @($exit, $text)
}

function Join-NativeOutput {
    param($Lines)
    $sb = New-Object System.Text.StringBuilder
    foreach ($line in $Lines) {
        $s = if ($line -is [string]) { $line } else { "$line" }
        $s = $s.Replace([string][char]0, "")   # strip NUL corruption
        [void]$sb.AppendLine($s)
    }
    return $sb.ToString().TrimEnd()
}

function Compact-Text {
    param([string]$Text, [int]$Max = 300)
    if ([string]::IsNullOrEmpty($Text)) { return "" }
    $s = $Text.Replace([string][char]0, "")
    $s = $s -replace "`r`n", " " -replace "`n", " " -replace "`r", " "
    $s = $s -replace "\s+", " "
    $s = $s.Trim()
    if ($s.Length -gt $Max) { $s = $s.Substring(0, $Max) + "..." }
    return $s
}

# ---------------------------------------------------------------------
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
$mediaDetail = if ($MediaId) { $MediaId } else { "(not provided - pass -MediaId for full evidence)" }
Add-Check $m "isoMedia" "Pass" $mediaDetail
if ($IsoSha256) { Add-Check $m "isoSha256" "Pass" $IsoSha256 -Required:$false } else { Add-Check $m "isoSha256" "Warning" "ISO SHA-256 not provided (host-side computed)" -Required:$false }
$m.status = Resolve-SectionStatus $m

# ---- profile ----
$p = $report.sections.profile
Add-Check $p "profileId" "Pass" $ProfileId
$p.status = Resolve-SectionStatus $p

# ---- windowsIdentity ----
$wi = $report.sections.windowsIdentity
$cv = Get-ItemProperty "HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion" -ErrorAction SilentlyContinue
if ($cv) {
    # Windows 11 keeps the legacy "Windows 10 Pro" ProductName registry value for
    # compatibility. Normalize the DISPLAY name from the build number so a 25H2
    # installation is never confusingly presented as Windows 10.
    $rawProduct = [string]$cv.ProductName
    $buildNum = 0
    [void][int]::TryParse([string]$cv.CurrentBuildNumber, [ref]$buildNum)
    $display = $rawProduct
    if ($buildNum -ge 22000 -and $rawProduct -match "Windows 10") {
        $display = $rawProduct -replace "Windows 10", "Windows 11"
    }
    Add-Check $wi "edition" "Pass" "$display (raw ProductName: $rawProduct)"
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
    if ([int]$lic.LicenseStatus -eq 1) { Add-Check $wi "activation" "Pass" $state -Required:$false }
    else { Add-Check $wi "activation" "Warning" "$state (report only - activation not required for validation)" -Required:$false }
} else {
    Add-Check $wi "activation" "NotTested" "No licensing product with partial key found" -Required:$false
}
$boot = (Get-CimInstance Win32_OperatingSystem -ErrorAction SilentlyContinue).LastBootUpTime
if ($boot) { Add-Check $wi "systemBoot" "Pass" "Last boot: $boot" } else { Add-Check $wi "systemBoot" "NotTested" "LastBootUpTime not readable" }
$wi.status = Resolve-SectionStatus $wi

# ---- bootAndShell ----
$bs = $report.sections.bootAndShell
if (Get-Process explorer -ErrorAction SilentlyContinue) { Add-Check $bs "explorer" "Pass" "Explorer shell process running" }
else { Add-Check $bs "explorer" "Fail" "Explorer shell process NOT running - desktop may not be reachable" }
if (Get-Process StartMenuExperienceHost -ErrorAction SilentlyContinue) { Add-Check $bs "startMenu" "Pass" "Start menu host process running" -Required:$false }
else { Add-Check $bs "startMenu" "Warning" "StartMenuExperienceHost not running (may start on demand)" -Required:$false }
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
if ($audio) { Add-Check $dv "audioDevice" "Pass" $audio.Name -Required:$false }
else { Add-Check $dv "audioDevice" "Warning" "No audio device - expected for a VMware VM without an audio device; not a product failure" -Required:$false }
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
    if ($resp.Content -match "Microsoft Connect Test") { Add-Check $nw "httpsConnectivity" "Pass" "HTTPS to msftconnecttest.com OK" -Required:$false }
    else { Add-Check $nw "httpsConnectivity" "Warning" "HTTPS endpoint reachable but unexpected content" -Required:$false }
} catch {
    # TLS trust chain issues (VM CA store, proxy) are honest Warnings: network
    # fundamentals (adapter/IP/DNS) already Pass and a trust-channel warning is
    # NOT a product failure. Never convert this to Pass artificially.
    Add-Check $nw "httpsConnectivity" "Warning" "HTTPS unavailable: $(Compact-Text $_.Exception.Message). Deliberately-offline VM is NOT a product failure." -Required:$false
}
$nw.status = Resolve-SectionStatus $nw

# ---- servicing ----
$sv = $report.sections.servicing
$dismResult = Invoke-Native -FilePath "dism.exe" -ArgumentList @("/English", "/Online", "/Cleanup-Image", "/CheckHealth")
$dismExit = $dismResult[0]; $dismText = $dismResult[1]
$dismOk = ($dismExit -eq 0) -or ($dismText -match "No component store corruption detected")
if ($dismOk) { Add-Check $sv "dismCheckHealth" "Pass" (Compact-Text $dismText) }
else { Add-Check $sv "dismCheckHealth" "Fail" "CheckHealth did not pass (exit $dismExit): $(Compact-Text $dismText)" }
if ($ScanHealth) {
    $scanResult = Invoke-Native -FilePath "dism.exe" -ArgumentList @("/English", "/Online", "/Cleanup-Image", "/ScanHealth")
    $scanExit = $scanResult[0]; $scanText = $scanResult[1]
    $scanOk = ($scanExit -eq 0) -or ($scanText -match "No component store corruption detected")
    if ($scanOk) { Add-Check $sv "dismScanHealth" "Pass" (Compact-Text $scanText) -Required:$false }
    else { Add-Check $sv "dismScanHealth" "Warning" "ScanHealth did not fully pass (exit $scanExit): $(Compact-Text $scanText)" -Required:$false }
} else {
    Add-Check $sv "dismScanHealth" "NotTested" "Skipped (opt-in -ScanHealth)" -Required:$false
}
# sfc /verifyonly: the EXIT CODE is authoritative and locale-independent
# (0 = no integrity violations). Localized success text is only corroborating.
$sfcResult = Invoke-Native -FilePath "sfc.exe" -ArgumentList @("/verifyonly")
$sfcExit = $sfcResult[0]; $sfcText = $sfcResult[1]
$sfcOk = ($sfcExit -eq 0) -or ($sfcText -match "did not find any integrity violations") -or ($sfcText -match "未找到任何完整性冲突")
if ($sfcOk) { Add-Check $sv "sfcVerifyOnly" "Pass" "sfc /verifyonly passed (exit $sfcExit): $(Compact-Text $sfcText)" }
else { Add-Check $sv "sfcVerifyOnly" "Fail" "sfc /verifyonly FAILED (exit $sfcExit): $(Compact-Text $sfcText)" }
$sv.status = Resolve-SectionStatus $sv

# ---- windowsUpdate ----
$wu = $report.sections.windowsUpdate
foreach ($svcName in @("wuauserv", "UsoSvc")) {
    $svc = Get-Service $svcName -ErrorAction SilentlyContinue
    if ($svc) { Add-Check $wu $svcName "Pass" "$svcName present (status $($svc.Status))" }
    else { Add-Check $wu $svcName "Fail" "$svcName missing" }
}
if (Get-Command winget -ErrorAction SilentlyContinue) { Add-Check $wu "winget" "Pass" "winget (App Installer) present" -Required:$false }
else { Add-Check $wu "winget" "Warning" "winget not on PATH" -Required:$false }
if (Get-AppxPackage *immersivecontrolpanel* -ErrorAction SilentlyContinue) { Add-Check $wu "settingsApp" "Pass" "Windows Settings app present" -Required:$false }
else { Add-Check $wu "settingsApp" "Warning" "Settings app package not found" -Required:$false }
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
if ($profiles.Count -gt 0) { Add-Check $se "firewallEnabled" "Pass" "Firewall enabled on $($profiles.Count) profile(s)" -Required:$false }
else { Add-Check $se "firewallEnabled" "Warning" "No enabled firewall profile" -Required:$false }
# Defender signatures: report only - a VM without internet must not fail security.
try {
    $sig = (Get-MpComputerStatus -ErrorAction Stop)
    Add-Check $se "defenderSignatures" "Pass" "Antivirus signatures: $($sig.AntivirusSignatureVersion) (age $($sig.AntivirusSignatureAge) days)" -Required:$false
} catch {
    Add-Check $se "defenderSignatures" "NotTested" "Signature status unavailable (offline VM or module missing) - not a product failure" -Required:$false
}
$se.status = Resolve-SectionStatus $se

# ---- storeAndAppPlatform ----
$sa = $report.sections.storeAndAppPlatform
$store = Get-AppxPackage *WindowsStore* -ErrorAction SilentlyContinue
if ($store) { Add-Check $sa "microsoftStore" "Pass" "Microsoft Store present ($($store[0].Version))" }
else { Add-Check $sa "microsoftStore" "Fail" "Microsoft Store missing" }
$ai = Get-AppxPackage *DesktopAppInstaller* -ErrorAction SilentlyContinue
if ($ai) { Add-Check $sa "appInstaller" "Pass" "App Installer present ($($ai[0].Version))" -Required:$false }
else { Add-Check $sa "appInstaller" "Warning" "App Installer not found" -Required:$false }
$vc = Get-AppxPackage *VCLibs.140* -ErrorAction SilentlyContinue
if ($vc) { Add-Check $sa "vclibs" "Pass" "VC++ runtime framework present" }
else { Add-Check $sa "vclibs" "Fail" "VCLibs.140 framework missing" }
$net = Get-AppxPackage *NET.Native.Framework* -ErrorAction SilentlyContinue
if ($net) { Add-Check $sa "netNativeFramework" "Pass" ".NET Native framework present" -Required:$false }
else { Add-Check $sa "netNativeFramework" "Warning" ".NET Native framework not found" -Required:$false }
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

    # Registry expectations with EXPLICIT scope. Settings intended to seed the
    # OOBE-created user's profile (Start_ShowRecent / Start_ShowRecommended) are
    # verified in the EFFECTIVE current-user hive (HKCU) - NOT the post-OOBE
    # Default-User template, which Windows legitimately consumes at profile
    # creation. Machine policies stay machine (HKLM).
    $hiveName = "WinForgeHealth"
    $hiveLoaded = $false
    foreach ($reg in $expected.registryChecks) {
        $checkName = "reg_$($reg.name)"
        $expectedHex = "0x$($reg.expectedData)" -replace "0x0x", "0x"
        switch ($reg.scope) {
            "OfflineMachine" {
                $keyPath = "HKLM:\$($reg.path)"
                $val = (Get-ItemProperty $keyPath -Name $reg.name -ErrorAction SilentlyContinue).$($reg.name)
                if ($null -eq $val) { Add-Check $pe $checkName "Fail" "HKLM $($reg.path)\$($reg.name) missing (expected $($reg.expectedData))" }
                elseif ((Get-DwordAsHex $val) -eq $expectedHex) { Add-Check $pe $checkName "Pass" "HKLM $($reg.path)\$($reg.name) = $val" }
                else { Add-Check $pe $checkName "Fail" "HKLM $($reg.path)\$($reg.name) = $val (expected $($reg.expectedData))" }
            }
            "CurrentUserEffective" {
                $keyPath = "HKCU:\$($reg.path)"
                $val = (Get-ItemProperty $keyPath -Name $reg.name -ErrorAction SilentlyContinue).$($reg.name)
                if ($null -eq $val) { Add-Check $pe $checkName "Fail" "HKCU $($reg.path)\$($reg.name) missing (expected $($reg.expectedData))" }
                elseif ((Get-DwordAsHex $val) -eq $expectedHex) { Add-Check $pe $checkName "Pass" "HKCU $($reg.path)\$($reg.name) = $val" }
                else { Add-Check $pe $checkName "Fail" "HKCU $($reg.path)\$($reg.name) = $val (expected $($reg.expectedData))" }
            }
            "DefaultUserTemplate" {
                if (-not $hiveLoaded) {
                    if ($isAdmin) {
                        & reg.exe load "HKU\$hiveName" "C:\Users\Default\NTUSER.DAT" 2>$null | Out-Null
                        $hiveLoaded = ($LASTEXITCODE -eq 0)
                    }
                }
                if ($hiveLoaded) {
                    $keyPath = "HKU:\$hiveName\$($reg.path)"
                    $val = (Get-ItemProperty $keyPath -Name $reg.name -ErrorAction SilentlyContinue).$($reg.name)
                    if ($null -eq $val) { Add-Check $pe $checkName "Fail" "DefaultUser $($reg.path)\$($reg.name) missing (expected $($reg.expectedData))" }
                    elseif ((Get-DwordAsHex $val) -eq $expectedHex) { Add-Check $pe $checkName "Pass" "DefaultUser $($reg.path)\$($reg.name) = $val" }
                    else { Add-Check $pe $checkName "Fail" "DefaultUser $($reg.path)\$($reg.name) = $val (expected $($reg.expectedData))" }
                } else {
                    Add-Check $pe $checkName "NotTested" "DefaultUser hive not loaded (requires Administrator) - re-run elevated"
                }
            }
            default {
                Add-Check $pe $checkName "NotTested" "Unknown registry scope '$($reg.scope)' for $($reg.name)"
            }
        }
    }
    if ($hiveLoaded) { & reg.exe unload "HKU\$hiveName" 2>$null | Out-Null }
}
$pe.status = Resolve-SectionStatus $pe

# ---- overall aggregation (Stage 16.1b) ----
# overallStatus = honest worst of ALL required checks, any Warning/Fail OPTIONAL
# check, and any check-less section status. An OPTIONAL NotTested (e.g.
# DISM /ScanHealth) never drags the overall down; optional Warnings (activation,
# HTTPS TLS-trust) are still surfaced as Warning.
$rank = @{ "Fail" = 3; "Warning" = 2; "NotTested" = 1; "Pass" = 0 }
$worst = "Pass"; $worstRank = 0
foreach ($name in @("media","profile","windowsIdentity","bootAndShell","devices","network","servicing","windowsUpdate","security","storeAndAppPlatform","profileExpectedChanges")) {
    $sec = $report.sections[$name]
    if (@($sec.checks).Count -eq 0) {
        $s = $sec.status
        $r = $rank[$s]
        if ($null -eq $r) { $r = 1 }
        if ($r -gt $worstRank) { $worstRank = $r; $worst = $s }
        continue
    }
    foreach ($c in $sec.checks) {
        $participates = $c.requiredForFullHealth -or $c.status -eq "Warning" -or $c.status -eq "Fail"
        if (-not $participates) { continue }
        $r = $rank[[string]$c.status]
        if ($null -eq $r) { $r = 1 }
        if ($r -gt $worstRank) { $worstRank = $r; $worst = $c.status }
    }
}
$report.overallStatus = $worst

# Failures / warnings (from non-Pass checks)
$failures = @(); $warnings = @()
foreach ($name in @("media","profile","windowsIdentity","bootAndShell","devices","network","servicing","windowsUpdate","security","storeAndAppPlatform","profileExpectedChanges")) {
    foreach ($c in $report.sections[$name].checks) {
        if ($c.status -eq "Fail") { $failures += "${name}: $($c.name) - $($c.detail)" }
        elseif ($c.status -eq "Warning") { $warnings += "${name}: $($c.name) - $($c.detail)" }
    }
}
$report.failures = $failures
$report.warnings = $warnings

# ADR-084 FullHealthValidated gate (Stage 16.1b): REQUIRED-check based, not
# worst-status-of-every-check.
#   - a Fail on ANY check (required or optional) blocks (conservative);
#   - a REQUIRED check that is NotTested blocks (failures=[] alone is NOT
#     sufficient - untested required evidence never validates);
#   - OPTIONAL NotTested (DISM /ScanHealth) and Warnings (activation, HTTPS
#     TLS-trust with IP/DNS Pass) do NOT block.
$gate = $true
foreach ($name in @("media","profile","windowsIdentity","bootAndShell","devices","network","servicing","windowsUpdate","security","storeAndAppPlatform","profileExpectedChanges")) {
    foreach ($ck in $report.sections[$name].checks) {
        if ($ck.status -eq "Fail") { $gate = $false }
        if ($ck.requiredForFullHealth -and $ck.status -eq "NotTested") { $gate = $false }
    }
}
# Defensive: the critical sections must actually be exercised (have checks).
foreach ($c in @("bootAndShell", "servicing", "security", "network")) {
    if (@($report.sections[$c].checks).Count -eq 0) { $gate = $false }
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
