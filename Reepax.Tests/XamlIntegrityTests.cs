using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Reepax.Services.Localization;
using Xunit;

namespace Reepax.Tests;

public class XamlIntegrityTests
{
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

            string csPath = Path.ChangeExtension(xamlPath, ".xaml.cs");
            if (!File.Exists(csPath))
                continue;

            string xamlContent = File.ReadAllText(xamlPath);
            string csContent = File.ReadAllText(csPath);

            var matches = eventRegex.Matches(xamlContent);
            foreach (Match m in matches)
            {
                string eventName = m.Groups[1].Value;
                string handlerName = m.Groups[2].Value;

                // Check that handler exists in the code-behind file
                if (!csContent.Contains(handlerName))
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

        Assert.True(missingKeys.Count == 0,
            $"Missing localization keys referenced in XAML:\n{string.Join("\n", missingKeys)}");
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
