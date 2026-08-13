# WinForge VM Installation Validation — Manual Runbook (Phase 13)

Target: **Windows 11 25H2 · Pro · zh-CN · x64 · WIM · UEFI**
Generated ISO: `C:\Users\xulu1998\Documents\WinForge\WinForge_Windows_11_专业版_20260812-1915.iso`

> Purpose: produce the Phase 13 baseline `validation/25H2-Pro-zh-CN-x64-<ts>.json|.md` with
> `Evidence: RealVmValidation`. Do NOT mark Validated until all critical stages PASS.

## 1. VM creation (Hyper-V — platform-neutral report; VMware/VirtualBox equivalent steps OK)

Run in an **Administrator** PowerShell (Hyper-V module; WinForge is NOT coupled to a VM vendor):

```powershell
# 1) Generation-2 (UEFI) VM, 8 GB RAM, 4 vCPU
$vmName = "WF-25H2-Pro-zh-CN"
New-VM -Name $vmName -Generation 2 -MemoryStartupBytes 8GB -NewVHDPath "D:\VMs\$vmName\$vmName.vhdx" -NewVHDSizeBytes 120GB -BootDevice VHD

# 2) 4 virtual processors
Set-VM -Name $vmName -ProcessorCount 4

# 3) Attach the GENERATED ISO (not the source media)
$iso = "C:\Users\xulu1998\Documents\WinForge\WinForge_Windows_11_专业版_20260812-1915.iso"
Add-VMDvdDrive -VMName $vmName -Path $iso

# 4) Secure Boot (UEFI) + boot from DVD first
Set-VMFirmware -VMName $vmName -EnableSecureBoot On -FirstBootDevice (Get-VMDvdDrive -VMName $vmName)
Start-VM -VMName $vmName

# 5) Connect via VMConnect (graphical install)
vmconnect.exe localhost $vmName
```

VMware Workstation / VirtualBox equivalents: create a **UEFI** guest (Windows 11 x64, TPM + Secure
Boot where required), attach the generated ISO as the optical drive, boot from it.

## 2. Manual acceptance checklist (record PASS / WARNING / FAIL per row)

| # | Check | Result | Notes |
| --- | --- | --- | --- |
| 1 | VM boots from generated ISO | | |
| 2 | UEFI boot succeeds (no secure-boot error) | | |
| 3 | Windows Setup launches | | |
| 4 | Target edition is correct (Windows 11 Pro) | | |
| 5 | Disk partition / install proceeds | | |
| 6 | Image applies successfully | | |
| 7 | First reboot succeeds | | |
| 8 | OOBE launches | | |
| 9 | Account setup completes | | |
| 10 | Desktop reached | | |
| 11 | Start menu opens | | |
| 12 | Taskbar usable | | |
| 13 | Explorer launches | | |
| 14 | Settings opens | | |
| 15 | Network works | | |
| 16 | Windows Update UI opens | | |
| 17 | Windows Update service stack functional (wuauserv running, updates check) | | |
| 18 | Defender / Windows Security opens | | |
| 19 | Microsoft Store opens (if kept) | | |
| 20 | App Installer / winget works (if kept) | | |
| 21 | PowerShell / Terminal opens | | |
| 22 | Device Manager shows no setup-critical unknown device | | |
| 23 | `reagentc /info` works | | |
| 24 | `DISM /Online /Cleanup-Image /ScanHealth` completes | | |
| 25 | No setup-critical failure caused by WinForge customization | | |

Guidance: harmless informational Event Viewer entries do NOT fail the image. Separate
**critical** (blocks install/boot/login) from **advisory** findings. Record both in the report Notes.

## 3. Fill the report

After the run, update `validation/25H2-Pro-zh-CN-x64-<timestamp>.json|.md`:
- set `Evidence` to `RealVmValidation`
- mark every checklist phase PASS/FAIL in `Phases`
- fill `Notes` (VM platform, findings, advisory items)

The file is Validated only when `AllPhasesPassed` (every phase recorded + passed).
