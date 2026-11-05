using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using System.Xml.Linq;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Localization;
using Reepax.ViewModels;
using Xunit;

namespace Reepax.Tests;

public partial class DialogUiTests
{
    [Theory]
    [InlineData(ThemeCatalog.DefaultId)]
    [InlineData(ThemeCatalog.MacOSId)]
    public void NextTaskPresentation_LocalizesExistingStepsAndFadesOnlyCompletedStepsToTheAccent(string theme)
    {
        RunInSta(() =>
        {
            EnsureSettingsTheme();
            var model = new MainViewModel();
            var themes = ThemeService.Instance;
            var previous = (themes.CurrentTheme.Id, themes.CurrentAccentColorHex, model.SelectedLanguage, Application.Current.ShutdownMode);
            var package = new DownloadPackage
            {
                Name = "Sample", AutoExtractArchives = true, LowResourceExtraction = true,
                DeleteArchiveAfterExtraction = true, AutoPar2Repair = false
            };
            var host = new Window { Width = 700, Height = 140, ShowActivated = false,
                ShowInTaskbar = false, Left = -10000, Top = -10000 };
            Application.Current.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                model.SelectedLanguage = "en";
                themes.SelectTheme(theme, save: false);
                package.Items.Add(new DownloadItem { FileName = "sample.zip", Status = DownloadStatus.Paused });
                package.EnsureNextTaskSteps();
                package.NextTaskSteps.Single(step => step.Key == "Extract").State = NextTaskStepState.Running;
                package.NextTaskSteps.Single(step => step.Key == "Cleanup").State = NextTaskStepState.Done;
                model.Packages.Add(package);
                model.SelectedLanguage = "de";
                Assert.Equal("Entpacken (schonend)", package.NextTaskSteps.Single(step => step.Key == "Extract").Name);
                Assert.Equal(Loc.Get("NextTask_DeleteArchive"), package.NextTaskSteps.Single(step => step.Key == "Cleanup").Name);
                Assert.Equal(NextTaskStepState.Running, package.NextTaskSteps.Single(step => step.Key == "Extract").State);
                Assert.Equal(NextTaskStepState.Done, package.NextTaskSteps.Single(step => step.Key == "Cleanup").State);

                var document = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Reepax", "MainWindow.xaml"));
                var markup = new XElement(document.Descendants().Single(element => element.Name.LocalName == "ItemsControl" &&
                    (string?)element.Attribute("ItemsSource") == "{Binding NextTaskSteps}"));
                DownloadListVirtualizationTests.SetNamespaces(markup, document.Root!);
                var indicator = (ItemsControl)XamlReader.Parse(markup.ToString());
                indicator.DataContext = package;
                var surface = new Border { Background = (Brush)Application.Current.FindResource("BgCardBrush"), Padding = new Thickness(16), Child = indicator };
                host.Content = surface;
                host.Show();
                host.UpdateLayout();
                PumpSettingsAnimations(20);
                TextBlock Label(int index, string name)
                {
                    var presenter = Assert.IsType<ContentPresenter>(indicator.ItemContainerGenerator.ContainerFromIndex(index));
                    return Assert.IsType<TextBlock>(presenter.ContentTemplate.FindName(name, presenter));
                }
                var pending = Label(0, "StepPendingText");
                var completed = Label(0, "StepCompletedText");
                Assert.Equal("Entpacken (schonend)", pending.Text);
                Assert.StartsWith("- ", Label(1, "StepCompletedText").Text);
                Assert.Equal(((SolidColorBrush)Application.Current.FindResource("TextMutedBrush")).Color, ((SolidColorBrush)pending.Foreground).Color);
                Assert.Equal(1, pending.Opacity);
                Assert.Equal(0, completed.Opacity);
                Assert.Equal(1, Label(1, "StepCompletedText").Opacity);
                Assert.False(Label(1, "StepCompletedText").HasAnimatedProperties);
                double width = indicator.ActualWidth;
                package.SetNextTaskDone("Extract");
                if (SystemParameters.ClientAreaAnimation) Assert.True(completed.HasAnimatedProperties);
                PumpSettingsAnimations();
                Assert.Equal(0, pending.Opacity);
                Assert.Equal(1, completed.Opacity);
                Assert.Equal(width, indicator.ActualWidth);
                themes.SetAccentColor("#7C6EE6", save: false);
                host.UpdateLayout();
                PumpSettingsAnimations(20);
                foreach (int index in new[] { 0, 1 })
                    Assert.Equal(((SolidColorBrush)Application.Current.FindResource("AccentBlueBrush")).Color,
                        ((SolidColorBrush)Label(index, "StepCompletedText").Foreground).Color);
                RenderSettings(surface, "NextTaskSteps-Completed-" + theme, 700);

                package.LowResourceExtraction = false;
                package.EnsureNextTaskSteps();
                model.SelectedLanguage = "en";
                host.UpdateLayout();
                Assert.Equal("Extracting", Label(0, "StepCompletedText").Text);
                package.DeleteArchiveAfterExtraction = false;
                package.MoveArchiveToRecycleBin = true;
                package.EnsureNextTaskSteps();
                model.SelectedLanguage = "de";
                host.UpdateLayout();
                Assert.Equal("- In Papierkorb", Label(1, "StepCompletedText").Text);
                Assert.All(package.NextTaskSteps, step => Assert.Equal(NextTaskStepState.Done, step.State));
                package.ResetNextTasks();
                host.UpdateLayout();
                PumpSettingsAnimations();
                Assert.Equal(1, Label(0, "StepPendingText").Opacity);
                Assert.Equal(0, Label(0, "StepCompletedText").Opacity);
                var repair = new NextTaskStep { Key = "Par2", Name = Loc.Get("NextTask_Par2Repair") };
                package.NextTaskSteps.Insert(0, repair);
                host.UpdateLayout();
                Assert.Equal(repair.Name, Label(0, "StepPendingText").Text);
                Assert.StartsWith("- ", Label(1, "StepPendingText").Text);
                Assert.StartsWith("- ", Label(2, "StepPendingText").Text);
                package.NextTaskSteps.Remove(repair);
                host.UpdateLayout();
                Assert.Equal("Entpacken", Label(0, "StepPendingText").Text);
                RenderSettings(surface, "NextTaskSteps-Pending-" + theme, 700);
            }
            finally
            {
                host.Close();
                model.Packages.Remove(package);
                model.SelectedLanguage = previous.SelectedLanguage;
                Application.Current.ShutdownMode = previous.ShutdownMode;
                themes.SelectTheme(previous.Id, save: false);
                themes.SetAccentColor(previous.CurrentAccentColorHex, save: false);
            }
        });
    }
}
