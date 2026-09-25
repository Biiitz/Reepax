using CommunityToolkit.Mvvm.ComponentModel;

namespace Reepax.Models;

public enum NextTaskStepState
{
    Pending,
    Running,
    Transitioning,
    Done
}

/// <summary>
/// A single post-download step of a package (e.g. "Extracting", "Delete archive").
/// Drives the animated step indicator in the package row.
/// </summary>
public partial class NextTaskStep : ObservableObject
{
    [ObservableProperty]
    private string _key = string.Empty;

    [ObservableProperty]
    private string _name = string.Empty;

    [ObservableProperty]
    private NextTaskStepState _state = NextTaskStepState.Pending;
}
