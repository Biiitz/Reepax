namespace Reepax.Models;

public enum DownloadOverviewScope { All, Filtered, Selected }

public sealed class DownloadOverviewSettings
{
    public const double DefaultHeight = 72;
    public bool IsShown { get; set; } = true;
    public bool IsExpanded { get; set; } = true;
    public double Height { get; set; } = DefaultHeight;
    public DownloadOverviewScope Scope { get; set; }
    public bool IncludeDisabled { get; set; }
    public List<string> VisibleMetrics { get; set; } = DefaultMetrics.ToList();

    public static IReadOnlyList<string> DefaultMetrics { get; } =
        new[] { "Files", "Size", "Downloaded", "Speed", "Eta", "Active", "Completed", "Failed" };
}
