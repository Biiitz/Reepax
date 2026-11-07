using Reepax.Helpers;
using Reepax.Models;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using Reepax.ViewModels;
using Reepax.Views;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData("en")]
    [InlineData("de")]
    public void SoundSettings_AlignControlsAndPreserveSettingsWhenCollapsed(string language)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var vm = new MainViewModel();
            var previous = (vm.SelectedLanguage, vm.EnableCompletionSound, vm.EnableErrorSound,
                vm.CompletionSoundVolume, vm.ErrorSoundVolume, vm.IsCompletionSoundEntirePackage,
                vm.SelectedCompletionSound, vm.SelectedErrorSound);
            var shutdownMode = Application.Current.ShutdownMode;
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var view = new SettingsView { DataContext = vm };
            var host = new Window { Content = view, Width = 900, Height = 680,
                Left = -10000, Top = -10000, ShowActivated = false, ShowInTaskbar = false };
            try
            {
                vm.SelectedLanguage = language;
                vm.SelectedCompletionSound = vm.SelectedErrorSound = "sounds/1.wav";
                vm.IsCompletionSoundEntirePackage = true;
                vm.EnableCompletionSound = vm.EnableErrorSound = true;
                vm.CompletionSoundVolume = 37;
                vm.ErrorSoundVolume = 33;
                host.Show();
                RenderSettings(view, "SoundSettings");
                var completion = (Button)view.FindName("CompletionSoundButton");
                var failure = (Button)view.FindName("ErrorSoundButton");
                Assert.Equal(completion.TranslatePoint(new Point(), view).X, failure.TranslatePoint(new Point(), view).X, precision: 1);
                var completionArea = (Border)view.FindName("CompletionSoundVolumeContainer");
                var failureArea = (Border)view.FindName("ErrorSoundVolumeContainer");
                Assert.Equal(Visibility.Visible, completionArea.Visibility);
                Assert.Equal(Visibility.Visible, failureArea.Visibility);
                Assert.True(completionArea.ActualWidth > 300);
                var sliders = Descendants<Slider>(completionArea).Concat(Descendants<Slider>(failureArea)).ToArray();
                Assert.Equal(2, sliders.Length);
                sliders[0].SetCurrentValue(RangeBase.ValueProperty, 42.0);
                sliders[1].SetCurrentValue(RangeBase.ValueProperty, 28.0);
                Assert.Equal(42, vm.CompletionSoundVolume);
                Assert.Equal(28, vm.ErrorSoundVolume);
                var trigger = (SingleChoiceDropdown)view.FindName("CompletionSoundTriggerSelector");
                trigger.SetCurrentValue(SingleChoiceDropdown.SelectedValueProperty, CompletionSoundTrigger.IndividualFiles);
                Assert.True(vm.IsCompletionSoundIndividualFiles);
                host.Width = 1280;
                RenderSettings(view, "SoundSettingsWide", width: 1280);
                double triggerCenter = trigger.TranslatePoint(new Point(0, trigger.ActualHeight / 2), view).Y;
                double selectorCenter = completion.TranslatePoint(new Point(0, completion.ActualHeight / 2), view).Y;
                Assert.InRange(Math.Abs(triggerCenter - selectorCenter), 0, 1);
                Assert.True(trigger.TranslatePoint(new Point(), view).X < completion.TranslatePoint(new Point(), view).X);
                host.Width = 900;
                vm.EnableCompletionSound = vm.EnableErrorSound = false;
                RenderSettings(view, "SoundSettingsCollapsed");
                Assert.Equal(Visibility.Collapsed, completionArea.Visibility);
                Assert.Equal(Visibility.Collapsed, failureArea.Visibility);
                vm.EnableCompletionSound = vm.EnableErrorSound = true;
                RenderSettings(view, "SoundSettingsExpanded");
                Assert.Equal(42, vm.CompletionSoundVolume);
                Assert.Equal(28, vm.ErrorSoundVolume);
                Assert.True(vm.IsCompletionSoundIndividualFiles);
            }
            finally
            {
                host.Close();
                vm.SelectedLanguage = previous.SelectedLanguage;
                vm.EnableCompletionSound = previous.EnableCompletionSound;
                vm.EnableErrorSound = previous.EnableErrorSound;
                vm.CompletionSoundVolume = previous.CompletionSoundVolume;
                vm.ErrorSoundVolume = previous.ErrorSoundVolume;
                vm.IsCompletionSoundEntirePackage = previous.IsCompletionSoundEntirePackage;
                vm.SelectedCompletionSound = previous.SelectedCompletionSound;
                vm.SelectedErrorSound = previous.SelectedErrorSound;
                Application.Current.ShutdownMode = shutdownMode;
            }
        });
    }
}
