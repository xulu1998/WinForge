#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
Phase 11.1 generator: produces the curated component catalog (C#) AND inserts
localized resx entries (en + zh-CN) so the two never drift.

Single source of truth for component ids, their human guidance, and the
localization keys. Run from the WinForge repo root:

    python3 .tmp/phase11/gen_catalog.py
"""
import os
import xml.sax.saxutils as su

REPO = os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__))))
CS_PATH = os.path.join(REPO, "src", "WinForge.Infrastructure", "ComponentIntelligence", "CuratedComponentCatalog.cs")
RESX_EN = os.path.join(REPO, "src", "WinForge.App", "Resources", "Strings.resx")
RESX_ZH = os.path.join(REPO, "src", "WinForge.App", "Resources", "Strings.zh-CN.resx")

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
        ],
        "rec": "OptionalRemove", "risk": "Low", "removal": "Supported", "restore": "Easy",
        "scenarios": ["Gaming", "XboxGamePass"],
        "deps": [],
        "display": ("Xbox", "Xbox"),
        "short": ("Xbox app and related gaming overlays/providers.", "Xbox 应用及相关的游戏覆盖层/提供程序。"),
        "long": ("The Xbox app plus its gaming overlay and identity provider. Relevant to Xbox Game "
                 "Pass and PC gaming. Removal is safe for users who do not game on Xbox.",
                 "Xbox 应用及其游戏覆盖层与身份提供程序。与 Xbox Game Pass 和 PC 游戏相关。不玩 Xbox 的用户可安全移除。"),
        "keepif": ("You use Xbox Game Pass or play Xbox titles on this PC.", "你使用 Xbox Game Pass 或在此电脑上游玩 Xbox 游戏。"),
        "removeif": ("You do not use Xbox apps or PC gaming.", "你不使用 Xbox 应用或 PC 游戏。"),
        "impact": ("Xbox app, game overlay and Xbox sign-in provider are removed. Non-gaming apps are unaffected.",
                   "Xbox 应用、游戏覆盖层与 Xbox 登录提供程序被移除；非游戏应用不受影响。"),
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


def gen_csharp():
    lines = []
    lines.append("// <auto-generated by .tmp/phase11/gen_catalog.py — Phase 11.1 curated catalog>")
    lines.append("// Do not edit by hand; edit the generator and re-run it.")
    lines.append("using System.Collections.Generic;")
    lines.append("using WinForge.Core.Models;")
    lines.append("using WinForge.Core.Services;")
    lines.append("")
    lines.append("namespace WinForge.Infrastructure.ComponentIntelligence;")
    lines.append("")
    lines.append("/// <summary>")
    lines.append("/// Initial curated component catalog (Stage 11.1). Maps a SMALL set of")
    lines.append("/// well-understood Windows components onto stable Microsoft package-family")
    lines.append("/// identifiers. Technical targets use Prefix matching against well-known inbox")
    lines.append("/// AppX family names (Microsoft-supported identifiers); a component only becomes")
    lines.append("/// Curated when a discovered item actually matches. Unknown is preferred over")
    lines.append("/// invented information — savings and many impact details are left Unknown.")
    lines.append("/// </summary>")
    lines.append("public sealed class CuratedComponentCatalog : IComponentCatalogProvider")
    lines.append("{")
    lines.append("    public IReadOnlyList<ComponentDefinition> GetDefinitions()")
    lines.append("    {")
    lines.append("        return new List<ComponentDefinition>")
    lines.append("        {")

    for c in COMPONENTS:
        lines.append("            new ComponentDefinition")
        lines.append("            {")
        lines.append(f'                Id = {cs_str(c["id"])},')
        lines.append(f'                Category = ComponentCategory.{c["category"]},')
        lines.append(f'                DisplayNameKey = {cs_str("Comp." + c["id"] + ".DisplayName")},')
        lines.append(f'                ShortDescriptionKey = {cs_str("Comp." + c["id"] + ".Short")},')
        lines.append(f'                LongDescriptionKey = {cs_str("Comp." + c["id"] + ".Long")},')
        lines.append(f'                Recommendation = RecommendationLevel.{c["rec"]},')
        lines.append(f'                Risk = RiskLevel.{c["risk"]},')
        lines.append(f'                Removal = RemovalSupport.{c["removal"]},')
        lines.append(f'                Restore = RestoreSupport.{c["restore"]},')

        scen = c["scenarios"]
        lines.append("                UserScenarios = " + cs_arr(
            scen, lambda s: f"ComponentScenario.{s}", "ComponentScenario") + ",")

        lines.append("                KeepIf = " + cs_arr(
            [f"Comp.{c['id']}.KeepIf"], cs_str, "string") + ",")
        lines.append("                RemoveIf = " + cs_arr(
            [f"Comp.{c['id']}.RemoveIf"], cs_str, "string") + ",")
        lines.append("                KnownImpact = " + cs_arr(
            [f"Comp.{c['id']}.Impact"], cs_str, "string") + ",")

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

        lines.append("                EstimatedSavingsBytes = 0,")
        lines.append("                SavingsConfidence = SavingsConfidence.None,")
        lines.append("                Tags = " + cs_arr([c["id"].lower()], cs_str, "string") + ",")
        lines.append("            },")
        lines.append("")

    lines.append("        };")
    lines.append("    }")
    lines.append("}")
    return "\n".join(lines)


def resx_block():
    """All (key, en, zh) pairs -> (en_xml, zh_xml) data blocks."""
    pairs = list(SHARED)
    for c in COMPONENTS:
        cid = c["id"]
        pairs.append((f"Comp.{cid}.DisplayName", c["display"][0], c["display"][1]))
        pairs.append((f"Comp.{cid}.Short", c["short"][0], c["short"][1]))
        pairs.append((f"Comp.{cid}.Long", c["long"][0], c["long"][1]))
        pairs.append((f"Comp.{cid}.KeepIf", c["keepif"][0], c["keepif"][1]))
        pairs.append((f"Comp.{cid}.RemoveIf", c["removeif"][0], c["removeif"][1]))
        pairs.append((f"Comp.{cid}.Impact", c["impact"][0], c["impact"][1]))

    en = []
    zh = []
    for key, en_v, zh_v in pairs:
        en.append(f'  <data name="{key}" xml:space="preserve"><value>{su.escape(en_v)}</value></data>')
        zh.append(f'  <data name="{key}" xml:space="preserve"><value>{su.escape(zh_v)}</value></data>')
    return "\n".join(en), "\n".join(zh)


def insert_resx(path, block):
    with open(path, "r", encoding="utf-8") as f:
        content = f.read()
    marker = "</root>"
    idx = content.rfind(marker)
    if idx < 0:
        raise RuntimeError("no </root> in " + path)
    new_content = content[:idx] + block + "\n" + content[idx:]
    with open(path, "w", encoding="utf-8") as f:
        f.write(new_content)


def main():
    cs = gen_csharp()
    with open(CS_PATH, "w", encoding="utf-8") as f:
        f.write(cs)
    en_block, zh_block = resx_block()
    insert_resx(RESX_EN, en_block)
    insert_resx(RESX_ZH, zh_block)
    print(f"Wrote {CS_PATH}")
    print(f"Inserted {len(SHARED) + len(COMPONENTS)*6} keys into each resx.")
    print(f"Components: {len(COMPONENTS)}")


if __name__ == "__main__":
    main()
