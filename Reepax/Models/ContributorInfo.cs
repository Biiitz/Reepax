namespace Reepax.Models;

/// <summary>
/// Represents an official GitHub contributor featured in the in-app Contributors list.
/// </summary>
public class ContributorInfo
{
    public string Username { get; set; } = string.Empty;
    public string ProfileUrl { get; set; } = string.Empty;
    public bool HasCrown { get; set; }
}
