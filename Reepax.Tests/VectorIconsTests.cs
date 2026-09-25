using System;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using Xunit;

namespace Reepax.Tests;

public class VectorIconsTests
{
    [Fact]
    public void AllReferencedVectorIcons_ExistInVectorIconsXaml()
    {
        // 1. Locate Reepax project directory
        string baseDir = AppDomain.CurrentDomain.BaseDirectory;
        string projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\..\Reepax"));
        if (!Directory.Exists(projectDir))
        {
            projectDir = Path.GetFullPath(Path.Combine(baseDir, @"..\..\..\Reepax"));
        }

        Assert.True(Directory.Exists(projectDir), $"Reepax project directory not found at '{projectDir}'");

        string vectorIconsFile = Path.Combine(projectDir, "Themes", "VectorIcons.xaml");
        Assert.True(File.Exists(vectorIconsFile), $"VectorIcons.xaml not found at '{vectorIconsFile}'");

        // 2. Extract all defined icon keys
        var definedIcons = new HashSet<string>(StringComparer.Ordinal);
        var keyRegex = new Regex(@"x:Key=""(Icon[a-zA-Z0-9]+)""", RegexOptions.Compiled);
        foreach (var line in File.ReadAllLines(vectorIconsFile))
        {
            var match = keyRegex.Match(line);
            if (match.Success)
            {
                definedIcons.Add(match.Groups[1].Value);
            }
        }

        Assert.NotEmpty(definedIcons);

        // 3. Scan all XAML files in Reepax project for {StaticResource Icon...} usages
        var xamlFiles = Directory.GetFiles(projectDir, "*.xaml", SearchOption.AllDirectories);
        var missingIcons = new List<string>();
        var resourceRegex = new Regex(@"\{StaticResource\s+(Icon[a-zA-Z0-9]+)\}", RegexOptions.Compiled);

        foreach (var file in xamlFiles)
        {
            // Skip obj/ and bin/ folders
            if (file.Contains(@"\bin\") || file.Contains(@"\obj/"))
                continue;

            var content = File.ReadAllText(file);
            var matches = resourceRegex.Matches(content);
            foreach (Match m in matches)
            {
                var iconName = m.Groups[1].Value;
                if (!definedIcons.Contains(iconName))
                {
                    missingIcons.Add($"{iconName} (in {Path.GetFileName(file)})");
                }
            }
        }

        Assert.Empty(missingIcons);
    }
}
