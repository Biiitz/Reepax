using System;
using System.Threading.Tasks;
using System.Windows;
using CommunityToolkit.Mvvm.Input;
using Reepax.Models;
using Reepax.Services.Extractor;

namespace Reepax.ViewModels;

public partial class MainViewModel
{
    internal Func<DownloadPackage, bool> ShowArchivePasswords { get; set; } = package =>
        Application.Current?.MainWindow is Window { IsVisible: true } owner &&
        new Views.ArchivePasswordsDialog(package) { Owner = owner }.ShowDialog() == true;

    [RelayCommand]
    private async Task ManageArchivePasswordsAsync(DownloadPackage? package)
    {
        package ??= SelectedPackage;
        if (package == null || !ArchiveExtractionService.Instance.CanExtractPackage(package, ignorePasswordWait: true)) return;
        if (ShowArchivePasswords(package)) await ExtractPackageAsync(package);
    }
}
