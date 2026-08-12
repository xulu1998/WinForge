#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Phase 11.2 generator: produces the curated component catalog (C#) AND inserts
localized resx entries (en + zh-CN) so the two never drift.

Single source of truth for component ids, their human guidance, and the
localization keys. Run from the WinForge repo root:

    python3 .tmp/phase11/gen_catalog.py

The resx insertion is IDEMPOTENT: it replaces the marked WINFORGE_CATALOG_BLOCK
region on every run, so re-running never duplicates keys.
"""
import os
import xml.sax.saxutils as su

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CS_PATH = os.path.join(REPO, "src", "WinForge.Infrastructure", "ComponentIntelligence", "CuratedComponentCatalog.cs")
RESX_EN = os.path.join(REPO, "src", "WinForge.App", "Resources", "Strings.resx")
RESX_ZH = os.path.join(REPO, "src", "WinForge.App", "Resources", "Strings.zh-CN.resx")
BLOCK_START = "<!-- WINFORGE_CATALOG_BLOCK_START -->"
BLOCK_END = "<!-- WINFORGE_CATALOG_BLOCK_END -->"

# --------------------------------------------------------------------------
# Shared UI labels + enum captions (key -> (en, zh))
# --------------------------------------------------------------------------
SHARED = [
    ("Nav.ComponentIntelligence", "Component Intelligence", "组件智能"),
    ("ComponentIntelligence.Title", "Component Intelligence", "组件智能"),
    ("ComponentIntelligence.Intro",
     "Understand what each Windows component does, whether you need it, and what happens if you remove it. Read-only analysis prototype.",
     "了解每个 Windows 组件的作用、是否需要，以及移除后会发生什么。只读分析原型。"),
    ("ComponentIntelligence.Discover", "Discover from mounted image", "从已挂载映像发现"),
    ("ComponentIntelligence.Discovering", "Discovering…", "正在发现…"),
    ("ComponentIntelligence.StandardMode", "Standard (curated only)", "标准（仅策展）"),
    ("ComponentIntelligence.AdvancedMode", "Advanced (all discovered)", "高级（全部已发现）"),
    ("ComponentIntelligence.NoImage",
     "Mount a working image to inventory real components. Showing the curated catalog only.",
     "请挂载工作映像以清点真实组件。当前仅显示策展目录。"),
    ("ComponentIntelligence.StatusEmpty", "No components discovered.", "未发现组件。"),
    ("ComponentIntelligence.Summary",
     "Discovered {0} AppX, {1} capabilities, {2} optional features, {3} packages.",
     "已发现 {0} 个 AppX、{1} 个功能、{2} 个可选功能、{3} 个程序包。"),
    ("ComponentIntelligence.Counts",
     "Curated: {0} · Unclassified: {1} · Protected: {2} · Unsupported: {3}",
     "已策展: {0} · 未分类: {1} · 受保护: {2} · 不支持: {3}"),

    # RecommendationLevel
    ("Recommendation.RecommendedRemove", "Recommended to remove", "建议移除"),
    ("Recommendation.OptionalRemove", "Optional to remove", "可选移除"),
    ("Recommendation.UsuallyKeep", "Usually keep", "通常保留"),
    ("Recommendation.AdvancedOnly", "Advanced users only", "仅限高级用户"),
    ("Recommendation.NeverRemove", "Never remove", "切勿移除"),
    ("Recommendation.Unknown", "Unknown", "尚未确认"),

    # RiskLevel
    ("Risk.Low", "Low", "低"),
    ("Risk.Medium", "Medium", "中"),
    ("Risk.High", "High", "高"),
    ("Risk.Critical", "Critical", "严重"),
    ("Risk.Unknown", "Unknown", "尚未确认"),

    # RemovalSupport
    ("Removal.Supported", "Supported", "支持"),
    ("Removal.Conditional", "Conditional", "有条件"),
    ("Removal.Experimental", "Experimental", "实验性"),
    ("Removal.Blocked", "Blocked", "已阻止"),
    ("Removal.Unknown", "Unknown", "尚未确认"),

    # RestoreSupport
    ("Restore.Easy", "Easy (reinstall / re-enable)", "容易（重装/重新启用）"),
    ("Restore.RequiresSource", "Requires Windows install source", "需要 Windows 安装源"),
    ("Restore.RequiresWindowsUpdate", "Requires Windows Update", "需要 Windows 更新"),
    ("Restore.ReinstallWindows", "Requires reinstalling Windows", "需要重装 Windows"),
    ("Restore.Unknown", "Unknown", "尚未确认"),

    # Classification
    ("Classification.Curated", "Curated", "已策展"),
    ("Classification.DiscoveredUnclassified", "Unclassified", "未分类"),
    ("Classification.Protected", "Protected", "受保护"),
    ("Classification.Unsupported", "Unsupported", "不支持"),

    # Category
    ("Category.AppX", "Provisioned AppX", "预置 AppX"),
    ("Category.Capability", "Capability", "功能能力"),
    ("Category.OptionalFeature", "Optional Feature", "可选功能"),
    ("Category.CbsPackage", "Windows Package", "Windows 程序包"),
    ("Category.Service", "Service", "服务"),
    ("Category.ScheduledTask", "Scheduled Task", "计划任务"),
    ("Category.Driver", "Driver", "驱动"),
    ("Category.Language", "Language", "语言"),
    ("Category.WinRecovery", "Windows Recovery", "Windows 恢复"),
    ("Category.SystemApp", "System App", "系统应用"),
    ("Category.Protected", "Protected", "受保护"),
    ("Category.Unknown", "Unknown", "未知"),
    ("Classification.Unknown", "Unknown", "未知"),

    # Detail panel labels
    ("Component.WhatIsThis", "What is this?", "这是什么？"),
    ("Component.ShortDescription", "Description", "说明"),
    ("Component.Recommendation", "Recommendation", "建议"),
    ("Component.Risk", "Risk if removed", "移除风险"),
    ("Component.Scenarios", "Relevant if you…", "相关场景"),
    ("Component.KeepIf", "Keep it if", "保留条件"),
    ("Component.RemoveIf", "You can remove it if", "可移除条件"),
    ("Component.Impact", "What stops working", "移除后受影响"),
    ("Component.Restoration", "Restoration", "恢复方式"),
    ("Component.Savings", "Space saved", "可节省空间"),
    ("Component.TechnicalDetails", "Technical details", "技术详情"),
    ("Component.RawCategory", "Raw category", "原始类别"),
    ("Component.RawIdentity", "Windows identity", "Windows 标识"),
    ("Component.RawState", "State", "状态"),
    ("Component.RawVersion", "Version", "版本"),
    ("Component.MatchRule", "Matching rule", "匹配规则"),
    ("Component.Unknown", "Unknown", "尚未确认"),
    ("Component.NotInventoried", "Not yet inventoried", "尚未清点"),
    ("Component.Present", "Present in image", "映像中存在"),
    ("Component.ListHeader", "Components", "组件"),
    ("Component.Why", "Why?", "为什么？"),
    ("Component.Evidence", "Evidence", "信息来源"),
    ("Component.Blocked", "Cannot be removed", "不可移除"),
    ("Component.NotConfirmed", "WinForge has not confirmed this", "WinForge 尚未确认"),

    # Knowledge source type captions
    ("KnowledgeSource.MicrosoftOfficial", "Microsoft official", "Microsoft 官方"),
    ("KnowledgeSource.WindowsImageDiscovery", "Current Windows image", "当前 Windows 镜像"),
    ("KnowledgeSource.CommunityProject", "Community reference", "社区参考"),
    ("KnowledgeSource.WinForgeCurated", "WinForge reviewed", "WinForge 已审核"),
    ("KnowledgeSource.EmpiricalValidation", "Empirical validation", "实证验证"),
    ("KnowledgeSource.Unknown", "Unknown", "未知"),

    # Confidence captions
    ("Confidence.Unknown", "Unknown", "未知"),
    ("Confidence.Low", "Low", "低"),
    ("Confidence.Medium", "Medium", "中"),
    ("Confidence.High", "High", "高"),
    ("Confidence.Verified", "Verified", "已核实"),

    # Customize knowledge tab (Part D–H)
    ("Customize.Tab.Knowledge", "Component Knowledge", "组件知识"),
    ("Knowledge.Filter.All", "All", "全部"),
    ("Knowledge.Filter.RecommendedRemove", "Recommended remove", "推荐精简"),
    ("Knowledge.Filter.OptionalRemove", "Optional remove", "可精简"),
    ("Knowledge.Filter.UsuallyKeep", "Usually keep", "建议保留"),
    ("Knowledge.Filter.AdvancedOnly", "Advanced", "高级"),
    ("Knowledge.Filter.NeverRemove", "Do not remove", "不可移除"),
    ("Knowledge.Detail", "Details", "详情"),
    ("Knowledge.WhyRecommended", "Why is this recommended?", "为什么这样建议？"),
    ("Knowledge.SortNote", "Sorted by recommendation, then risk.", "按建议排序，再按风险排序。"),
    ("Knowledge.PresentInImage", "Present in this image", "存在于此映像"),
    ("Knowledge.CatalogOnly", "Catalog entry (not detected in this image)", "目录条目（此映像未检测到）"),

    # Component Intelligence status
    ("ComponentIntelligence.StatusCancelled", "Discovery was cancelled.", "发现已取消。"),

    # Dependency relation captions
    ("Dependency.Unknown", "Unknown", "未知"),
    ("Dependency.Requires", "Requires", "需要"),
    ("Dependency.RequiredBy", "Required by", "被依赖"),
    ("Dependency.RelatedTo", "Related to", "相关"),
    ("Dependency.ConflictsWith", "Conflicts with", "冲突"),
    ("Dependency.RecommendsKeeping", "Recommends keeping", "建议保留"),

    # Savings confidence captions
    ("Savings.None", "Unknown", "未知"),
    ("Savings.Low", "Low", "低"),
    ("Savings.Medium", "Medium", "中"),
    ("Savings.High", "High", "高"),

    # ComponentScenario captions
    ("ComponentScenario.Unknown", "Unknown", "未知"),
    ("ComponentScenario.Gaming", "Gaming", "游戏"),
    ("ComponentScenario.Office", "Office", "办公"),
    ("ComponentScenario.Developer", "Developer", "开发"),
    ("ComponentScenario.Laptop", "Laptop", "笔记本"),
    ("ComponentScenario.TouchPen", "Touch / Pen", "触控 / 手写笔"),
    ("ComponentScenario.PrintingScanning", "Printing & Scanning", "打印与扫描"),
    ("ComponentScenario.Wsl", "WSL", "WSL"),
    ("ComponentScenario.Docker", "Docker", "Docker"),
    ("ComponentScenario.HyperV", "Hyper-V", "Hyper-V"),
    ("ComponentScenario.WindowsSandbox", "Windows Sandbox", "Windows 沙盒"),
    ("ComponentScenario.XboxGamePass", "Xbox Game Pass", "Xbox Game Pass"),
    ("ComponentScenario.MixedReality", "Mixed Reality", "混合现实"),
    ("ComponentScenario.Accessibility", "Accessibility", "辅助功能"),
    ("ComponentScenario.EnterpriseDomain", "Enterprise / Domain", "企业 / 域"),
    ("ComponentScenario.RemoteDesktop", "Remote Desktop", "远程桌面"),
    ("ComponentScenario.Bluetooth", "Bluetooth", "蓝牙"),
    ("ComponentScenario.WiFi", "Wi-Fi", "Wi-Fi"),
    ("ComponentScenario.Biometrics", "Biometrics", "生物识别"),
    ("ComponentScenario.WindowsHello", "Windows Hello", "Windows Hello"),
]

# --------------------------------------------------------------------------
# Curated components (id -> data). Technical targets use stable Microsoft
# inbox AppX family-name PREFIXES (Microsoft-supported identifiers). A
# component becomes Curated only when a discovered item actually matches.
#
# `prov` (optional): list of provenance claims
#   {"kind": "Fact"|"Recommendation", "en":.., "zh":..,
#    "src": "MicrosoftOfficial"|"WinForgeCurated"|..., "name":.., "conf":.., "ref":..}
# `scen` (optional): list of (scenario, rec, reason_en, reason_zh)
# --------------------------------------------------------------------------
COMPONENTS = [
    {
        "id": "Weather", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.BingWeather", "")],
        "rec": "RecommendedRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Microsoft Weather", "微软天气"),
        "short": ("Microsoft's weather application, provisioned for new users.",
                  "微软天气应用，为新用户预置。"),
        "long": ("The built-in Weather app. Its removal only affects the app itself; it does not "
                 "disable Windows networking or any weather APIs used by the OS.",
                 "内置的天气应用。移除仅影响该应用本身，不会禁用 Windows 网络或系统使用的天气 API。"),
        "keepif": ("You actively use the Microsoft Weather app.", "你经常使用微软天气应用。"),
        "removeif": ("You never check weather in the built-in app and use a website or another app.",
                     "你从不在内置应用查看天气，改用网站或其他应用。"),
        "impact": ("The Weather app is unavailable for newly created users. No core Windows feature is removed.",
                   "新创建的用户将无法使用天气应用；不影响任何核心 Windows 功能。"),
    },
    {
        "id": "Clipchamp", "category": "AppX",
        "targets": [("AppX", "Prefix", "Clipchamp.Clipchamp", "")],
        "rec": "RecommendedRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Clipchamp", "Clipchamp"),
        "short": ("Microsoft's video editor, provisioned for new users.", "微软视频编辑器，为新用户预置。"),
        "long": ("A consumer video-editing and capture tool. Removal only affects the app; it is "
                 "reinstallable from the Microsoft Store.",
                 "面向消费者的视频编辑与录制工具。移除仅影响该应用，可从 Microsoft Store 重新安装。"),
        "keepif": ("You edit or record video with Clipchamp.", "你使用 Clipchamp 编辑或录制视频。"),
        "removeif": ("You never use Clipchamp and prefer another editor.", "你从不用 Clipchamp，改用其他编辑器。"),
        "impact": ("Clipchamp is unavailable for new users. No system video capability is removed.",
                   "新用户将无法使用 Clipchamp；不影响系统视频能力。"),
    },
    {
        "id": "GetHelp", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.GetHelp", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Get Help", "获取帮助"),
        "short": ("Microsoft's help viewer / contact-support app.", "微软帮助查看器 / 联系支持应用。"),
        "long": ("Launches help articles and can open a support contact flow. Removing it does not "
                 "disable Windows troubleshooting or Settings search.",
                 "用于打开帮助文章并可发起支持联系。移除它不会禁用 Windows 故障排除或设置搜索。"),
        "keepif": ("You use the built-in Help app to reach Microsoft support.", "你使用内置“帮助”应用联系微软支持。"),
        "removeif": ("You never use the Help app and search the web instead.", "你从不用“帮助”应用，而是直接上网搜索。"),
        "impact": ("The Get Help app is unavailable. Windows Settings and troubleshooting remain.",
                   "“获取帮助”应用不可用；Windows 设置与故障排除仍然可用。"),
    },
    {
        "id": "XboxApp", "category": "AppX",
        "targets": [
            ("AppX", "Prefix", "Microsoft.XboxApp", ""),
            ("AppX", "Prefix", "Microsoft.XboxGamingOverlay", ""),
            ("AppX", "Prefix", "Microsoft.XboxIdentityProvider", ""),
            ("AppX", "Prefix", "Microsoft.XboxSpeechToTextOverlay", ""),
            ("AppX", "Prefix", "Microsoft.XboxGameOverlay", ""),
            ("AppX", "Prefix", "Microsoft.Xbox.TCUI", ""),
            ("AppX", "Prefix", "Microsoft.GamingServices", ""),
            ("AppX", "Prefix", "Microsoft.GamingServicesNet", ""),
            ("AppX", "Prefix", "Microsoft.GamingApp", ""),
        ],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["Gaming", "XboxGamePass"],
        "deps": [],
        "display": ("Xbox / Gaming", "Xbox / 游戏"),
        "short": ("Xbox app plus gaming overlays, providers and services.", "Xbox 应用及其游戏覆盖层、提供程序与服务。"),
        "long": ("The Xbox app, game overlay and identity provider, plus the Gaming Services "
                 "runtime used by Xbox Game Pass and PC games. Several raw Xbox identities collapse "
                 "into this one logical component. Removal is safe for users who do not game on Xbox.",
                 "Xbox 应用、游戏覆盖层与身份提供程序，以及 Xbox Game Pass 和 PC 游戏使用的 Gaming Services 运行时。"
                 "多个 Xbox 原始标识归并为这一个逻辑组件。不玩 Xbox 的用户可安全移除。"),
        "keepif": ("You use Xbox Game Pass or play Xbox titles on this PC.", "你使用 Xbox Game Pass 或在此电脑上游玩 Xbox 游戏。"),
        "removeif": ("You do not use Xbox apps or PC gaming.", "你不使用 Xbox 应用或 PC 游戏。"),
        "impact": ("Xbox app, game overlay, Xbox sign-in provider and Gaming Services are removed. "
                   "Non-gaming apps are unaffected.",
                   "Xbox 应用、游戏覆盖层、Xbox 登录提供程序与 Gaming Services 被移除；非游戏应用不受影响。"),
        "scen": [("Gaming", "UsuallyKeep",
                  "Kept because you selected a Gaming profile; Xbox Game Pass and PC gaming depend on it.",
                  "选择“游戏”配置后保留；Xbox Game Pass 与 PC 游戏依赖它。")],
        "prov": [
            {"kind": "Fact", "en": "Xbox / Gaming bundles the Xbox app, game overlay, identity provider and Gaming Services runtime.",
             "zh": "Xbox / 游戏 包含 Xbox 应用、游戏覆盖层、身份提供程序与 Gaming Services 运行时。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "Xbox App"},
            {"kind": "Recommendation", "en": "Optional to remove for users who do not use Xbox / PC gaming.",
             "zh": "对不使用 Xbox / PC 游戏的用户，可选移除。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "Photos", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.Windows.Photos", "")],
        "rec": "UsuallyKeep", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Photos", "照片"),
        "short": ("Microsoft's default photo viewer/editor.", "微软默认的照片查看器/编辑器。"),
        "long": ("The default Photos app. Many users rely on it as the shell image viewer; removing "
                 "it leaves the OS without a built-in photo viewer (files still open via other apps).",
                 "默认的照片应用。许多用户将其作为系统图片查看器；移除后系统将没有内置照片查看器（文件仍可由其他应用打开）。"),
        "keepif": ("You use Photos as your default image viewer.", "你将“照片”用作默认图片查看器。"),
        "removeif": ("You use a different photo app and never the built-in one.", "你使用其他照片应用，从不用内置应用。"),
        "impact": ("The built-in Photos viewer is removed. Image files remain on disk and open via other apps.",
                   "内置“照片”查看器被移除；图片文件仍保留，可由其他应用打开。"),
    },
    {
        "id": "FeedbackHub", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.WindowsFeedbackHub", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Feedback Hub", "反馈中心"),
        "short": ("Microsoft's Windows feedback app.", "微软 Windows 反馈应用。"),
        "long": ("Used to send feedback to Microsoft. Not required by any Windows feature.",
                 "用于向微软发送反馈。任何 Windows 功能都不依赖它。"),
        "keepif": ("You submit feedback to Microsoft through the app.", "你通过该应用向微软提交反馈。"),
        "removeif": ("You never use the Feedback Hub.", "你从不用反馈中心。"),
        "impact": ("The Feedback Hub is unavailable. No Windows function depends on it.",
                   "反馈中心不可用；没有任何 Windows 功能依赖它。"),
    },
    {
        "id": "Maps", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.WindowsMaps", "")],
        "rec": "RecommendedRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["TouchPen", "Laptop"],
        "deps": [],
        "display": ("Maps", "地图"),
        "short": ("Microsoft's Maps application.", "微软地图应用。"),
        "long": ("The built-in Maps app. Offline maps and location APIs used by other apps are "
                 "separate Windows features and are NOT removed with this app.",
                 "内置的地图应用。其他应用使用的离线地图与位置 API 是独立的 Windows 功能，不会随此应用一起移除。"),
        "keepif": ("You use the built-in Maps app for navigation.", "你使用内置地图应用进行导航。"),
        "removeif": ("You use a web map or another maps app.", "你使用网页地图或其他地图应用。"),
        "impact": ("The Maps app is removed. Location services and other apps' maps still work.",
                   "地图应用被移除；位置服务及其他应用的地图仍可用。"),
    },
    {
        "id": "PhoneLink", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.YourPhone", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["Laptop", "Bluetooth"],
        "deps": [],
        "display": ("Phone Link", "手机连接"),
        "short": ("Microsoft's app that links your phone to your PC.", "微软将手机与电脑连接的应。"),
        "long": ("Phone Link (Your Phone) mirrors notifications, photos and calls from an Android "
                 "phone. Removing it only affects that integration.",
                 "手机连接（Your Phone）可将 Android 手机的通知、照片和通话同步到电脑。移除仅影响该集成。"),
        "keepif": ("You use Phone Link to connect an Android phone.", "你使用“手机连接”连接 Android 手机。"),
        "removeif": ("You do not link a phone to this PC.", "你不将此电脑与手机连接。"),
        "impact": ("Phone Link integration is removed. Your phone and PC otherwise work normally.",
                   "手机连接集成被移除；手机与电脑其他功能正常。"),
    },
    {
        "id": "Solitaire", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.MicrosoftSolitaireCollection", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Solitaire Collection", "纸牌合集"),
        "short": ("Microsoft's Solitaire / Casual Games app.", "微软纸牌 / 休闲游戏应用。"),
        "long": ("A casual games bundle. Purely optional entertainment; no system dependency.",
                 "休闲游戏合集。纯可选的娱乐应用，无系统依赖。"),
        "keepif": ("You play the built-in Solitaire or casual games.", "你玩内置纸牌或休闲游戏。"),
        "removeif": ("You never play the bundled games.", "你从不玩内置游戏。"),
        "impact": ("The Solitaire Collection is removed. No Windows function depends on it.",
                   "纸牌合集被移除；没有任何 Windows 功能依赖它。"),
    },
    {
        "id": "Teams", "category": "AppX",
        "targets": [("AppX", "Prefix", "MicrosoftTeams", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["Office", "EnterpriseDomain"],
        "deps": [("OneDrive", "RelatedTo",
                  "Teams stores shared chat files and attachments in OneDrive; the two are associated, but Teams core chat/calls/meetings work without OneDrive. This is NOT a hard runtime dependency.")],
        "display": ("Microsoft Teams", "Microsoft Teams"),
        "short": ("Microsoft's consumer/work chat and meetings app.", "微软消费/工作版聊天与会议应用。"),
        "long": ("Teams for personal and work communication. Files shared in Teams are stored in "
                 "OneDrive; the two are associated (see the dependency note) but Teams works without OneDrive.",
                 "用于个人与工作沟通的 Teams。Teams 中共享的文件存储在 OneDrive 中；两者相关联（见依赖说明），但 Teams 在没有 OneDrive 时仍可正常使用。"),
        "keepif": ("You use Teams for chat, calls or meetings.", "你使用 Teams 进行聊天、通话或会议。"),
        "removeif": ("You do not use Teams on this PC.", "你在此电脑上不使用 Teams。"),
        "impact": ("The Teams app is removed. Web Teams still works in a browser.",
                   "Teams 应用被移除；网页版 Teams 仍可在浏览器中使用。"),
    },
    {
        "id": "OneDrive", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.OneDriveSync", "")],
        "rec": "UsuallyKeep", "risk": "Medium", "removal": "Conditional", "restore": "RequiresWindowsUpdate",
        "scenarios": ["Office", "Laptop"],
        "deps": [],
        "display": ("OneDrive", "OneDrive"),
        "short": ("Microsoft's file-sync client.", "微软文件同步客户端。"),
        "long": ("OneDrive syncs your files to the cloud and is deeply integrated with Office and "
                 "the shell. Removal is possible but can affect file sync and Teams/Office storage; "
                 "treat as Conditional and verify before removing.",
                 "OneDrive 将文件同步到云端，并与 Office 和文件资源管理器深度集成。可以移除，但可能影响文件同步以及 Teams/Office 存储；视为“有条件”，移除前请确认。"),
        "keepif": ("You rely on OneDrive to sync or back up your files.", "你依赖 OneDrive 同步或备份文件。"),
        "removeif": ("You do not use cloud file sync and keep files local only.", "你不使用云文件同步，仅保留本地文件。"),
        "impact": ("OneDrive sync stops; locally cached files may need re-downloading. Office and "
                   "shell integration are reduced.",
                   "OneDrive 同步停止；本地缓存的文件可能需要重新下载。Office 与文件资源管理器集成减弱。"),
    },

    # ---- Stage 11.2 expansions (conservative, real 25H2 identities) ----
    {
        "id": "AV1VideoExtension", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.AV1VideoExtension", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("AV1 Video Extension", "AV1 视频扩展"),
        "short": ("Codec that lets Windows play AV1-encoded video.", "让 Windows 播放 AV1 编码视频的编解码器。"),
        "long": ("Provides AV1 hardware/software playback. Optional: most content still plays via "
                 "other codecs; removing it only affects AV1 playback.",
                 "提供 AV1 硬件/软件播放能力。可选：大多数内容仍可通过其他编解码器播放；移除仅影响 AV1 播放。"),
        "keepif": ("You watch AV1 video or need maximum codec coverage.", "你观看 AV1 视频或需要最完整的编解码支持。"),
        "removeif": ("You do not play AV1 video and want to free space.", "你不播放 AV1 视频并希望释放空间。"),
        "impact": ("AV1 video will not play (other formats are unaffected).", "AV1 视频无法播放（其他格式不受影响）。"),
        "prov": [
            {"kind": "Fact", "en": "AV1 Video Extension provides AV1 media playback support.",
             "zh": "AV1 视频扩展提供 AV1 媒体播放支持。",
             "src": "MicrosoftOfficial", "name": "Microsoft Store", "conf": "High", "ref": "AV1VideoExtension"},
            {"kind": "Recommendation", "en": "Optional to remove for users who do not play AV1 video.",
             "zh": "对不播放 AV1 视频的用户，可选移除。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "AVCEncoderVideoExtension", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.AVCEncoderVideoExtension", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("AVC Encoder Video Extension", "AVC 编码器视频扩展"),
        "short": ("Codec that lets apps encode H.264/AVC video.", "让应用编码 H.264/AVC 视频的编解码器。"),
        "long": ("Provides AVC encoding used by some capture/share apps. Optional: removing it only "
                 "affects apps that depend on this specific encoder.",
                 "提供部分录制/分享应用使用的 AVC 编码能力。可选：移除仅影响依赖此特定编码器的应用。"),
        "keepif": ("You use screen/capture apps that encode AVC video.", "你使用编码 AVC 视频的录屏/捕获应用。"),
        "removeif": ("You do not use AVC encoding apps.", "你不使用 AVC 编码应用。"),
        "impact": ("Apps relying on the AVC encoder may lose export options.", "依赖 AVC 编码器的应用可能失去导出选项。"),
        "prov": [
            {"kind": "Fact", "en": "AVC Encoder Video Extension provides H.264/AVC video encoding.",
             "zh": "AVC 编码器视频扩展提供 H.264/AVC 视频编码。",
             "src": "MicrosoftOfficial", "name": "Microsoft Store", "conf": "High", "ref": "AVCEncoderVideoExtension"},
            {"kind": "Recommendation", "en": "Optional to remove for users who do not encode AVC video.",
             "zh": "对不编码 AVC 视频的用户，可选移除。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "BingNews", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.BingNews", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Microsoft News", "微软新闻"),
        "short": ("Microsoft's news aggregation app.", "微软新闻聚合应用。"),
        "long": ("A consumer news reader. Purely optional; no Windows feature depends on it.",
                 "面向消费者的新闻阅读应用。纯可选；没有任何 Windows 功能依赖它。"),
        "keepif": ("You read news in the built-in app.", "你在内置应用中阅读新闻。"),
        "removeif": ("You never use the News app.", "你从不用新闻应用。"),
        "impact": ("The News app is unavailable. Web news still works.", "新闻应用不可用；网页新闻仍可用。"),
        "prov": [
            {"kind": "Fact", "en": "Microsoft News is a consumer news aggregation app provisioned for new users.",
             "zh": "微软新闻是面向消费者的新闻聚合应用，为新用户预置。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "BingNews"},
            {"kind": "Recommendation", "en": "Optional to remove; no system dependency.",
             "zh": "可选移除；无系统依赖。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "BingSearch", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.BingSearch", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Search (Bing)", "搜索（Bing）"),
        "short": ("The Windows Search / Bing app used by Start and taskbar search.", "开始菜单与任务栏搜索所用的 Windows 搜索 / Bing 应用。"),
        "long": ("Provides web results inside Windows Search. Local file/settings search is a "
                 "separate OS service and continues to work after removal; only the web layer is affected.",
                 "提供 Windows 搜索中的网页结果。本地文件/设置搜索是独立的系统服务，移除后仍可用；仅网页层受影响。"),
        "keepif": ("You rely on web results inside Windows Search.", "你依赖 Windows 搜索中的网页结果。"),
        "removeif": ("You only search local files and settings.", "你只搜索本地文件与设置。"),
        "impact": ("Web results disappear from Windows Search; local search is unaffected.",
                   "Windows 搜索中的网页结果消失；本地搜索不受影响。"),
        "prov": [
            {"kind": "Fact", "en": "Bing Search provides web results inside the Windows Search experience.",
             "zh": "Bing 搜索在 Windows 搜索体验中提供网页结果。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "BingSearch"},
            {"kind": "Recommendation", "en": "Optional to remove; local search continues to work.",
             "zh": "可选移除；本地搜索仍可用。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "Calculator", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.WindowsCalculator", "")],
        "rec": "UsuallyKeep", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["Developer"],
        "deps": [],
        "display": ("Calculator", "计算器"),
        "short": ("Microsoft's calculator app.", "微软计算器应用。"),
        "long": ("A widely used utility. Removal is possible but many users expect it; keep unless "
                 "you replace it with another calculator.",
                 "广泛使用的工具。可以移除，但许多用户依赖它；除非用其他计算器替代，否则建议保留。"),
        "keepif": ("You use the built-in Calculator regularly.", "你经常使用内置计算器。"),
        "removeif": ("You use a different calculator app.", "你使用其他计算器应用。"),
        "impact": ("The Calculator app is removed. No OS function depends on it.", "计算器应用被移除；无系统功能依赖它。"),
        "prov": [
            {"kind": "Fact", "en": "Calculator is a built-in utility app used by many users.",
             "zh": "计算器是许多用户使用的内置工具应用。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "WindowsCalculator"},
            {"kind": "Recommendation", "en": "Usually keep; removal is safe but unexpected by many users.",
             "zh": "通常保留；移除安全但许多用户会感到意外。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "Notepad", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.WindowsNotepad", "")],
        "rec": "UsuallyKeep", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["Developer"],
        "deps": [],
        "display": ("Notepad", "记事本"),
        "short": ("Microsoft's plain-text editor.", "微软纯文本编辑器。"),
        "long": ("A long-standing utility many users and scripts rely on. Keep unless replaced.",
                 "许多用户与脚本依赖的经典工具。除非替代，否则建议保留。"),
        "keepif": ("You or your scripts use Notepad.", "你或你的脚本使用记事本。"),
        "removeif": ("You use another text editor exclusively.", "你只用其他文本编辑器。"),
        "impact": ("Notepad is removed. Many users and tools expect it.", "记事本被移除；许多用户与工具依赖它。"),
        "prov": [
            {"kind": "Fact", "en": "Notepad is a built-in plain-text editor relied on by users and scripts.",
             "zh": "记事本是许多用户与脚本依赖的内置纯文本编辑器。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "WindowsNotepad"},
            {"kind": "Recommendation", "en": "Usually keep; removal is safe but widely unexpected.",
             "zh": "通常保留；移除安全但普遍令人意外。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "Paint", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.Paint", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": [],
        "deps": [],
        "display": ("Paint", "画图"),
        "short": ("Microsoft's classic raster graphics editor.", "微软经典位图图像编辑器。"),
        "long": ("A familiar drawing tool. Optional: alternatives (Paint.NET, Photos) exist; removal "
                 "only affects the app itself.",
                 "熟悉的绘图工具。可选：存在替代（Paint.NET、照片）；移除仅影响该应用本身。"),
        "keepif": ("You use Paint for quick image edits.", "你使用画图进行快速图片编辑。"),
        "removeif": ("You use another image editor.", "你使用其他图像编辑器。"),
        "impact": ("Paint is removed. Image files remain and open via other apps.", "画图被移除；图片文件仍可由其他应用打开。"),
        "prov": [
            {"kind": "Fact", "en": "Paint is a classic raster graphics editor included with Windows.",
             "zh": "画图是 Windows 自带的经典位图图像编辑器。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "Paint"},
            {"kind": "Recommendation", "en": "Optional to remove; alternatives exist.",
             "zh": "可选移除；存在替代。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "Terminal", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.WindowsTerminal", "")],
        "rec": "UsuallyKeep", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["Developer"],
        "deps": [],
        "display": ("Windows Terminal", "Windows 终端"),
        "short": ("Microsoft's modern multi-tab terminal.", "微软现代多标签页终端。"),
        "long": ("Preferred terminal for developers and IT. Keep unless you use another; removal is "
                 "safe but inconvenient for power users.",
                 "开发者与 IT 人员偏好的终端。除非使用其他终端，否则建议保留；移除安全但对高级用户不便。"),
        "keepif": ("You use Windows Terminal for command-line work.", "你使用 Windows 终端进行命令行工作。"),
        "removeif": ("You use another terminal emulator.", "你使用其他终端模拟器。"),
        "impact": ("Windows Terminal is removed; classic Console Host still works.", "Windows 终端被移除；经典控制台宿主仍可用。"),
        "prov": [
            {"kind": "Fact", "en": "Windows Terminal is the modern multi-tab terminal for Windows.",
             "zh": "Windows 终端是 Windows 的现代多标签页终端。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "WindowsTerminal"},
            {"kind": "Recommendation", "en": "Usually keep; valuable for developers.",
             "zh": "通常保留；对开发者很有价值。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "ToDo", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.Todos", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["Office"],
        "deps": [],
        "display": ("Microsoft To Do", "Microsoft To Do"),
        "short": ("Microsoft's task / to-do list app.", "微软任务 / 待办事项应用。"),
        "long": ("A consumer productivity app. Optional; no Windows feature depends on it.",
                 "面向消费者的生产力应用。可选；没有任何 Windows 功能依赖它。"),
        "keepif": ("You manage tasks in Microsoft To Do.", "你在 Microsoft To Do 中管理任务。"),
        "removeif": ("You use another task app.", "你使用其他任务应用。"),
        "impact": ("Microsoft To Do is removed. Web/phone versions still work.", "Microsoft To Do 被移除；网页/手机版仍可用。"),
        "prov": [
            {"kind": "Fact", "en": "Microsoft To Do is a consumer task-management app.",
             "zh": "Microsoft To Do 是面向消费者的任务管理应用。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "Todos"},
            {"kind": "Recommendation", "en": "Optional to remove; no system dependency.",
             "zh": "可选移除；无系统依赖。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "QuickAssist", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.RemoteHelp", "")],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["EnterpriseDomain", "RemoteDesktop"],
        "deps": [],
        "display": ("Quick Assist", "快速助手"),
        "short": ("Microsoft's app for giving/receiving remote help.", "微软用于提供/接受远程帮助的应用。"),
        "long": ("Lets a helper view and control the screen to assist. Optional; not required for "
                 "normal use or for standard Remote Desktop.",
                 "让协助者查看并控制屏幕以提供帮助。可选；正常使用或标准远程桌面不需要它。"),
        "keepif": ("You (or help desk) use Quick Assist for remote help.", "你（或帮助台）使用快速助手进行远程协助。"),
        "removeif": ("You never use remote assistance.", "你从不用远程协助。"),
        "impact": ("Quick Assist is removed. Standard Remote Desktop is unaffected.", "快速助手被移除；标准远程桌面不受影响。"),
        "prov": [
            {"kind": "Fact", "en": "Quick Assist provides view/control screen sharing for remote help.",
             "zh": "快速助手提供用于远程协助的屏幕查看/控制共享。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "RemoteHelp"},
            {"kind": "Recommendation", "en": "Optional to remove; not required for normal use.",
             "zh": "可选移除；正常使用不需要。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
    {
        "id": "DesktopAppInstaller", "category": "AppX",
        "targets": [("AppX", "Prefix", "Microsoft.DesktopAppInstaller", "")],
        "rec": "UsuallyKeep", "risk": "Medium", "removal": "Conditional", "restore": "RequiresWindowsUpdate",
        "scenarios": ["Developer"],
        "deps": [],
        "display": ("App Installer (winget)", "应用安装程序（winget）"),
        "short": ("Provides the winget package manager and .msix/.appx installer.", "提供 winget 包管理器与 .msix/.appx 安装程序。"),
        "long": ("Hosts winget and the AppX/MSIX installer. Removing it breaks package management "
                 "and Store-based installs; treat as Conditional and keep unless you have a reason.",
                 "承载 winget 与 AppX/MSIX 安装程序。移除会破坏包管理与基于应用商店的安装；视为“有条件”，除非有理由否则保留。"),
        "keepif": ("You use winget or install .msix/.appx packages.", "你使用 winget 或安装 .msix/.appx 程序包。"),
        "removeif": ("You never use winget or sideloaded packages.", "你从不用 winget 或旁加载程序包。"),
        "impact": ("winget and AppX/MSIX install are unavailable; some installs break.",
                   "winget 与 AppX/MSIX 安装不可用；部分安装会失败。"),
        "prov": [
            {"kind": "Fact", "en": "App Installer provides the winget package manager and MSIX/AppX installer.",
             "zh": "应用安装程序提供 winget 包管理器与 MSIX/AppX 安装程序。",
             "src": "MicrosoftOfficial", "name": "Microsoft Learn", "conf": "High", "ref": "DesktopAppInstaller"},
            {"kind": "Recommendation", "en": "Usually keep; removal breaks package management.",
             "zh": "通常保留；移除会破坏包管理。",
             "src": "WinForgeCurated", "name": "WinForge review", "conf": "Verified", "ref": None},
        ],
    },
]


# --------------------------------------------------------------------------
# Helpers
# --------------------------------------------------------------------------
def cs_str(s: str) -> str:
    return '"' + s.replace("\\", "\\\\").replace('"', '\\"') + '"'


def cs_arr(items, elem_fn, empty_type):
    if not items:
        return f"new {empty_type}[0]"
    return "new[] { " + ", ".join(elem_fn(i) for i in items) + " }"


SRC_TYPE = {
    "MicrosoftOfficial": "KnowledgeSourceType.MicrosoftOfficial",
    "WindowsImageDiscovery": "KnowledgeSourceType.WindowsImageDiscovery",
    "CommunityProject": "KnowledgeSourceType.CommunityProject",
    "WinForgeCurated": "KnowledgeSourceType.WinForgeCurated",
    "EmpiricalValidation": "KnowledgeSourceType.EmpiricalValidation",
    "Unknown": "KnowledgeSourceType.Unknown",
}
CONF = {
    "Low": "ConfidenceLevel.Low", "Medium": "ConfidenceLevel.Medium",
    "High": "ConfidenceLevel.High", "Verified": "ConfidenceLevel.Verified",
    "Unknown": "ConfidenceLevel.Unknown",
}


def gen_csharp():
    lines = []
    lines.append("// <auto-generated by .tmp/phase11/gen_catalog.py — Phase 11.2 curated catalog>")
    lines.append("// Do not edit by hand; edit the generator and re-run it.")
    lines.append("using System.Collections.Generic;")
    lines.append("using WinForge.Core.Models;")
    lines.append("using WinForge.Core.Services;")
    lines.append("")
    lines.append("namespace WinForge.Infrastructure.ComponentIntelligence;")
    lines.append("")
    lines.append("/// <summary>")
    lines.append("/// Curated component catalog (Stages 11.1-11.2). Maps a SMALL set of")
    lines.append("/// well-understood Windows components onto stable Microsoft package-family")
    lines.append("/// identifiers. Technical targets use Prefix matching against well-known inbox")
    lines.append("/// AppX family names (Microsoft-supported identifiers); a component only becomes")
    lines.append("/// Curated when a discovered item actually matches. Unknown is preferred over")
    lines.append("/// invented information. Each entry carries provenance (FACT vs RECOMMENDATION)")
    lines.append("/// and, where relevant, scenario recommendation overrides.")
    lines.append("/// </summary>")
    lines.append("public sealed class CuratedComponentCatalog : IComponentCatalogProvider")
    lines.append("{")
    lines.append("    public IReadOnlyList<ComponentDefinition> GetDefinitions()")
    lines.append("    {")
    lines.append("        return new List<ComponentDefinition>")
    lines.append("        {")

    for c in COMPONENTS:
        cid = c["id"]
        lines.append("            new ComponentDefinition")
        lines.append("            {")
        lines.append(f'                Id = {cs_str(cid)},')
        lines.append(f'                Category = ComponentCategory.{c["category"]},')
        lines.append(f'                DisplayNameKey = {cs_str("Comp." + cid + ".DisplayName")},')
        lines.append(f'                ShortDescriptionKey = {cs_str("Comp." + cid + ".Short")},')
        lines.append(f'                LongDescriptionKey = {cs_str("Comp." + cid + ".Long")},')
        lines.append(f'                Recommendation = RecommendationLevel.{c["rec"]},')
        lines.append(f'                Risk = RiskLevel.{c["risk"]},')
        lines.append(f'                Removal = RemovalSupport.{c["removal"]},')
        lines.append(f'                Restore = RestoreSupport.{c["restore"]},')

        scen = c["scenarios"]
        lines.append("                UserScenarios = " + cs_arr(
            scen, lambda s: f"ComponentScenario.{s}", "ComponentScenario") + ",")

        lines.append("                KeepIf = " + cs_arr([f"Comp.{cid}.KeepIf"], cs_str, "string") + ",")
        lines.append("                RemoveIf = " + cs_arr([f"Comp.{cid}.RemoveIf"], cs_str, "string") + ",")
        lines.append("                KnownImpact = " + cs_arr([f"Comp.{cid}.Impact"], cs_str, "string") + ",")

        deps = c["deps"]
        if deps:
            dep_items = []
            for (to_id, rel, reason) in deps:
                dep_items.append(
                    "new ComponentDependency { "
                    f"ToId = {cs_str(to_id)}, Relation = DependencyRelation.{rel}, "
                    f"Reason = {cs_str(reason)} }}")
            lines.append("                Dependencies = new[] { " + ", ".join(dep_items) + " },")
        else:
            lines.append("                Dependencies = new ComponentDependency[0],")

        lines.append("                Conflicts = new string[0],")

        tgt_items = []
        for (cat, match, pat, note) in c["targets"]:
            note_part = f", Note = {cs_str(note)}" if note else ""
            tgt_items.append(
                "new TechnicalTarget { "
                f"Category = ComponentCategory.{cat}, Match = MatchMethod.{match}, "
                f"Pattern = {cs_str(pat)}{note_part} }}")
        lines.append("                TechnicalTargets = new[] { " + ", ".join(tgt_items) + " },")

        lines.append("                CompatibilityRules = new[] { new CompatibilityRule")
        lines.append("                {")
        lines.append('                    SupportedBuildMin = "22000",')
        lines.append('                    KnownOnBuilds = new[] { "26100" }')
        lines.append("                } },")

        # Provenance (FACT vs RECOMMENDATION), default empty.
        prov = c.get("prov")
        if prov:
            claims = []
            for i, p in enumerate(prov):
                kind = "KnowledgeClaimKind.Fact" if p["kind"] == "Fact" else "KnowledgeClaimKind.Recommendation"
                key = f"Comp.{cid}.Prov{i}"
                srcs = (
                    "new[] { new KnowledgeSource { "
                    f"SourceType = {SRC_TYPE[p['src']]}, SourceName = {cs_str(p['name'])}, "
                    f"Confidence = {CONF[p['conf']]}"
                    + (f", SourceReference = {cs_str(p['ref'])}" if p.get("ref") else "")
                    + " } }")
                claims.append(
                    "new KnowledgeClaim { "
                    f"Kind = {kind}, TextKey = {cs_str(key)}, Sources = {srcs} }}")
            lines.append("                Provenance = new[] { " + ", ".join(claims) + " },")
        else:
            lines.append("                Provenance = new KnowledgeClaim[0],")

        # Scenario recommendation overrides (Part I).
        scen_recs = c.get("scen")
        if scen_recs:
            recs = []
            for (sc, rec, _, _) in scen_recs:
                recs.append(
                    "new ScenarioRecommendation { "
                    f"Scenario = ComponentScenario.{sc}, Recommendation = RecommendationLevel.{rec}, "
                    f"ReasonKey = {cs_str('Comp.' + cid + '.Scen.' + sc)} }}")
            lines.append("                ScenarioRecommendations = new[] { " + ", ".join(recs) + " },")
        else:
            lines.append("                ScenarioRecommendations = new ScenarioRecommendation[0],")

        lines.append("                EstimatedSavingsBytes = 0,")
        lines.append("                SavingsConfidence = SavingsConfidence.None,")
        lines.append("                Tags = " + cs_arr([cid.lower()], cs_str, "string") + ",")
        lines.append("            },")
        lines.append("")

    lines.append("        };")
    lines.append("    }")
    lines.append("}")
    return "\n".join(lines)


def catalog_pairs():
    """Every (key, en, zh) pair the generator OWNS.

    This is the single source of truth for the key namespace. The resx
    insert/regeneration STRIPS every one of these keys before re-inserting,
    so re-running the generator is fully idempotent and never duplicates keys
    -- whether the prior copy lived inside a marked block or was committed
    inline in Stage 11.1.
    """
    pairs = list(SHARED)
    for c in COMPONENTS:
        cid = c["id"]
        pairs.append((f"Comp.{cid}.DisplayName", c["display"][0], c["display"][1]))
        pairs.append((f"Comp.{cid}.Short", c["short"][0], c["short"][1]))
        pairs.append((f"Comp.{cid}.Long", c["long"][0], c["long"][1]))
        pairs.append((f"Comp.{cid}.KeepIf", c["keepif"][0], c["keepif"][1]))
        pairs.append((f"Comp.{cid}.RemoveIf", c["removeif"][0], c["removeif"][1]))
        pairs.append((f"Comp.{cid}.Impact", c["impact"][0], c["impact"][1]))
        for i, p in enumerate(c.get("prov", []) or []):
            pairs.append((f"Comp.{cid}.Prov{i}", p["en"], p["zh"]))
        for (sc, _, reason_en, reason_zh) in (c.get("scen") or []):
            pairs.append((f"Comp.{cid}.Scen.{sc}", reason_en, reason_zh))
    return pairs


def resx_block():
    """All owned (key, en, zh) pairs -> (en_xml, zh_xml) data blocks."""
    pairs = catalog_pairs()
    en = []
    zh = []
    for key, en_v, zh_v in pairs:
        en.append(f'  <data name="{key}" xml:space="preserve"><value>{su.escape(en_v)}</value></data>')
        zh.append(f'  <data name="{key}" xml:space="preserve"><value>{su.escape(zh_v)}</value></data>')
    return "\n".join(en), "\n".join(zh)


def insert_resx(path, block):
    import re
    with open(path, "r", encoding="utf-8") as f:
        content = f.read()
    # The generator is the single owner of every key it emits. Strip any prior
    # marked block AND every owned key (whether committed inline or appended by
    # an earlier non-idempotent run) so re-running never duplicates keys.
    content = re.sub(r"<!-- WINFORGE_CATALOG_BLOCK_START -->.*?<!-- WINFORGE_CATALOG_BLOCK_END -->",
                     "", content, flags=re.DOTALL)
    for key, _, _ in catalog_pairs():
        content = re.sub(r"\s*<data name=\"" + re.escape(key) + r"\"[^>]*>.*?</data>",
                         "", content, flags=re.DOTALL)
    full = f"\n{BLOCK_START}\n{block}\n{BLOCK_END}\n"
    marker = "</root>"
    idx = content.rfind(marker)
    if idx < 0:
        raise RuntimeError("no </root> in " + path)
    content = content[:idx] + full + content[idx:]
    with open(path, "w", encoding="utf-8") as f:
        f.write(content)


def main():
    cs = gen_csharp()
    with open(CS_PATH, "w", encoding="utf-8") as f:
        f.write(cs)
    en_block, zh_block = resx_block()
    insert_resx(RESX_EN, en_block)
    insert_resx(RESX_ZH, zh_block)
    print(f"Wrote {CS_PATH}")
    print(f"Inserted {len(SHARED) + sum(6 + len(c.get('prov') or []) + len(c.get('scen') or []) for c in COMPONENTS)} keys into each resx.")
    print(f"Components: {len(COMPONENTS)}")


if __name__ == "__main__":
    main()
