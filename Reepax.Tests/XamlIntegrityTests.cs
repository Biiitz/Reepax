using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class XamlIntegrityTests
{
    [Fact]
    public void PasswordTable_BindsStatisticsAndUsesOneGuardedEnterAction()
    {
        var document = XDocument.Load(Path.Combine(GetReepaxProjectDir(), "Views", "SettingsView.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        var input = document.Descendants().Single(element => (string?)element.Attribute(names + "Name") == "NewArchivePasswordTextBox");
        Assert.Equal("NewArchivePasswordTextBox_KeyDown", (string?)input.Attribute("KeyDown"));
        Assert.DoesNotContain(input.Descendants(), element => element.Name.LocalName == "KeyBinding");
        var add = document.Descendants().Single(element => (string?)element.Attribute("Command") == "{Binding AddArchivePasswordCommand}");
        Assert.Equal("{Binding CanAddArchivePassword}", (string?)add.Attribute("IsEnabled"));
        var table = document.Descendants().Single(element => (string?)element.Attribute("ItemsSource") == "{Binding ArchivePasswordEntries}");
        foreach (var binding in new[] { "Password", "AddedAtText", "UsageCount", "SuccessCount" })
            Assert.Contains(table.Descendants(), element => (string?)element.Attribute("Text") == "{Binding " + binding + "}");
        Assert.All(table.Descendants().Where(element => element.Name.LocalName == "Button"),
            button => Assert.Equal("{Binding Password}", (string?)button.Attribute("CommandParameter")));
    }

    [Fact]
    public void ToolbarAnalysis_ShowsPixelLogoAtExistingPosition()
    {
        var xaml = XDocument.Load(Path.Combine(GetReepaxProjectDir(), "MainWindow.xaml"));
        XNamespace names = "http://schemas.microsoft.com/winfx/2006/xaml";
        XNamespace helpers = "clr-namespace:Reepax.Helpers";
        var status = xaml.Descendants().Single(element => (string?)element.Attribute(names + "Name") == "ToolbarAnalysisStatus");
        Assert.Equal("14,0,0,0", (string?)status.Attribute("Margin"));
        Assert.Equal("{Binding IsBusy}", (string?)status.Attribute(helpers + "FadeAnimation.IsShown"));
        Assert.DoesNotContain(status.Descendants(), element => element.Name.LocalName == "ProgressBar");
        var loader = status.Descendants().Single(element => (string?)element.Attribute(names + "Name") == "MetadataAnalysisLoader");
        Assert.Equal(helpers + "PixelLogoLoader", loader.Name);
        Assert.Equal("{Binding IsAnalyzingMetadata}", (string?)loader.Attribute(helpers + "FadeAnimation.IsShown"));
        Assert.Equal("{Binding IsAnalyzingMetadata}", (string?)loader.Attribute("IsActive"));
        Assert.Same(loader, status.Elements().First());
        var text = Assert.Single(status.Elements().Where(element => element.Name.LocalName == "TextBlock"));
        Assert.Equal("{Binding BusyDisplayMessage}", (string?)text.Attribute("Text"));
        Assert.DoesNotContain(loader.Descendants(), element => element.Name.LocalName == "RotateTransform");
    }

    private static string GetReepaxProjectDir()
    {
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\Reepax"));
        if (!Directory.Exists(projectDir))
        {
            projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\Reepax"));
        }

        Assert.True(Directory.Exists(projectDir), $"Reepax project directory not found at '{projectDir}'");
        return projectDir;
    }

    [Fact]
    public void AllXamlEventHandlers_ExistInCodeBehind()
    {
        string projectDir = GetReepaxProjectDir();
        var xamlFiles = Directory.GetFiles(projectDir, "*.xaml", SearchOption.AllDirectories);

        var eventRegex = new Regex(
            @"\b(Click|TextChanged|PreviewMouseWheel|MouseWheel|PreviewMouseDown|MouseDown|PreviewMouseUp|MouseUp|KeyDown|KeyUp|SelectionChanged|Checked|Unchecked|Closed|Loaded|Unloaded|DragEnter|DragLeave|Drop|DragOver)=""([a-zA-Z0-9_]+)""",
            RegexOptions.Compiled);

        var missingHandlers = new List<string>();

        foreach (var xamlPath in xamlFiles)
        {
            if (xamlPath.Contains(@"\bin\") || xamlPath.Contains(@"\obj\"))
                continue;

            var document = XDocument.Load(xamlPath);
            string? className = (string?)document.Root?.Attribute(XName.Get("Class", "http://schemas.microsoft.com/winfx/2006/xaml"));
            if (className == null)
                continue;

            string xamlContent = File.ReadAllText(xamlPath);
            var codeBehind = typeof(App).Assembly.GetType(className);
            Assert.NotNull(codeBehind);
            var handlers = codeBehind!.GetMethods(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)
                .Select(method => method.Name).ToHashSet(StringComparer.Ordinal);

            var matches = eventRegex.Matches(xamlContent);
            foreach (Match m in matches)
            {
                string eventName = m.Groups[1].Value;
                string handlerName = m.Groups[2].Value;

                if (!handlers.Contains(handlerName))
                {
                    missingHandlers.Add($"{handlerName} ({eventName}) in {Path.GetFileName(xamlPath)}");
                }
            }
        }

        Assert.True(missingHandlers.Count == 0,
            $"Missing event handlers in code-behind files:\n{string.Join("\n", missingHandlers)}");
    }

    [Fact]
    public void AllLocMarkupExtensions_ExistInLocalizationDictionaries()
    {
        string projectDir = GetReepaxProjectDir();
        var xamlFiles = Directory.GetFiles(projectDir, "*.xaml", SearchOption.AllDirectories);

        var locRegex = new Regex(@"\{loc:Loc\s+([a-zA-Z0-9_]+)\}", RegexOptions.Compiled);
        var missingKeys = new List<string>();
        var loc = LocalizationService.Instance;

        foreach (var xamlPath in xamlFiles)
        {
            if (xamlPath.Contains(@"\bin\") || xamlPath.Contains(@"\obj\"))
                continue;

            string xamlContent = File.ReadAllText(xamlPath);
            var matches = locRegex.Matches(xamlContent);

        try
        {
            foreach (Match m in matches)
            {
                string key = m.Groups[1].Value;

                // Test in English
                loc.CurrentLanguage = "en";
                string enVal = loc[key];
                if (string.IsNullOrWhiteSpace(enVal) || enVal == key)
                {
                    missingKeys.Add($"EN: '{key}' in {Path.GetFileName(xamlPath)}");
                }

                // Test in German
                loc.CurrentLanguage = "de";
                string deVal = loc[key];
                if (string.IsNullOrWhiteSpace(deVal) || deVal == key)
                {
                    missingKeys.Add($"DE: '{key}' in {Path.GetFileName(xamlPath)}");
                }
            }
        }
        finally
        {
            loc.CurrentLanguage = "en";
        }
        }

        Assert.True(missingKeys.Count == 0,
            $"Missing localization keys referenced in XAML:\n{string.Join("\n", missingKeys)}");
    }

    [Fact]
    public void AllCSharpLocCalls_ExistInLocalizationDictionaries()
    {
        string projectDir = GetReepaxProjectDir();
        var csFiles = Directory.GetFiles(projectDir, "*.cs", SearchOption.AllDirectories);

        var locRegex = new Regex(@"Loc\.(?:Get|Format)\s*\(\s*""([^""]+)""", RegexOptions.Compiled);
        var missingKeys = new List<string>();
        var loc = LocalizationService.Instance;

        try
        {
            foreach (var csPath in csFiles)
            {
                if (csPath.Contains(@"\bin\") || csPath.Contains(@"\obj\"))
                    continue;

                string csContent = File.ReadAllText(csPath);
                var matches = locRegex.Matches(csContent);

                foreach (Match m in matches)
                {
                    string key = m.Groups[1].Value;

                    loc.CurrentLanguage = "en";
                    string enVal = loc[key];
                    if (string.IsNullOrWhiteSpace(enVal) || enVal == key)
                    {
                        missingKeys.Add($"EN: '{key}' in {Path.GetFileName(csPath)}");
                    }

                    loc.CurrentLanguage = "de";
                    string deVal = loc[key];
                    if (string.IsNullOrWhiteSpace(deVal) || deVal == key)
                    {
                        missingKeys.Add($"DE: '{key}' in {Path.GetFileName(csPath)}");
                    }
                }
            }
        }
        finally
        {
            loc.CurrentLanguage = "en";
        }

        Assert.True(missingKeys.Count == 0,
            $"Missing localization keys referenced in C# files:\n{string.Join("\n", missingKeys)}");
    }

    [Fact]
    public void AllXamlFiles_AreValidXml()
    {
        string projectDir = GetReepaxProjectDir();
        var xamlFiles = Directory.GetFiles(projectDir, "*.xaml", SearchOption.AllDirectories);

        var parseErrors = new List<string>();

        foreach (var xamlPath in xamlFiles)
        {
            if (xamlPath.Contains(@"\bin\") || xamlPath.Contains(@"\obj\"))
                continue;

            try
            {
                XDocument.Load(xamlPath);
            }
            catch (Exception ex)
            {
                parseErrors.Add($"{Path.GetFileName(xamlPath)}: {ex.Message}");
            }
        }

        Assert.True(parseErrors.Count == 0,
            $"XML parse errors in XAML files:\n{string.Join("\n", parseErrors)}");
    }
}
