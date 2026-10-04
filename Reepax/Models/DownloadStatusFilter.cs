using System.Text.Json.Serialization;

namespace Reepax.Models;

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum DownloadStatusFilter
{
    All,
    Running,
    Paused,
    Completed,
    Failed
}
