using Reepax.Services.Navigation;
using Reepax.Services.Storage;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public class NavigationHistoryTests
{
    [Fact]
    public void InitialState_CannotGoBackOrForward()
    {
        var manager = new NavigationHistoryManager();
        Assert.False(manager.CanGoBack);
        Assert.False(manager.CanGoForward);
        Assert.Null(manager.CurrentState);

        // Recording initial state
        manager.Record(new NavigationState(AppMainTab.Downloads, SettingsCategory.General));
        Assert.False(manager.CanGoBack);
        Assert.False(manager.CanGoForward);
        Assert.Equal(new NavigationState(AppMainTab.Downloads, SettingsCategory.General), manager.CurrentState);
    }

    [Fact]
    public void Record_DifferentState_EnablesGoBack()
    {
        var manager = new NavigationHistoryManager();
        manager.Record(new NavigationState(AppMainTab.Downloads, SettingsCategory.General));
        manager.Record(new NavigationState(AppMainTab.Settings, SettingsCategory.General));

        Assert.True(manager.CanGoBack);
        Assert.False(manager.CanGoForward);
        Assert.Equal(new NavigationState(AppMainTab.Settings, SettingsCategory.General), manager.CurrentState);
    }

    [Fact]
    public void Record_DuplicateConsecutiveState_IsIgnored()
    {
        var manager = new NavigationHistoryManager();
        manager.Record(new NavigationState(AppMainTab.Downloads, SettingsCategory.General));
        manager.Record(new NavigationState(AppMainTab.Downloads, SettingsCategory.General));
        manager.Record(new NavigationState(AppMainTab.Downloads, SettingsCategory.General));

        Assert.False(manager.CanGoBack);
        Assert.False(manager.CanGoForward);
    }

    [Fact]
    public void GoBack_And_GoForward_NavigateCorrectly()
    {
        var manager = new NavigationHistoryManager();
        var s1 = new NavigationState(AppMainTab.Downloads, SettingsCategory.General);
        var s2 = new NavigationState(AppMainTab.Settings, SettingsCategory.General);
        var s3 = new NavigationState(AppMainTab.Settings, SettingsCategory.Shortcuts);

        manager.Record(s1);
        manager.Record(s2);
        manager.Record(s3);

        Assert.True(manager.CanGoBack);
        Assert.False(manager.CanGoForward);
        Assert.Equal(s3, manager.CurrentState);

        // Step back to s2
        var back1 = manager.GoBack();
        Assert.Equal(s2, back1);
        Assert.Equal(s2, manager.CurrentState);
        Assert.True(manager.CanGoBack);
        Assert.True(manager.CanGoForward);

        // Step back to s1
        var back2 = manager.GoBack();
        Assert.Equal(s1, back2);
        Assert.Equal(s1, manager.CurrentState);
        Assert.False(manager.CanGoBack);
        Assert.True(manager.CanGoForward);

        // Can't step back further
        Assert.Null(manager.GoBack());

        // Step forward to s2
        var fwd1 = manager.GoForward();
        Assert.Equal(s2, fwd1);
        Assert.Equal(s2, manager.CurrentState);
        Assert.True(manager.CanGoBack);
        Assert.True(manager.CanGoForward);

        // Step forward to s3
        var fwd2 = manager.GoForward();
        Assert.Equal(s3, fwd2);
        Assert.Equal(s3, manager.CurrentState);
        Assert.True(manager.CanGoBack);
        Assert.False(manager.CanGoForward);

        // Can't step forward further
        Assert.Null(manager.GoForward());
    }

    [Fact]
    public void Branching_DiscardsForwardHistory()
    {
        var manager = new NavigationHistoryManager();
        var s1 = new NavigationState(AppMainTab.Downloads, SettingsCategory.General);
        var s2 = new NavigationState(AppMainTab.Settings, SettingsCategory.General);
        var s3 = new NavigationState(AppMainTab.Settings, SettingsCategory.Appearance);
        var s4 = new NavigationState(AppMainTab.Downloads, SettingsCategory.General);

        manager.Record(s1);
        manager.Record(s2);
        manager.Record(s3);

        // Go back to s2
        manager.GoBack();
        Assert.True(manager.CanGoForward);

        // Now branch by recording s4
        manager.Record(s4);
        Assert.True(manager.CanGoBack);
        Assert.False(manager.CanGoForward); // Forward to s3 must be gone
        Assert.Equal(s4, manager.CurrentState);

        // Going back should now lead to s2, then s1
        Assert.Equal(s2, manager.GoBack());
        Assert.Equal(s1, manager.GoBack());
        Assert.False(manager.CanGoBack);
    }

    [Fact]
    public void Capacity_IsBoundedToMaxHistory()
    {
        var manager = new NavigationHistoryManager();
        for (int i = 0; i < 70; i++)
        {
            var tab = (i % 2 == 0) ? AppMainTab.Downloads : AppMainTab.Settings;
            var cat = (SettingsCategory)(i % 5);
            manager.Record(new NavigationState(tab, cat));
        }

        // Count how many back steps we can take
        int backSteps = 0;
        while (manager.CanGoBack)
        {
            manager.GoBack();
            backSteps++;
        }

        // Max history is 50, so max back steps from index 49 is 49
        Assert.Equal(49, backSteps);
    }

    [Fact]
    public void MainViewModel_TracksNavigation_AndCommandsWork()
    {
        DownloadPersistenceService.IsTestEnvironment = true;
        var vm = new MainViewModel();

        // Initial state
        Assert.Equal(AppMainTab.Downloads, vm.SelectedMainTab);
        Assert.False(vm.CanGoBack);
        Assert.False(vm.CanGoForward);
        Assert.False(vm.GoBackCommand.CanExecute(null));
        Assert.False(vm.GoForwardCommand.CanExecute(null));

        // Switch to Settings (General)
        vm.SwitchToSettingsTabCommand.Execute(null);
        Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoForward);
        Assert.True(vm.GoBackCommand.CanExecute(null));

        // Switch to Shortcuts category
        vm.SelectSettingsCategoryCommand.Execute(SettingsCategory.Shortcuts);
        Assert.Equal(SettingsCategory.Shortcuts, vm.SelectedSettingsCategory);
        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoForward);

        // Go back (should restore General category in Settings)
        vm.GoBackCommand.Execute(null);
        Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
        Assert.Equal(SettingsCategory.General, vm.SelectedSettingsCategory);
        Assert.True(vm.CanGoBack);
        Assert.True(vm.CanGoForward);
        Assert.True(vm.GoForwardCommand.CanExecute(null));

        // Go back again (should restore Downloads tab)
        vm.GoBackCommand.Execute(null);
        Assert.Equal(AppMainTab.Downloads, vm.SelectedMainTab);
        Assert.False(vm.CanGoBack);
        Assert.True(vm.CanGoForward);

        // Go forward (should restore Settings General)
        vm.GoForwardCommand.Execute(null);
        Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
        Assert.Equal(SettingsCategory.General, vm.SelectedSettingsCategory);
        Assert.True(vm.CanGoBack);
        Assert.True(vm.CanGoForward);

        // Go forward again (should restore Settings Shortcuts)
        vm.GoForwardCommand.Execute(null);
        Assert.Equal(AppMainTab.Settings, vm.SelectedMainTab);
        Assert.Equal(SettingsCategory.Shortcuts, vm.SelectedSettingsCategory);
        Assert.True(vm.CanGoBack);
        Assert.False(vm.CanGoForward);
    }
}
