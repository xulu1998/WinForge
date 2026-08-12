#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Stage 11.3 generator: emits the Windows Features catalog (C#), the optimization
catalog for Services/Privacy/System/Personalization (C#), the localized resx
keys (en + zh-CN), and the coverage matrix document. Single source of truth so
catalogs, localization, and the coverage report can never drift.

Run from the WinForge repo root:
    python3 .tmp/phase11/gen_stage113.py
"""
import os

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
FEAT_CS = os.path.join(REPO, "src", "WinForge.Infrastructure", "ComponentIntelligence", "WindowsFeaturesCatalog.cs")
OPT_CS = os.path.join(REPO, "src", "WinForge.Infrastructure", "Customization", "OptimizationCatalog.cs")
RESX_EN = os.path.join(REPO, "src", "WinForge.App", "Resources", "Strings.resx")
RESX_ZH = os.path.join(REPO, "src", "WinForge.App", "Resources", "Strings.zh-CN.resx")
MATRIX = os.path.join(REPO, ".tmp", "phase11", "stage11.3-coverage-matrix.md")
BLOCK_START = "<!-- WINFORGE_STAGE113_BLOCK_START -->"
BLOCK_END = "<!-- WINFORGE_STAGE113_BLOCK_END -->"

# ---------------------------------------------------------------------------
# Shared labels (key -> (en, zh))
# ---------------------------------------------------------------------------
SHARED = [
    ("Customize.Tab.Personalization", "Personalization", "个性化"),
    # Action captions (Review badges)
    ("Opt.Action.Remove", "Remove", "移除"),
    ("Opt.Action.Disable", "Disable", "禁用"),
    ("Opt.Action.Configure", "Configure", "配置"),
    ("Opt.Action.Service", "Service", "服务"),
    ("Opt.Action.Feature", "Feature", "功能"),
    ("Opt.Action.Unknown", "Change", "更改"),
    # Recommendation captions per action (action-appropriate wording, Part N)
    ("Opt.Recommendation.Remove.RecommendedRemove", "Recommended to remove", "推荐移除"),
    ("Opt.Recommendation.Remove.OptionalRemove", "Optional to remove", "按需移除"),
    ("Opt.Recommendation.Remove.UsuallyKeep", "Usually keep", "建议保留"),
    ("Opt.Recommendation.Remove.AdvancedOnly", "Advanced users only", "仅限高级用户"),
    ("Opt.Recommendation.Remove.NeverRemove", "Never remove", "不可移除"),
    ("Opt.Recommendation.Disable.RecommendedRemove", "Recommended to disable", "推荐关闭"),
    ("Opt.Recommendation.Disable.OptionalRemove", "Optional to disable", "按需关闭"),
    ("Opt.Recommendation.Disable.UsuallyKeep", "Usually keep", "建议保留"),
    ("Opt.Recommendation.Disable.AdvancedOnly", "Advanced users only", "仅限高级用户"),
    ("Opt.Recommendation.Disable.NeverRemove", "Never change", "不可修改"),
    ("Opt.Recommendation.Configure.RecommendedRemove", "Recommended to enable", "推荐开启"),
    ("Opt.Recommendation.Configure.OptionalRemove", "Optional to enable", "按需开启"),
    ("Opt.Recommendation.Configure.UsuallyKeep", "Usually keep", "建议保留"),
    ("Opt.Recommendation.Configure.AdvancedOnly", "Advanced users only", "仅限高级用户"),
    ("Opt.Recommendation.Configure.NeverRemove", "Never change", "不可修改"),
    ("Opt.Recommendation.Service.RecommendedRemove", "Recommended change", "推荐调整"),
    ("Opt.Recommendation.Service.OptionalRemove", "Optional change", "按需调整"),
    ("Opt.Recommendation.Service.UsuallyKeep", "Usually keep", "建议保留"),
    ("Opt.Recommendation.Service.AdvancedOnly", "Advanced users only", "仅限高级用户"),
    ("Opt.Recommendation.Service.NeverRemove", "Never change", "不可修改"),
    ("Opt.Recommendation.Feature.RecommendedRemove", "Recommended to disable", "推荐禁用"),
    ("Opt.Recommendation.Feature.OptionalRemove", "Optional to disable", "按需禁用"),
    ("Opt.Recommendation.Feature.UsuallyKeep", "Usually keep", "建议保留"),
    ("Opt.Recommendation.Feature.AdvancedOnly", "Advanced users only", "仅限高级用户"),
    ("Opt.Recommendation.Feature.NeverRemove", "Never change", "不可修改"),
    ("Opt.Recommendation.Unknown.RecommendedRemove", "Recommended", "推荐"),
    ("Opt.Recommendation.Unknown.OptionalRemove", "Optional", "按需"),
    ("Opt.Recommendation.Unknown.UsuallyKeep", "Usually keep", "建议保留"),
    ("Opt.Recommendation.Unknown.AdvancedOnly", "Advanced users only", "仅限高级用户"),
    ("Opt.Recommendation.Unknown.NeverRemove", "Never change", "不可修改"),
    # Scope captions (Part J)
    ("Opt.Scope.OfflineMachine", "Machine-wide (offline image)", "整机范围（离线镜像）"),
    ("Opt.Scope.OfflineDefaultUser", "New users (Default User profile)", "新用户（默认用户配置文件）"),
    ("Opt.Scope.OfflineAllUsers", "All users in the offline image", "离线镜像全部用户"),
    ("Opt.Scope.ProvisionedApp", "Provisioned app in the offline image", "离线镜像预置应用"),
    ("Opt.Scope.MountedImageFeature", "Offline image feature (DISM)", "离线镜像功能（DISM）"),
    ("Opt.Scope.PostInstallRequired", "After first logon only — not applied to the image", "仅首次登录后生效——不写入镜像"),
    ("Opt.Scope.UnsupportedOffline", "Not supported on an offline image", "离线镜像不支持"),
    ("Opt.Scope.Unknown", "Unknown", "未知"),
    # Review plan labels
    ("Plan.Ops", "Selected changes", "已选更改"),
    ("Plan.Empty", "No changes selected yet.", "尚未选择任何更改。"),
    ("Plan.Action", "Action", "操作"),
    ("Plan.Target", "Change", "更改"),
    ("Plan.Scope", "Applies to", "作用域"),
    ("Plan.Reversal", "How to revert", "如何还原"),
    ("Plan.Reversal.Generic", "Restore to the Windows default recorded by WinForge", "恢复为 WinForge 记录的 Windows 默认值"),
    # Detail panel labels for optimization rows
    ("Opt.Detail.Scope", "Applies to", "作用域"),
    ("Opt.Detail.Reversal", "How to revert", "如何还原"),
    ("Opt.Detail.Target", "Technical target", "技术目标"),
    ("Opt.Detail.ProposedStart", "Proposed startup type", "建议启动类型"),
    ("Opt.Empty", "No reviewed controls in this category yet.", "该类别暂无已审核的控制项。"),
    # Block reasons
    ("Opt.Blocked", "Cannot be changed", "不可更改"),
    ("Opt.NotApplicable", "Not applicable to the selected image", "不适用于当前所选映像"),
    ("Opt.NoChangeRecommended", "No change recommended — Windows default kept", "不建议更改——保持 Windows 默认"),
    ("Opt.CoreNeverChange", "Core infrastructure — never modified by WinForge", "核心基础组件——WinForge 绝不修改"),
    ("Opt.ApplyUnsupported", "Apply is not supported for this item in this version", "当前版本暂不支持应用"),
    # Service start type captions
    ("Opt.StartType.Automatic", "Automatic", "自动"),
    ("Opt.StartType.Manual", "Manual", "手动"),
    ("Opt.StartType.Disabled", "Disabled", "禁用"),
    ("Opt.StartType.Boot", "Boot", "启动"),
    ("Opt.StartType.System", "System", "系统"),
    # Scope/mechanism notes shown in coverage matrix + detail
    ("Opt.Mechanism.ServiceStartup", "Service startup type", "服务启动类型"),
    ("Opt.Mechanism.RegistryPolicy", "Registry policy (machine)", "注册表策略（整机）"),
    ("Opt.Mechanism.ExplorerPreference", "Explorer preference", "资源管理器偏好"),
    ("Opt.Mechanism.StartPreference", "Start preference", "开始菜单偏好"),
    ("Opt.Mechanism.TaskbarPreference", "Taskbar preference", "任务栏偏好"),
    ("Opt.Mechanism.VisualPreference", "Visual preference", "外观偏好"),
    ("Opt.Mechanism.PrivacyPolicy", "Privacy policy", "隐私策略"),
    ("Opt.Mechanism.SystemPolicy", "System policy", "系统策略"),
    ("Opt.Mechanism.DisableOptionalFeature", "Optional feature (DISM)", "可选功能（DISM）"),
    ("Opt.Mechanism.RemoveCapability", "Capability (DISM)", "功能能力（DISM）"),
]

# ---------------------------------------------------------------------------
# Windows Features catalog (Windows Components tab). ComponentDefinition entries.
# ---------------------------------------------------------------------------
FEATURES = [
    dict(id="HyperV", name=("Hyper-V", "Hyper-V"), short=("Virtual machine platform for running Windows/Linux VMs", "运行 Windows/Linux 虚拟机的虚拟机平台"), rec="AdvancedOnly", risk="Medium", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("Microsoft-Hyper-V", "OptionalFeature"), ("Microsoft-Hyper-V-Management-PowerShell", "OptionalFeature")], deps=[("virtual-machine-platform", "RelatedTo", "hyperv-dep"), ("hypervisor-platform", "RelatedTo", "hyperv-dep")], keep=("hyperv-keep",), scen=("HyperV",), tags=("hyperv",)),
    dict(id="WindowsSandbox", name=("Windows Sandbox", "Windows 沙盒"), short=("Disposable isolated desktop for testing untrusted software", "用于测试不受信任软件的临时隔离桌面"), rec="AdvancedOnly", risk="Medium", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("Containers-DisposableClientVM", "OptionalFeature")], deps=[("virtual-machine-platform", "RelatedTo", "sandbox-dep")], keep=("sandbox-keep",), scen=("WindowsSandbox", "Developer"), tags=("sandbox",)),
    dict(id="Wsl", name=("Linux / WSL support", "Linux / WSL 支持"), short=("Run a Linux environment inside Windows (Windows Subsystem for Linux)", "在 Windows 中运行 Linux 环境（适用于 Linux 的 Windows 子系统）"), rec="AdvancedOnly", risk="Medium", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("Microsoft-Windows-Subsystem-Linux", "OptionalFeature")], deps=[("virtual-machine-platform", "Requires", "wsl-dep")], keep=("wsl-keep",), scen=("Wsl", "Docker", "Developer"), tags=("wsl", "linux")),
    dict(id="VirtualMachinePlatform", name=("Virtual Machine Platform", "虚拟机平台"), short=("Shared virtualization platform used by WSL2, Sandbox and Android apps", "WSL2、沙盒与 Android 应用共用的虚拟化平台"), rec="AdvancedOnly", risk="High", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("VirtualMachinePlatform", "OptionalFeature")], deps=[], keep=("vmp-keep",), scen=("Wsl", "WindowsSandbox", "Docker"), tags=("virtualization",)),
    dict(id="OpenSshClient", name=("OpenSSH Client", "OpenSSH 客户端"), short=("Connect to remote servers over SSH from this PC", "在本机通过 SSH 连接远程服务器"), rec="OptionalRemove", risk="Low", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("OpenSSH.Client", "OptionalFeature")], deps=[], keep=("sshclient-keep",), scen=("Developer", "RemoteDesktop"), tags=("ssh", "remote")),
    dict(id="OpenSshServer", name=("OpenSSH Server", "OpenSSH 服务器"), short=("Accept incoming SSH connections to this PC (opens a network port)", "接受指向本机的 SSH 连接（会开放网络端口）"), rec="AdvancedOnly", risk="Medium", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("OpenSSH.Server", "OptionalFeature")], deps=[], keep=("sshserver-keep",), scen=("Developer", "EnterpriseDomain"), tags=("ssh", "remote")),
    dict(id="MediaPlayer", name=("Windows Media Player (legacy)", "Windows Media Player（旧版）"), short=("Legacy media player for local audio/video files", "播放本地音视频文件的旧版媒体播放器"), rec="OptionalRemove", risk="Low", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("WindowsMediaPlayer", "OptionalFeature")], deps=[], keep=("wmp-keep",), scen=(), tags=("media",)),
    dict(id="InternetPrinting", name=("Internet Printing Client", "Internet 打印客户端"), short=("Print over the network using Internet Printing Protocol (IPP)", "通过 Internet 打印协议 (IPP) 进行网络打印"), rec="OptionalRemove", risk="Low", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("Internet-Printing-Client", "OptionalFeature")], deps=[("PrintingScanning", "RelatedTo", "print-dep")], keep=("ipp-keep",), scen=("PrintingScanning",), tags=("printing",)),
    dict(id="ScanManagement", name=("Scan Management Console", "扫描管理控制台"), short=("Manage scanners and scanning from Windows", "在 Windows 中管理扫描仪与扫描"), rec="OptionalRemove", risk="Low", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("ScanManagementConsole", "OptionalFeature")], deps=[("PrintingScanning", "RelatedTo", "scan-dep")], keep=("scan-keep",), scen=("PrintingScanning",), tags=("scanning",)),
    dict(id="XpsServices", name=("XPS Services", "XPS 服务"), short=("Create and print XPS documents", "创建与打印 XPS 文档"), rec="OptionalRemove", risk="Low", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("Printing-XPSServices-Features", "OptionalFeature")], deps=[("PrintingScanning", "RelatedTo", "xps-dep")], keep=("xps-keep",), scen=("PrintingScanning",), tags=("printing", "xps")),
    dict(id="PowerShell2", name=("PowerShell 2.0 Engine (legacy)", "PowerShell 2.0 引擎（旧版）"), short=("Deprecated engine for very old scripts; current PowerShell does not need it", "供极老脚本使用的已弃用引擎；新版 PowerShell 不需要它"), rec="OptionalRemove", risk="Medium", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("MicrosoftWindowsPowerShellV2Root", "OptionalFeature")], deps=[], keep=("ps2-keep",), scen=("Developer",), tags=("powershell", "legacy")),
    dict(id="HypervisorPlatform", name=("Windows Hypervisor Platform", "Windows 虚拟机监控程序平台"), short=("Lets third-party VMs use the Windows hypervisor", "让第三方虚拟机使用 Windows 虚拟机监控程序"), rec="AdvancedOnly", risk="Medium", action="Feature", mechanism="DisableOptionalFeature", scope="MountedImageFeature", restore="Easy", targets=[("HypervisorPlatform", "OptionalFeature")], deps=[("HyperV", "RelatedTo", "hvplatform-dep"), ("Wsl", "RelatedTo", "hvplatform-dep")], keep=("hvplatform-keep",), scen=("HyperV", "Docker"), tags=("virtualization",)),
]

# ---------------------------------------------------------------------------
# Services catalog (Services tab). OptimizationDefinition entries.
# ProposedStartType: "Automatic"/"Manual"/"Disabled" or None (LeaveDefault/informational).
# ---------------------------------------------------------------------------
SERVICES = [
    dict(id="DiagTrack", name=("Connected User Experiences and Telemetry", "连接的用户体验和遥测"), short=("Microsoft telemetry service; commonly disabled on privacy-focused images", "Microsoft 遥测服务；注重隐私的镜像通常会禁用它"), rec="RecommendedRemove", risk="Medium", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="DiagTrack", start="Disabled", prov=("MicrosoftOfficial",), tags=("telemetry",)),
    dict(id="WerSvc", name=("Windows Error Reporting Service", "Windows 错误报告服务"), short=("Reports crashes to Microsoft; keeping it off reduces background reporting", "向 Microsoft 报告崩溃；关闭可减少后台上报"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="WerSvc", start="Disabled", prov=("MicrosoftOfficial",), tags=("diagnostics",)),
    dict(id="PcaSvc", name=("Program Compatibility Assistant Service", "程序兼容性助手服务"), short=("Detects and prompts about compatibility issues; can cause prompts on old installers", "检测并提示兼容性问题；旧安装程序可能引发提示"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="PcaSvc", start="Disabled", prov=("MicrosoftOfficial",), tags=("compatibility",)),
    dict(id="XboxGipSvc", name=("Xbox Accessory Management Service", "Xbox 配件管理服务"), short=("Manages Xbox accessories (controllers, headsets); only needed for Xbox accessories", "管理 Xbox 配件（手柄、耳机）；仅在使用 Xbox 配件时需要"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="XboxGipSvc", start="Manual", prov=("CommunityReference",), scen=("Gaming", "XboxGamePass"), tags=("xbox", "gaming")),
    dict(id="XboxNetApiSvc", name=("Xbox Live Networking Service", "Xbox Live 网络服务"), short=("Networking for Xbox Live on PC; only needed for Xbox Live gaming", "PC 上的 Xbox Live 网络支持；仅在玩 Xbox Live 游戏时需要"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="XboxNetApiSvc", start="Manual", prov=("CommunityReference",), scen=("Gaming", "XboxGamePass"), tags=("xbox", "gaming")),
    dict(id="XblAuthManager", name=("Xbox Live Auth Manager", "Xbox Live 身份验证管理器"), short=("Sign-in for Xbox Live titles; only needed for Xbox Live gaming", "Xbox Live 游戏的登录支持；仅在玩 Xbox Live 游戏时需要"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="XblAuthManager", start="Manual", prov=("CommunityReference",), scen=("Gaming", "XboxGamePass"), tags=("xbox", "gaming")),
    dict(id="RetailDemo", name=("Retail Demo Service", "零售演示服务"), short=("Runs retail/kiosk demo mode; never used on normal PCs", "运行零售/展台演示模式；普通电脑用不到"), rec="RecommendedRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="RetailDemo", start="Disabled", prov=("MicrosoftOfficial",), tags=("demo",)),
    dict(id="MapsBroker", name=("Downloaded Maps Manager", "已下载地图管理器"), short=("Manages offline map downloads for the Maps app; only needed if you use offline maps", "管理“地图”应用的离线地图下载；仅使用离线地图时需要"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="MapsBroker", start="Manual", prov=("CommunityReference",), scen=("Laptop",), tags=("maps", "location")),
    dict(id="WMPNetworkSvc", name=("Windows Media Player Network Sharing", "Windows Media Player 网络共享"), short=("Shares media libraries over the network; only needed for WMP sharing", "通过网络共享媒体库；仅需 WMP 共享时需要"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="WMPNetworkSvc", start="Manual", prov=("CommunityReference",), tags=("media",)),
    dict(id="TabletInputService", name=("Touch Keyboard and Handwriting Panel", "触摸键盘和手写面板服务"), short=("Touch keyboard and handwriting input; desktop users without touch can set it to manual", "触摸键盘与手写输入；无触摸屏的台式机可设为手动"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="TabletInputService", start="Manual", prov=("CommunityReference",), scen=("TouchPen",), tags=("touch", "input")),
    dict(id="Lfsvc", name=("Geolocation Service", "地理位置服务"), short=("Monitors the current location for apps; only needed when apps use location", "为应用提供当前位置；仅应用使用定位时需要"), rec="OptionalRemove", risk="Low", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="lfsvc", start="Manual", prov=("CommunityReference",), scen=("Laptop",), tags=("location",)),
    # Informational LeaveDefault row: proves core services are never offered.
    dict(id="RpcSs", name=("Remote Procedure Call (RPC)", "远程过程调用 (RPC)"), short=("Core Windows IPC infrastructure — almost every component depends on it", "Windows 核心进程间通信基础——几乎所有组件都依赖它"), rec="NeverRemove", risk="Critical", action="Service", mechanism="ServiceStartup", scope="OfflineMachine", restore="Easy", svc="RpcSs", start=None, prov=("MicrosoftOfficial",), tags=("core",)),
]

# ---------------------------------------------------------------------------
# Privacy catalog (Privacy tab). RegistryPolicy machine settings.
# ---------------------------------------------------------------------------
def reg(hive, key, value, data, restore):
    return dict(Hive=hive, KeyPath=key, ValueName=value, Kind="DWord", RecommendedData=data, RestoreData=restore)

PRIVACY = [
    dict(id="AdvertisingId", name=("Advertising ID", "广告 ID"), short=("Disable the per-device advertising ID used for targeted ads", "关闭用于定向广告的设备广告 ID"), rec="RecommendedRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Microsoft\Windows\CurrentVersion\AdvertisingInfo", "Enabled", "0", "1")], prov=("MicrosoftOfficial",), tags=("ads",)),
    dict(id="TailoredExperiences", name=("Tailored experiences", "定制体验"), short=("Stop Windows using diagnostics data to personalize tips and recommendations", "阻止 Windows 利用诊断数据个性化提示与建议"), rec="RecommendedRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\CloudContent", "DisableWindowsConsumerFeatures", "1", "0")], prov=("MicrosoftOfficial",), tags=("telemetry", "content")),
    dict(id="ActivityHistory", name=("Activity history", "活动历史记录"), short=("Stop collecting and uploading local activity history to the cloud", "停止收集并上传本地活动历史记录到云端"), rec="RecommendedRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\System", "EnableActivityHistory", "0", "1")], prov=("MicrosoftOfficial",), tags=("history",)),
    dict(id="AppLaunchTracking", name=("App launch tracking", "应用启动跟踪"), short=("Stop storing which apps are launched (Start menu telemetry)", "停止记录启动了哪些应用（开始菜单遥测）"), rec="RecommendedRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\AppCompat", "AllowTelemetry", "0", "1")], prov=("MicrosoftOfficial",), tags=("telemetry",)),
    dict(id="WebSearchStart", name=("Web results in Start search", "开始菜单搜索中的网页结果"), short=("Keep Start search local instead of querying Bing/web results", "让开始菜单搜索保持本地，不查询 Bing/网页结果"), rec="RecommendedRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\Explorer", "DisableSearchBoxSuggestions", "1", "0")], prov=("MicrosoftOfficial",), tags=("search",)),
    dict(id="InputPersonalization", name=("Input personalization", "输入个性化"), short=("Disable cloud-backed typing insights (input personalization)", "关闭基于云的键入见解（输入个性化）"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\InputPersonalization", "AllowInputPersonalization", "0", "1")], prov=("MicrosoftOfficial",), tags=("input",)),
    dict(id="SpeechModelUpdates", name=("Speech model updates", "语音模型更新"), short=("Stop downloading online speech recognition models", "停止下载联机语音识别模型"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\InputPersonalization", "AllowSpeechModelUpdate", "0", "1")], prov=("MicrosoftOfficial",), tags=("speech",)),
    dict(id="Location", name=("Location services", "定位服务"), short=("Disable the location platform used by apps (maps, weather, …)", "关闭应用使用的定位平台（地图、天气等）"), rec="OptionalRemove", risk="Medium", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\LocationAndSensors", "DisableLocation", "1", "0")], prov=("MicrosoftOfficial",), scen=("Laptop",), tags=("location",)),
    dict(id="FindMyDevice", name=("Find My Device", "查找我的设备"), short=("Disable Find My Device (device location reporting to the account)", "关闭“查找我的设备”（向账户报告设备位置）"), rec="OptionalRemove", risk="Medium", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\FindMyDevice", "AllowFindMyDevice", "0", "1")], prov=("MicrosoftOfficial",), scen=("Laptop",), tags=("location",)),
    dict(id="FeedbackNotifications", name=("Feedback prompts", "反馈提示"), short=("Suppress Windows feedback notification prompts", "抑制 Windows 反馈通知提示"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\DataCollection", "DoNotShowFeedbackNotifications", "1", "0")], prov=("MicrosoftOfficial",), tags=("feedback",)),
    dict(id="SpotlightFeatures", name=("Windows Spotlight content", "Windows 聚焦内容"), short=("Turn off all Windows Spotlight features (lock screen / desktop content)", "关闭全部 Windows 聚焦功能（锁屏/桌面内容）"), rec="RecommendedRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightFeatures", "1", "0")], prov=("MicrosoftOfficial",), tags=("spotlight",)),
]

# ---------------------------------------------------------------------------
# System catalog (System tab). SystemPolicy machine settings.
# ---------------------------------------------------------------------------
SYSTEM = [
    dict(id="GameDvr", name=("Game DVR background recording", "游戏 DVR 后台录制"), short=("Prevent the Game DVR service from recording in the background", "阻止游戏 DVR 服务在后台录制"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\GameDVR", "AllowGameDVR", "0", "1")], prov=("MicrosoftOfficial",), scen=("Gaming",), tags=("gaming",)),
    dict(id="Cortana", name=("Consumer Cortana", "消费者版 Cortana"), short=("Turn off the consumer Cortana integration", "关闭消费者版 Cortana 集成"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\Windows Search", "AllowCortana", "0", "1")], prov=("MicrosoftOfficial",), tags=("cortana",)),
    dict(id="Tips", name=("Windows tips / suggestions", "Windows 提示/建议"), short=("Suppress cloud-backed tips and suggestions", "抑制云端的提示与建议"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\CloudContent", "DisableSoftLanding", "1", "0")], prov=("MicrosoftOfficial",), tags=("tips",)),
    dict(id="DeliveryOptimization", name=("Delivery Optimization downloads", "传递优化下载"), short=("Stop Windows Update downloading from other PCs on the network", "阻止 Windows 更新从网络中其他电脑下载"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Microsoft\Windows\CurrentVersion\DeliveryOptimization\Config", "DODownloadMode", "0", "1")], prov=("MicrosoftOfficial",), tags=("update",)),
    dict(id="DeviceMetadata", name=("Device metadata downloads", "设备元数据下载"), short=("Prevent automatic device metadata downloads from the network", "阻止从网络自动下载设备元数据"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\Device Metadata", "PreventDeviceMetadataFromNetwork", "1", "0")], prov=("MicrosoftOfficial",), tags=("devices",)),
    dict(id="RemoteAssistance", name=("Remote Assistance invitations", "远程协助邀请"), short=("Disable Remote Assistance invitations (Remote Desktop stays available)", "关闭远程协助邀请（远程桌面仍可用）"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows NT\Terminal Services", "fAllowToGetHelp", "0", "1")], prov=("MicrosoftOfficial",), scen=("EnterpriseDomain",), tags=("remote",)),
    dict(id="Hibernation", name=("Hibernation", "休眠"), short=("Disable hibernation (frees hiberfil.sys; prevents fast startup)", "关闭休眠（释放 hiberfil.sys；同时禁用快速启动）"), rec="AdvancedOnly", risk="Medium", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SYSTEM", r"CurrentControlSet\Control\Power", "HibernateEnabled", "0", "1")], prov=("MicrosoftOfficial",), scen=("Laptop",), tags=("power",)),
    dict(id="WindowsAi", name=("AI features data analysis (Recall / Click To Do)", "AI 功能数据分析（Recall / Click To Do）"), short=("Disable AI-driven on-device data analysis (Windows AI; Windows 11 24H2+)", "关闭设备端 AI 数据分析（Windows AI；Windows 11 24H2 及以上）"), rec="OptionalRemove", risk="Medium", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\WindowsAI", "DisableAIDataAnalysis", "1", "0")], prov=("MicrosoftOfficial",), compat={"min": "26100"}, tags=("ai", "recall")),
    dict(id="OneDriveSync", name=("OneDrive file sync", "OneDrive 文件同步"), short=("Disable OneDrive file sync (app stays; sync off)", "关闭 OneDrive 文件同步（应用保留，同步关闭）"), rec="OptionalRemove", risk="Medium", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\OneDrive", "DisableFileSyncNGSC", "1", "0")], prov=("MicrosoftOfficial",), scen=("Office",), tags=("onedrive",)),
    dict(id="PrintDriverDownload", name=("Web print driver downloads", "网页打印驱动程序下载"), short=("Do not automatically download print drivers from the web", "不从网页自动下载打印驱动程序"), rec="OptionalRemove", risk="Low", action="Disable", mechanism="SystemPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows NT\Printers", "DisableWebPnPDownload", "1", "0")], prov=("MicrosoftOfficial",), scen=("PrintingScanning",), tags=("printing",)),
]

# ---------------------------------------------------------------------------
# Personalization catalog (Personalization tab). Mix of DEFAULT_USER + policies.
# ---------------------------------------------------------------------------
DU = "DEFAULT_USER"
PERSONALIZATION = [
    dict(id="ShowFileExtensions", name=("Show file extensions", "显示文件扩展名"), short=("Show file name extensions in File Explorer", "在文件资源管理器中显示文件扩展名"), rec="RecommendedRemove", risk="Low", action="Configure", mechanism="ExplorerPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "HideFileExt", "0", "1")], prov=("WinForgeCurated", "CommunityReference"), tags=("explorer",)),
    dict(id="ShowHiddenFiles", name=("Show hidden files", "显示隐藏文件"), short=("Show hidden files and folders in File Explorer", "在文件资源管理器中显示隐藏文件和文件夹"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="ExplorerPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Hidden", "1", "2")], prov=("WinForgeCurated", "CommunityReference"), tags=("explorer",)),
    dict(id="OpenToThisPC", name=("Open File Explorer to This PC", "文件资源管理器打开到“此电脑”"), short=("Open File Explorer to This PC instead of Home", "打开文件资源管理器时显示“此电脑”而非“主页”"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="ExplorerPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "LaunchTo", "1", "0")], prov=("WinForgeCurated", "CommunityReference"), tags=("explorer",)),
    dict(id="HideRecentQuickAccess", name=("Hide recent files in Quick access", "隐藏快速访问中的最近文件"), short=("Stop showing recently opened files in Quick access", "不在快速访问中显示最近打开的文件"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="ExplorerPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackDocs", "0", "1")], prov=("WinForgeCurated", "CommunityReference"), tags=("explorer",)),
    dict(id="HideFrequentQuickAccess", name=("Hide frequent folders in Quick access", "隐藏快速访问中的常用文件夹"), short=("Stop showing frequently used folders in Quick access", "不在快速访问中显示常用文件夹"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="ExplorerPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_TrackFrec", "0", "1")], prov=("CommunityReference",), tags=("explorer",)),
    dict(id="HideStartRecommended", name=("Hide Recommended in Start", "隐藏开始菜单中的“推荐”"), short=("Remove the Recommended section from the Start menu", "从开始菜单移除“推荐”区域"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="StartPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_ShowRecommended", "0", "1")], prov=("CommunityReference",), tags=("start",)),
    dict(id="HideStartRecentlyAdded", name=("Hide recently added apps in Start", "隐藏开始菜单中的“最近添加”"), short=("Stop showing recently added apps in the Start menu", "不在开始菜单显示最近添加的应用"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="StartPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "Start_ShowRecent", "0", "1")], prov=("CommunityReference",), tags=("start",)),
    dict(id="HideTaskbarWidgets", name=("Hide Widgets button", "隐藏小组件按钮"), short=("Remove the Widgets button from the taskbar", "从任务栏移除“小组件”按钮"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="TaskbarPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarDa", "0", "1")], prov=("CommunityReference",), tags=("taskbar",)),
    dict(id="TaskbarSearchIcon", name=("Taskbar search as icon", "任务栏搜索仅显示图标"), short=("Show the taskbar search as an icon instead of a box", "任务栏搜索以图标而非搜索框显示"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="TaskbarPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarSearch", "1", "2")], prov=("CommunityReference",), tags=("taskbar", "search")),
    dict(id="HideTaskbarTaskView", name=("Hide Task View button", "隐藏任务视图按钮"), short=("Remove the Task View button from the taskbar", "从任务栏移除“任务视图”按钮"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="TaskbarPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced", "TaskbarMn", "0", "1")], prov=("CommunityReference",), tags=("taskbar",)),
    dict(id="DisableSpotlight", name=("Windows Spotlight (lock screen content)", "Windows 聚焦（锁屏内容）"), short=("Disable Spotlight lock-screen content (uses a plain image)", "关闭锁屏的聚焦内容（使用普通图片）"), rec="RecommendedRemove", risk="Low", action="Disable", mechanism="PrivacyPolicy", scope="OfflineMachine", restore="Easy", targets=[reg("SOFTWARE", r"Policies\Microsoft\Windows\CloudContent", "DisableWindowsSpotlightFeatures", "1", "0")], prov=("MicrosoftOfficial",), tags=("lock", "spotlight")),
    dict(id="DarkMode", name=("Dark mode", "深色模式"), short=("Use the dark theme for apps and system", "应用与系统使用深色主题"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="VisualPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", "0", "1"), reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", "0", "1")], prov=("CommunityReference",), tags=("appearance",)),
    dict(id="DisableTransparency", name=("Disable transparency effects", "关闭透明效果"), short=("Turn off window transparency / blur effects", "关闭窗口透明/模糊效果"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="VisualPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "EnableTransparency", "0", "1")], prov=("CommunityReference",), tags=("appearance",)),
    dict(id="DisableAnimations", name=("Disable animation effects", "关闭动画效果"), short=("Turn off window and menu animation effects", "关闭窗口与菜单动画效果"), rec="OptionalRemove", risk="Low", action="Configure", mechanism="VisualPreference", scope="OfflineDefaultUser", restore="Easy", targets=[reg(DU, r"Software\Microsoft\Windows\CurrentVersion\Explorer\VisualEffects", "VisualFXSetting", "2", "3")], prov=("CommunityReference",), tags=("appearance",)),
]

# Deferred / rejected / unsupported candidates (coverage report Part V).
DEFERRED = [
    ("Apps", "Feedback Hub", "AppX catalog entry pending review (not in first tranche).", "catalog-entry-pending"),
    ("Apps", "Phone Link", "AppX catalog entry pending review (not in first tranche).", "catalog-entry-pending"),
    ("Apps", "Maps", "AppX catalog entry pending review (not in first tranche).", "catalog-entry-pending"),
    ("Apps", "Solitaire Collection", "AppX catalog entry pending review (not in first tranche).", "catalog-entry-pending"),
    ("Windows Components", "Handwriting recognition", "Language-linked optional components need strong safeguards; deferred to a later tranche.", "language-safeguards"),
    ("Windows Components", "Speech recognition", "Language-linked optional components need strong safeguards; deferred to a later tranche.", "language-safeguards"),
    ("Windows Components", "OCR capabilities", "Language-linked optional components need strong safeguards; deferred to a later tranche.", "language-safeguards"),
    ("Windows Components", "Windows Media Format (legacy media)", "Optional feature target not yet validated against the real image.", "unvalidated-target"),
    ("Services", "Smart card services", "Security-adjacent; needs a dedicated review before any proposed change.", "security-review"),
    ("Services", "Biometric services", "Security-adjacent (Windows Hello); needs a dedicated review.", "security-review"),
    ("Services", "Remote Registry", "Security-adjacent; needs a dedicated review.", "security-review"),
    ("Privacy", "Cloud search history", "Only community evidence exists; not promoted (Part P).", "community-only"),
    ("Privacy", "Settings promotional content", "Only community evidence exists; not promoted (Part P).", "community-only"),
    ("System", "Reserved storage", "Mechanism/risk not yet well understood for offline images.", "mechanism-risk"),
    ("System", "Widgets machine policy", "No documented machine policy; user-scope only (deferred).", "user-scope"),
    ("System", "Game Bar toggle", "User-scope setting (HKCU); only Game DVR is a machine policy.", "user-scope"),
    ("Personalization", "Phone Link in Start", "No documented stable value; user-scope.", "user-scope"),
    ("Personalization", "Desktop Spotlight", "Windows 11 24H2 user-scope setting; not yet validated offline.", "user-scope"),
    ("Personalization", "Start layout pinning", "No documented offline mechanism; deferred.", "undocumented"),
]
REJECTED = [
    ("System", "Timer-resolution tweaks", "Undocumented scheduler folklore; no real user benefit.", "folklore"),
    ("System", "BCD tweaks", "Dangerous boot-configuration changes; not offered.", "dangerous"),
    ("System", "Memory-management myths", "No evidence of benefit; rejected.", "folklore"),
    ("System", "Disabling Microsoft Defender", "Security regression presented as optimization; rejected.", "security"),
    ("System", "Placebo performance registry tweaks", "No evidence; rejected.", "folklore"),
]
UNSUPPORTED = [
    ("Privacy", "Sign out everywhere / per-account cloud state", "Requires online account state; not applicable to an offline image.", "online-state"),
    ("Any", "Per-user (HKCU) changes for EXISTING users of an already-deployed image", "Only Default User (new users) can be targeted offline; existing-user scope is not offered.", "existing-user"),
    ("Windows Components", "DisableOfflineScheduledTask", "Robust offline scheduled-task support is not implemented; not offered.", "no-implementation"),
]

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def esc(s):
    return s.replace("\\", "\\\\").replace('"', '\\"')

def pkey(tab, ident, suffix):
    return "Opt.{}.{}.{}".format(tab, ident, suffix)

def entry_keys(tab, ident, entry):
    keys = [(pkey(tab, ident, "DisplayName"), entry["name"][0], entry["name"][1]),
            (pkey(tab, ident, "Short"), entry["short"], entry.get("short_zh") or entry["short"])]
    return keys

ALL_KEYS = {}
def add(key, en, zh):
    ALL_KEYS[key] = (en, zh)

# zh-CN translations for the SHORT captions of the non-AppX entries (FEATURES use
# bilingual tuples; these lists use English shorts + this map).
SHORT_ZH = {
    # Services
    "DiagTrack": "Microsoft 遥测服务；注重隐私的镜像通常会禁用它",
    "WerSvc": "向 Microsoft 报告崩溃；关闭可减少后台上报",
    "PcaSvc": "检测并提示兼容性问题；旧安装程序可能引发提示",
    "XboxGipSvc": "管理 Xbox 配件（手柄、耳机）；仅在使用 Xbox 配件时需要",
    "XboxNetApiSvc": "PC 上的 Xbox Live 网络支持；仅在玩 Xbox Live 游戏时需要",
    "XblAuthManager": "Xbox Live 游戏的登录支持；仅在玩 Xbox Live 游戏时需要",
    "RetailDemo": "运行零售/展台演示模式；普通电脑用不到",
    "MapsBroker": "管理“地图”应用的离线地图下载；仅使用离线地图时需要",
    "WMPNetworkSvc": "通过网络共享媒体库；仅需 WMP 共享时需要",
    "TabletInputService": "触摸键盘与手写输入；无触摸屏的台式机可设为手动",
    "Lfsvc": "为应用提供当前位置；仅应用使用定位时需要",
    "RpcSs": "Windows 核心进程间通信基础——几乎所有组件都依赖它",
    # Privacy
    "AdvertisingId": "关闭用于定向广告的设备广告 ID",
    "TailoredExperiences": "阻止 Windows 利用诊断数据个性化提示与建议",
    "ActivityHistory": "停止收集并上传本地活动历史记录到云端",
    "AppLaunchTracking": "停止记录启动了哪些应用（开始菜单遥测）",
    "WebSearchStart": "让开始菜单搜索保持本地，不查询 Bing/网页结果",
    "InputPersonalization": "关闭基于云的键入见解（输入个性化）",
    "SpeechModelUpdates": "停止下载联机语音识别模型",
    "Location": "关闭应用使用的定位平台（地图、天气等）",
    "FindMyDevice": "关闭“查找我的设备”（向账户报告设备位置）",
    "FeedbackNotifications": "抑制 Windows 反馈通知提示",
    "SpotlightFeatures": "关闭全部 Windows 聚焦功能（锁屏/桌面内容）",
    # System
    "GameDvr": "阻止游戏 DVR 服务在后台录制",
    "Cortana": "关闭消费者版 Cortana 集成",
    "Tips": "抑制云端的提示与建议",
    "DeliveryOptimization": "阻止 Windows 更新从网络中其他电脑下载",
    "DeviceMetadata": "阻止从网络自动下载设备元数据",
    "RemoteAssistance": "关闭远程协助邀请（远程桌面仍可用）",
    "Hibernation": "关闭休眠（释放 hiberfil.sys；同时禁用快速启动）",
    "WindowsAi": "关闭设备端 AI 数据分析（Windows AI；Windows 11 24H2 及以上）",
    "OneDriveSync": "关闭 OneDrive 文件同步（应用保留，同步关闭）",
    "PrintDriverDownload": "不从网页自动下载打印驱动程序",
    # Personalization
    "ShowFileExtensions": "在文件资源管理器中显示文件扩展名",
    "ShowHiddenFiles": "在文件资源管理器中显示隐藏文件和文件夹",
    "OpenToThisPC": "打开文件资源管理器时显示“此电脑”而非“主页”",
    "HideRecentQuickAccess": "不在快速访问中显示最近打开的文件",
    "HideFrequentQuickAccess": "不在快速访问中显示常用文件夹",
    "HideStartRecommended": "从开始菜单移除“推荐”区域",
    "HideStartRecentlyAdded": "不在开始菜单显示最近添加的应用",
    "HideTaskbarWidgets": "从任务栏移除“小组件”按钮",
    "TaskbarSearchIcon": "任务栏搜索以图标而非搜索框显示",
    "HideTaskbarTaskView": "从任务栏移除“任务视图”按钮",
    "DisableSpotlight": "关闭锁屏的聚焦内容（使用普通图片）",
    "DarkMode": "应用与系统使用深色主题",
    "DisableTransparency": "关闭窗口透明/模糊效果",
    "DisableAnimations": "关闭窗口与菜单动画效果",
}

def pair(x):
    """FEATURES shorts are (en, zh) tuples; the other lists use English strings."""
    return x if isinstance(x, tuple) else (x, SHORT_ZH.get(x, x))

def build_keys():
    for k, en, zh in SHARED:
        add(k, en, zh)
    for f in FEATURES:
        fid = f["id"]
        en, zh = pair(f["short"])
        add("Feat.{}.DisplayName".format(fid), f["name"][0], f["name"][1])
        add("Feat.{}.Short".format(fid), en, zh)
        if f.get("keep"):
            add("Feat.{}.KeepIf".format(fid), "You use {}".format(f["name"][0]), "你使用{}".format(f["name"][0]))
    for tab, lst in (("Services", SERVICES), ("Privacy", PRIVACY), ("System", SYSTEM), ("Personalization", PERSONALIZATION)):
        for e in lst:
            en, zh = pair(e["short"])
            add("Opt.{}.{}.DisplayName".format(tab, e["id"]), e["name"][0], e["name"][1])
            add("Opt.{}.{}.Short".format(tab, e["id"]), en, zh)
    # dependency reason keys
    for k in ("hyperv-dep", "sandbox-dep", "wsl-dep", "print-dep", "scan-dep", "xps-dep",
              "ps2-dep", "hvplatform-dep"):
        add("Opt.Dep." + k,
            "Shared virtualization / platform dependency — verify before disabling.",
            "共享虚拟化/平台依赖——请先确认再关闭。")

build_keys()

# ---------------------------------------------------------------------------
# C# emitters
# ---------------------------------------------------------------------------
def cs_string(s):
    return '"{}"'.format(esc(s))

# Data uses human names; map to the Core enum value names.
SOURCE_TYPE_MAP = {
    "MicrosoftOfficial": "MicrosoftOfficial",
    "WindowsImageDiscovery": "WindowsImageDiscovery",
    "CommunityReference": "CommunityProject",
    "WinForgeCurated": "WinForgeCurated",
    "EmpiricalValidation": "EmpiricalValidation",
}

def emit_feature_cs():
    lines = ["// <auto-generated by .tmp/phase11/gen_stage113.py — Phase 11.3 Windows Features catalog>",
             "// Do not edit by hand; edit the generator and re-run it.",
             "using System.Collections.Generic;",
             "using WinForge.Core.Models;",
             "using WinForge.Core.Services;",
             "",
             "namespace WinForge.Infrastructure.ComponentIntelligence;",
             "",
             "/// <summary>",
             "/// Curated Windows Components catalog (Stage 11.3, ADR-051/ADR-053). Maps a small set",
             "/// of well-understood optional features / capabilities onto exact DISM FeatureNames.",
             "/// A component only becomes Curated when a discovered item actually matches (ComponentMatcher).",
             "/// Every entry carries the operation taxonomy (Action / Mechanism / Scope), dependency",
             "/// edges (evidence-backed only; RelatedTo preferred over inferred Requires) and provenance.",
             "/// </summary>",
             "public sealed class WindowsFeaturesCatalog : IComponentCatalogProvider",
             "{",
             "    public IReadOnlyList<ComponentDefinition> GetDefinitions()",
             "    {",
             "        return new List<ComponentDefinition>",
             "        {"]
    for f in FEATURES:
        targets = ", ".join('new TechnicalTarget {{ Category = ComponentCategory.{0}, Match = MatchMethod.Exact, Pattern = {1} }}'.format(
            t[1], cs_string(t[0])) for t in f["targets"])
        deps = ", ".join('new ComponentDependency {{ ToId = {0}, Relation = DependencyRelation.{1}, Reason = "Opt.Dep.{2}" }}'.format(
            cs_string(d[0]), d[1], d[2]) for d in f.get("deps", []))
        deps_init = "new[] {{{}}}".format(deps) if deps else "new ComponentDependency[0]"
        scen = ", ".join("ComponentScenario.{}".format(s) for s in f.get("scen", ()))
        keep = ", ".join(cs_string("Feat.{}.KeepIf".format(f["id"])) for _ in [1]) if f.get("keep") else "new string[0]"
        keep_init = "new[] {{{}}}".format(keep) if f.get("keep") else "new string[0]"
        prov = ", ".join('new KnowledgeClaim(KnowledgeClaimKind.Fact, "Feat.{0}.Short", new[] {{ new KnowledgeSource(KnowledgeSourceType.{1}, "{2}", ConfidenceLevel.Verified) }})'.format(
            f["id"], SOURCE_TYPE_MAP.get(p, p), p) for p in f.get("prov", ("MicrosoftOfficial",)))
        lines.append("            new ComponentDefinition")
        lines.append("            {")
        lines.append("                Id = {},".format(cs_string(f["id"])))
        lines.append("                Category = ComponentCategory.OptionalFeature,")
        lines.append("                DisplayNameKey = {},".format(cs_string("Feat.{}.DisplayName".format(f["id"]))))
        lines.append("                ShortDescriptionKey = {},".format(cs_string("Feat.{}.Short".format(f["id"]))))
        lines.append("                LongDescriptionKey = {},".format(cs_string("Feat.{}.Short".format(f["id"]))))
        lines.append("                Recommendation = RecommendationLevel.{},".format(f["rec"]))
        lines.append("                Risk = RiskLevel.{},".format(f["risk"]))
        lines.append("                Removal = RemovalSupport.Supported,")
        lines.append("                Restore = RestoreSupport.{},".format(f["restore"]))
        lines.append("                Action = OptimizationAction.{},".format(f["action"]))
        lines.append("                Mechanism = OptimizationMechanism.{},".format(f["mechanism"]))
        lines.append("                Scope = OptimizationScope.{},".format(f["scope"]))
        scen_init = "new[] {{{}}}".format(scen) if scen else "new ComponentScenario[0]"
        tags_str = ", ".join(cs_string(t) for t in f.get("tags", ()))
        tags_init = "new[] {{{}}}".format(tags_str) if tags_str else "new string[0]"
        lines.append("                UserScenarios = {},".format(scen_init))
        lines.append("                KeepIf = {},".format(keep_init))
        lines.append("                RemoveIf = new string[0],")
        lines.append("                KnownImpact = new string[0],")
        lines.append("                Dependencies = {},".format(deps_init))
        lines.append("                Conflicts = new string[0],")
        lines.append("                TechnicalTargets = new[] {{{}}},".format(targets))
        lines.append('                CompatibilityRules = new[] { new CompatibilityRule { SupportedBuildMin = "22000", KnownOnBuilds = new[] { "26100" } } },')
        lines.append("                Provenance = new[] {{{}}},".format(prov))
        lines.append("                ScenarioRecommendations = new ScenarioRecommendation[0],")
        lines.append("                EstimatedSavingsBytes = 0,")
        lines.append("                SavingsConfidence = SavingsConfidence.None,")
        lines.append("                Tags = {},".format(tags_init))
        lines.append("            },")
    lines.append("        };")
    lines.append("    }")
    lines.append("}")
    return "\n".join(lines) + "\n"

def emit_opt_cs():
    lines = ["// <auto-generated by .tmp/phase11/gen_stage113.py — Phase 11.3 optimization catalog>",
             "// Do not edit by hand; edit the generator and re-run it.",
             "using System.Collections.Generic;",
             "using WinForge.Core.Models;",
             "using WinForge.Core.Services;",
             "",
             "namespace WinForge.Infrastructure.Customization;",
             "",
             "/// <summary>",
             "/// Reviewed knowledge catalog for the Services / Privacy / System / Personalization tabs",
             "/// (Stage 11.3, ADR-051/ADR-052/ADR-053). Every entry is WinForge-curated with explicit",
             "/// provenance; community evidence never auto-promotes to a trusted recommendation.",
             "/// Registry targets use SOFTWARE / SYSTEM (OfflineMachine) or DEFAULT_USER (new users).",
             "/// </summary>",
             "public sealed class OptimizationCatalog : IOptimizationCatalogProvider",
             "{",
             "    private static readonly IReadOnlyList<OptimizationDefinition> Entries = new List<OptimizationDefinition>",
             "    {"]
    def reg_emitter(r):
        return 'new RegistryTarget {{ Hive = {0}, KeyPath = {1}, ValueName = {2}, ValueKind = OfflineRegistryValueKind.{3}, RecommendedData = {4}, RestoreData = {5} }}'.format(
            cs_string(r["Hive"]), cs_string(r["KeyPath"]), cs_string(r["ValueName"]), r["Kind"], cs_string(r["RecommendedData"]), cs_string(r["RestoreData"]))
    for tab, lst in (("Services", SERVICES), ("Privacy", PRIVACY), ("System", SYSTEM), ("Personalization", PERSONALIZATION)):
        for e in lst:
            lines.append("        new OptimizationDefinition")
            lines.append("        {")
            lines.append("            Id = {},".format(cs_string(e["id"])))
            lines.append("            Tab = OptimizationTab.{},".format(tab))
            lines.append("            Action = OptimizationAction.{},".format(e["action"]))
            lines.append("            Mechanism = OptimizationMechanism.{},".format(e["mechanism"]))
            lines.append("            Scope = OptimizationScope.{},".format(e["scope"]))
            lines.append("            DisplayNameKey = {},".format(cs_string("Opt.{}.{}.DisplayName".format(tab, e["id"]))))
            lines.append("            ShortDescriptionKey = {},".format(cs_string("Opt.{}.{}.Short".format(tab, e["id"]))))
            lines.append("            LongDescriptionKey = {},".format(cs_string("Opt.{}.{}.Short".format(tab, e["id"]))))
            lines.append("            Recommendation = RecommendationLevel.{},".format(e["rec"]))
            lines.append("            Risk = RiskLevel.{},".format(e["risk"]))
            lines.append("            Removal = RemovalSupport.{},".format("Supported" if e["rec"] != "NeverRemove" else "Blocked"))
            lines.append("            Restore = RestoreSupport.{},".format(e["restore"]))
            if e.get("start") is None:
                lines.append("            ProposedStartType = null,")
            else:
                lines.append("            ProposedStartType = ServiceStartType.{},".format(e["start"]))
            if e.get("svc"):
                lines.append("            ServiceName = {},".format(cs_string(e["svc"])))
            if e.get("compat"):
                lines.append("            CompatibilityRules = new[] {{ new CompatibilityRule {{ SupportedBuildMin = {0} }} }},".format(cs_string(e["compat"]["min"])))
            else:
                lines.append("            CompatibilityRules = new CompatibilityRule[0],")
            prov = ", ".join('new KnowledgeClaim(KnowledgeClaimKind.Fact, "Opt.{0}.{1}.Short", new[] {{ new KnowledgeSource(KnowledgeSourceType.{2}, "{3}", ConfidenceLevel.Verified) }})'.format(
                tab, e["id"], SOURCE_TYPE_MAP.get(p, p), p) for p in e.get("prov", ("MicrosoftOfficial",)))
            lines.append("            Provenance = new[] {{{}}},".format(prov))
            scen = ", ".join("ComponentScenario.{}".format(s) for s in e.get("scen", ()))
            scen_init = "new[] {{{}}}".format(scen) if scen else "new ComponentScenario[0]"
            targets_str = ", ".join(reg_emitter(r) for r in e.get("targets", []))
            targets_init = "new[] {{{}}}".format(targets_str) if targets_str else "new RegistryTarget[0]"
            lines.append("            UserScenarios = {},".format(scen_init))
            lines.append("            Dependencies = new ComponentDependency[0],")
            lines.append("            KeepIf = new string[0],")
            lines.append("            RemoveIf = new string[0],")
            lines.append("            KnownImpact = new string[0],")
            lines.append("            RegistryTargets = {},".format(targets_init))
            lines.append("            TargetIdentifier = null,")
            lines.append("            IsStandardVisible = true,")
            lines.append("        },")
    lines.append("    };")
    lines.append("")
    lines.append("    public IReadOnlyList<OptimizationDefinition> GetEntries() => Entries;")
    lines.append("}")
    return "\n".join(lines) + "\n"

# ---------------------------------------------------------------------------
# resx insertion (idempotent, mirrors gen_catalog.py)
# ---------------------------------------------------------------------------
def resx_block(path_is_zh):
    pairs = sorted(ALL_KEYS.items())
    out = []
    for key, (en, zh) in pairs:
        val = zh if path_is_zh else en
        out.append('  <data name="{0}" xml:space="preserve"><value>{1}</value></data>'.format(
            key, val.replace("&", "&amp;").replace("<", "&lt;").replace(">", "&gt;")))
    return "\n".join(out)

def owned_keys():
    keys = list(ALL_KEYS.keys())
    keys += ["Feat.{}.DisplayName".format(f["id"]) for f in FEATURES]
    keys += ["Feat.{}.Short".format(f["id"]) for f in FEATURES]
    keys += ["Feat.{}.KeepIf".format(f["id"]) for f in FEATURES if f.get("keep")]
    return keys

def insert_resx(path, block):
    with open(path, "r", encoding="utf-8") as f:
        content = f.read()
    content = re.sub(r"<!-- WINFORGE_STAGE113_BLOCK_START -->.*?<!-- WINFORGE_STAGE113_BLOCK_END -->",
                     "", content, flags=re.DOTALL)
    for key in owned_keys():
        content = re.sub(r'\s*<data name="' + re.escape(key) + r'"[^>]*>.*?</data>',
                         "", content, flags=re.DOTALL)
    full = "\n{0}\n{1}\n{2}\n".format(BLOCK_START, block, BLOCK_END)
    idx = content.rfind("</root>")
    if idx < 0:
        raise RuntimeError("no </root> in " + path)
    content = content[:idx] + full + content[idx:]
    with open(path, "w", encoding="utf-8") as f:
        f.write(content)

# ---------------------------------------------------------------------------
# Coverage matrix document (Part V)
# ---------------------------------------------------------------------------
def mechanism_caption(mech):
    return mech

def emit_matrix():
    L = []
    L.append("# Stage 11.3 — Customize Coverage Matrix")
    L.append("")
    L.append("> Generated by `.tmp/phase11/gen_stage113.py`. Every candidate records: logical Id, "
             "target tab, user-facing name, mechanism type, technical target, image applicability, "
             "source/provenance, reversibility, recommendation, risk, compatibility/build constraints, "
             "implementation status, reason. Quality and correctness override quantity (ADR-053).")
    L.append("")
    counts = {}
    L.append("## Implemented (first tranche)")
    L.append("")
    L.append("| Id | Tab | Name | Mechanism | Technical target | Applicability | Provenance | Reversibility | Recommendation | Risk | Compatibility | Status |")
    L.append("|---|---|---|---|---|---|---|---|---|---|---|---|---|")
    def row(ident, tab, name, mech, target, appl, prov, rev, rec, risk, compat, status, reason=""):
        counts[tab] = counts.get(tab, 0) + 1
        L.append("| {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} | {} |".format(
            ident, tab, name, mech, target, appl, prov, rev, rec, risk, compat, status))
    for f in FEATURES:
        targets = ", ".join("{} {}".format(t[1], t[0]) for t in f["targets"])
        row(f["id"], "Windows Components", f["name"][0], f["mechanism"], targets,
            "Mounted image (DISM enumeration)", "MicrosoftOfficial", f["restore"],
            f["rec"], f["risk"], "Win11 22000+", "Implemented")
    for e in SERVICES:
        target = e["svc"] + ("" if e["start"] is None else " → " + e["start"])
        row(e["id"], "Services", e["name"][0], "ServiceStartup", target,
            "OfflineMachine (SYSTEM hive)", ", ".join(e["prov"]), "Easy",
            e["rec"], e["risk"], "Win11 22000+", "Implemented")
    for e in PRIVACY:
        t = e["targets"][0]
        row(e["id"], "Privacy", e["name"][0], "PrivacyPolicy",
            "{} \\ {} = {}".format(t["Hive"], t["KeyPath"], t["ValueName"]),
            "OfflineMachine (SOFTWARE)", ", ".join(e["prov"]), "Easy",
            e["rec"], e["risk"], "Win11 22000+", "Implemented")
    for e in SYSTEM:
        t = e["targets"][0]
        compat = "Win11 {} +".format(e["compat"]["min"]) if e.get("compat") else "Win11 22000+"
        row(e["id"], "System", e["name"][0], "SystemPolicy",
            "{} \\ {} = {}".format(t["Hive"], t["KeyPath"], t["ValueName"]),
            "OfflineMachine ({} hive)".format("SYSTEM" if t["Hive"] == "SYSTEM" else "SOFTWARE"),
            ", ".join(e["prov"]), "Easy", e["rec"], e["risk"], compat, "Implemented")
    for e in PERSONALIZATION:
        t = e["targets"][0]
        scope = "Default User profile (new users)" if t["Hive"] == "DEFAULT_USER" else "OfflineMachine (SOFTWARE)"
        row(e["id"], "Personalization", e["name"][0], e["mechanism"],
            "{} \\ {} = {}".format(t["Hive"], t["KeyPath"], t["ValueName"]),
            scope, ", ".join(e["prov"]), "Easy", e["rec"], e["risk"], "Win11 22000+", "Implemented")
    L.append("")
    L.append("## Deferred (investigated; not in this tranche)")
    L.append("")
    L.append("| Tab | Candidate | Reason |")
    L.append("|---|---|---|")
    for tab, name, reason, _ in DEFERRED:
        L.append("| {} | {} | {} |".format(tab, name, reason))
    L.append("")
    L.append("## Rejected (never offered)")
    L.append("")
    L.append("| Tab | Candidate | Reason |")
    L.append("|---|---|---|")
    for tab, name, reason, _ in REJECTED:
        L.append("| {} | {} | {} |".format(tab, name, reason))
    L.append("")
    L.append("## Unsupported offline")
    L.append("")
    L.append("| Tab | Candidate | Reason |")
    L.append("|---|---|---|")
    for tab, name, reason, _ in UNSUPPORTED:
        L.append("| {} | {} | {} |".format(tab, name, reason))
    L.append("")
    L.append("## Totals")
    L.append("")
    L.append("- Implemented candidates: {} (Windows Components {}, Services {}, Privacy {}, System {}, Personalization {})".format(
        sum(counts.values()), counts.get("Windows Components", 0), counts.get("Services", 0),
        counts.get("Privacy", 0), counts.get("System", 0), counts.get("Personalization", 0)))
    L.append("- Deferred: {} · Rejected: {} · Unsupported: {}".format(len(DEFERRED), len(REJECTED), len(UNSUPPORTED)))
    L.append("")
    L.append("## Before / After Stage 11.3")
    L.append("")
    L.append("| Tab | Before Stage 11.3 | After Stage 11.3 (implemented, standard-visible) |")
    L.append("|---|---|---|")
    L.append("| Apps | 22 curated definitions in the catalog (real present-in-image curated at Stage 11.1: 11) | 22 (catalog unchanged; expansion candidates deferred — see Deferred) |")
    L.append("| Windows Components | 0 knowledge rows (raw package list only) | {} |".format(counts.get("Windows Components", 0)))
    L.append("| Services | 3 configurable raw services (DiagTrack/WerSvc/PcaSvc) | {} |".format(counts.get("Services", 0)))
    L.append("| Privacy | 5 trusted registry settings (old thin page) | {} |".format(counts.get("Privacy", 0)))
    L.append("| System | 3 trusted registry settings (old thin page) | {} |".format(counts.get("System", 0)))
    L.append("| Personalization | Coming Soon (placeholder) | {} |".format(counts.get("Personalization", 0)))
    L.append("")
    L.append("Note: counts are the implemented, standard-visible rows per tab. Windows Components / Services "
             "rows additionally depend on image discovery (present-in-image only) exactly like the Apps tab.")
    L.append("")
    L.append("Apps: the Stage 11.2 curated AppX catalog (22 definitions) is unchanged in this tranche; "
             "Apps coverage expansion candidates are listed under Deferred.")
    return "\n".join(L) + "\n"

import re

def main():
    with open(FEAT_CS, "w", encoding="utf-8") as f:
        f.write(emit_feature_cs())
    with open(OPT_CS, "w", encoding="utf-8") as f:
        f.write(emit_opt_cs())
    insert_resx(RESX_EN, resx_block(False))
    insert_resx(RESX_ZH, resx_block(True))
    with open(MATRIX, "w", encoding="utf-8") as f:
        f.write(emit_matrix())
    print("Wrote", FEAT_CS)
    print("Wrote", OPT_CS)
    print("Inserted", len(ALL_KEYS), "keys into each resx.")
    print("Wrote", MATRIX)
    print("Features:", len(FEATURES), "Services:", len(SERVICES), "Privacy:", len(PRIVACY),
          "System:", len(SYSTEM), "Personalization:", len(PERSONALIZATION))

if __name__ == "__main__":
    main()
