using System;
using System.IO;
using System.Text;
using System.Text.Json;
using WinForge.Core.Compatibility;

namespace WinForge.Infrastructure.Compatibility;

/// <summary>
/// Writes a validation result as JSON + human-readable Markdown (Stage 13.24).
/// Output goes to <c>validation/&lt;target&gt;-&lt;date&gt;.json|.md</c> under the
/// supplied directory (default: repo <c>validation/</c>). Giant binary artifacts
/// are never written to Git — this is metadata only.
/// </summary>
public static class ValidationReportWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    /// <summary>Write a validation result; returns the .md path written.</summary>
    public static string Write(string outputDirectory, ValidationResult result)
    {
        Directory.CreateDirectory(outputDirectory);
        var stamp = result.Date.ToString("yyyyMMdd-HHmm");
        var safeId = Safe(result.TargetId);
        var basePath = Path.Combine(outputDirectory, $"{safeId}-{stamp}");
        var jsonPath = basePath + ".json";
        var mdPath = basePath + ".md";

        File.WriteAllText(jsonPath, JsonSerializer.Serialize(result, JsonOptions), new UTF8Encoding(false));
        File.WriteAllText(mdPath, RenderMarkdown(result), new UTF8Encoding(false));
        return mdPath;
    }

    private static string RenderMarkdown(ValidationResult result)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"# Validation Report — {result.TargetId}");
        sb.AppendLine();
        sb.AppendLine($"- **Evidence:** {result.Evidence}");
        sb.AppendLine($"- **Date:** {result.Date:yyyy-MM-dd HH:mm 'UTC'zzz}");
        sb.AppendLine($"- **WinForge:** {result.WinForgeVersion ?? "?"} ({result.WinForgeCommit ?? "?"})");
        if (result.SourceImageMetadata is not null) sb.AppendLine($"- **Source image:** {result.SourceImageMetadata}");
        if (result.SelectedIndex is not null) sb.AppendLine($"- **Selected index:** {result.SelectedIndex}");
        if (result.CustomizationProfile is not null) sb.AppendLine($"- **Profile:** {result.CustomizationProfile}");
        if (result.OperationsCount is not null) sb.AppendLine($"- **Operations:** {result.OperationsCount}");
        if (result.BuildIsoPath is not null) sb.AppendLine($"- **ISO:** {result.BuildIsoPath}");
        if (result.IsoSha256 is not null) sb.AppendLine($"- **ISO SHA-256:** {result.IsoSha256}");
        if (result.IsoSizeBytes is not null) sb.AppendLine($"- **ISO size:** {result.IsoSizeBytes:N0} bytes");
        sb.AppendLine();
        sb.AppendLine("## Pipeline results");
        sb.AppendLine();
        sb.AppendLine("| Phase | Result |");
        sb.AppendLine("| --- | --- |");
        foreach (var phase in Enum.GetValues<ValidationPhase>())
        {
            var passed = result.Phases.TryGetValue(phase, out var ok) ? ok : (bool?)null;
            var mark = passed switch
            {
                true => "PASS",
                false => "FAIL",
                null => "—",
            };
            sb.AppendLine($"| {phase} | {mark} |");
        }

        sb.AppendLine();
        sb.AppendLine($"**Overall: {(result.AllPhasesPassed ? "VALIDATED" : "NOT fully validated")}**");
        if (!string.IsNullOrWhiteSpace(result.Notes))
        {
            sb.AppendLine();
            sb.AppendLine("## Notes");
            sb.AppendLine();
            sb.AppendLine(result.Notes);
        }

        return sb.ToString();
    }

    private static string Safe(string id)
    {
        var sb = new StringBuilder();
        foreach (var c in id)
        {
            if (char.IsLetterOrDigit(c) || c == '-' || c == '_')
            {
                sb.Append(c);
            }
        }

        return sb.Length == 0 ? "validation" : sb.ToString();
    }
}
