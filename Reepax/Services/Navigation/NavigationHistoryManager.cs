using System.Collections.Generic;
using Reepax.ViewModels;

namespace Reepax.Services.Navigation;

/// Represents a snapshot of the UI navigation state.
public sealed record NavigationState(AppMainTab Tab, SettingsCategory SettingsCategory);

/// Manages a browser-like forward/backward navigation history stack.
public class NavigationHistoryManager
{
    private readonly List<NavigationState> _history = new();
    private int _currentIndex = -1;
    private const int MaxHistory = 50;

    /// Gets whether a previous navigation state is available.
    public bool CanGoBack => _currentIndex > 0;

    /// Gets whether a forward navigation state is available.
    public bool CanGoForward => _currentIndex >= 0 && _currentIndex < _history.Count - 1;

    /// Gets the current active state in history, or null if empty.
    public NavigationState? CurrentState =>
        _currentIndex >= 0 && _currentIndex < _history.Count ? _history[_currentIndex] : null;

    /// Records a new navigation state. Truncates any forward history and ignores consecutive duplicates.
    public void Record(NavigationState state)
    {
        // Ignore consecutive identical state
        if (_currentIndex >= 0 && _currentIndex < _history.Count && _history[_currentIndex] == state)
        {
            return;
        }

        // If we navigated while in the middle of history, discard forward branch
        if (_currentIndex < _history.Count - 1)
        {
            _history.RemoveRange(_currentIndex + 1, _history.Count - (_currentIndex + 1));
        }

        _history.Add(state);

        // Keep stack bounded
        if (_history.Count > MaxHistory)
        {
            _history.RemoveAt(0);
        }

        _currentIndex = _history.Count - 1;
    }

    /// Navigates one step backward in history.
    public NavigationState? GoBack()
    {
        if (!CanGoBack) return null;
        _currentIndex--;
        return _history[_currentIndex];
    }


    /// Navigates one step forward in history.
    public NavigationState? GoForward()
    {
        if (!CanGoForward) return null;
        _currentIndex++;
        return _history[_currentIndex];
    }

    /// Clears the navigation history.
    public void Clear()
    {
        _history.Clear();
        _currentIndex = -1;
    }
}
