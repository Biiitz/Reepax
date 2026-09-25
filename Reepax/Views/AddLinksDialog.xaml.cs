using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using Reepax.Models;
using Reepax.Services;
using Reepax.Services.Extractor;
using Reepax.Services.Storage;
using Reepax.ViewModels;

namespace Reepax.Views;

public partial class AddLinksDialog : Window
{
    public string EnteredText { get; private set; } = string.Empty;
    public string CustomPackageName { get; private set; } = string.Empty;
    public string CustomDownloadDirectory { get; private set; } = string.Empty;
    public bool AutoExtractArchives { get; private set; } = false;
    public bool LowResourceExtraction { get; private set; } = false;
    public bool DeleteArchiveAfterExtraction { get; private set; } = false;
    public bool MoveArchiveToRecycleBin { get; private set; } = false;
    public bool AutoResolveHostLinks { get; private set; } = false;

    private bool _isPackageNameUserEdited;
    private bool _isUpdatingPackageNameInternally;

    private readonly DownloadPackage? _editingPackage;
    private readonly bool _isAddingLinksToExisting;
    private readonly bool _isLockedDueToCompletedExtraction;

    public bool IsEditMode => _editingPackage != null && !_isAddingLinksToExisting;
    public bool IsAddingLinksToExisting => _isAddingLinksToExisting;
    public DownloadPackage? EditingPackage => _editingPackage;

    public AddLinksDialog(string? initialText = null)
        : this(initialText, null, false)
    {
    }

    public AddLinksDialog(DownloadPackage editingPackage)
        : this(null, editingPackage, false)
    {
    }

    public AddLinksDialog(string? initialText, DownloadPackage? editingPackage, bool isAddingLinksToExisting = false)
    {
        _editingPackage = editingPackage;
        _isAddingLinksToExisting = isAddingLinksToExisting;
        InitializeComponent();
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);

        bool isPackageCompletedOrExtracted = _editingPackage != null &&
            (_editingPackage.CheckIsFullyCompleted() ||
             _editingPackage.Status == DownloadStatus.Completed ||
             _editingPackage.NextTaskSteps.Any(s => s.Key == "Extract" && s.State == NextTaskStepState.Done));
        _isLockedDueToCompletedExtraction = isPackageCompletedOrExtracted;

        if (_editingPackage != null)
        {
            if (_isAddingLinksToExisting)
            {
                Title = Services.Localization.Loc.Format("Dialog_AddLinksToPackageTitle", _editingPackage.Name);
                AddButton.Content = Services.Localization.Loc.Get("Menu_AddLinksToPackage").TrimEnd('.').Trim();

                // Lock package name & path to existing package
                PackageNameTextBox.Text = _editingPackage.Name;
                PackageNameTextBox.IsEnabled = false;
                PackageNameTextBox.ToolTip = Services.Localization.Loc.Get("Dialog_PackageNameLocked_ToolTip");

                DownloadPathTextBox.Text = _editingPackage.SaveDirectory;
                DownloadPathTextBox.IsEnabled = false;
                DownloadPathTextBox.ToolTip = Services.Localization.Loc.Get("Dialog_DownloadPathLocked_ToolTip");
                BrowseFolderButton.IsEnabled = false;
                BrowseFolderButton.ToolTip = Services.Localization.Loc.Get("Dialog_DownloadPathLocked_ToolTip");

                if (!string.IsNullOrWhiteSpace(initialText))
                {
                    LinksTextBox.Text = LinkExtractor.NormalizeInputText(initialText);
                }
                else
                {
                    var cbText = GetClipboardLinksText();
                    if (!string.IsNullOrWhiteSpace(cbText))
                    {
                        LinksTextBox.Text = cbText;
                    }
                }
            }
            else
            {
                Title = Services.Localization.Loc.Format("Dialog_EditPackageTitle", _editingPackage.Name);
                AddButton.Content = Services.Localization.Loc.Get("Dialog_Button_SavePackage");

                // Populate URLs from package items
                var urls = _editingPackage.Items
                    .Select(i => !string.IsNullOrWhiteSpace(i.OriginalUrl) ? i.OriginalUrl : i.DirectDownloadUrl)
                    .Where(u => !string.IsNullOrWhiteSpace(u));
                LinksTextBox.Text = string.Join(Environment.NewLine, urls);

                DownloadPathTextBox.Text = _editingPackage.SaveDirectory;
                PackageNameTextBox.Text = _editingPackage.Name;
            }

            AutoExtractCheckBox.IsChecked = _editingPackage.AutoExtractArchives;
            if (LowResourceContainer != null)
            {
                LowResourceContainer.IsEnabled = _editingPackage.AutoExtractArchives;
                LowResourceCheckBox.IsChecked = _editingPackage.LowResourceExtraction;
            }
            if (DeleteArchivesCheckBox != null)
            {
                DeleteArchivesCheckBox.IsEnabled = _editingPackage.AutoExtractArchives;
                DeleteArchivesCheckBox.IsChecked = _editingPackage.DeleteArchiveAfterExtraction;
            }
            if (RecycleArchivesCheckBox != null)
            {
                RecycleArchivesCheckBox.IsEnabled = _editingPackage.AutoExtractArchives;
                RecycleArchivesCheckBox.IsChecked = _editingPackage.MoveArchiveToRecycleBin;
            }

            if (_isLockedDueToCompletedExtraction)
            {
                var lockedToolTip = Services.Localization.Loc.Get("Dialog_AutoExtractAlreadyDone_ToolTip");
                AutoExtractCheckBox.IsEnabled = false;
                AutoExtractCheckBox.ToolTip = lockedToolTip;
                if (LowResourceContainer != null)
                {
                    LowResourceContainer.IsEnabled = false;
                }
                if (LowResourceCheckBox != null)
                {
                    LowResourceCheckBox.IsEnabled = false;
                    LowResourceCheckBox.ToolTip = lockedToolTip;
                }
                if (DeleteArchivesCheckBox != null)
                {
                    DeleteArchivesCheckBox.IsEnabled = false;
                    DeleteArchivesCheckBox.ToolTip = lockedToolTip;
                }
                if (RecycleArchivesCheckBox != null)
                {
                    RecycleArchivesCheckBox.IsEnabled = false;
                    RecycleArchivesCheckBox.ToolTip = lockedToolTip;
                }
            }
        }
        else
        {
            var initialDir = SettingsService.Instance.Settings.DefaultDownloadDirectory;
            if (string.IsNullOrWhiteSpace(initialDir))
            {
                initialDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
            DownloadPathTextBox.Text = initialDir;

            if (!string.IsNullOrWhiteSpace(initialText))
            {
                LinksTextBox.Text = LinkExtractor.NormalizeInputText(initialText);
            }
            else
            {
                var cbText = GetClipboardLinksText();
                if (!string.IsNullOrWhiteSpace(cbText))
                {
                    LinksTextBox.Text = cbText;
                }
            }

            var settings = SettingsService.Instance.Settings;

            if (settings.AutoExtractArchives)
            {
                AutoExtractCheckBox.IsChecked = true;
                if (LowResourceContainer != null)
                {
                    LowResourceContainer.IsEnabled = true;
                    LowResourceCheckBox.IsChecked = settings.LowResourceExtraction == true;
                }
                if (DeleteArchivesCheckBox != null) DeleteArchivesCheckBox.IsEnabled = true;
                if (RecycleArchivesCheckBox != null) RecycleArchivesCheckBox.IsEnabled = true;

                if (settings.DeleteArchiveAfterExtraction)
                {
                    if (DeleteArchivesCheckBox != null) DeleteArchivesCheckBox.IsChecked = true;
                    if (RecycleArchivesCheckBox != null) RecycleArchivesCheckBox.IsChecked = false;
                }
                else if (settings.MoveArchiveToRecycleBin)
                {
                    if (RecycleArchivesCheckBox != null) RecycleArchivesCheckBox.IsChecked = true;
                    if (DeleteArchivesCheckBox != null) DeleteArchivesCheckBox.IsChecked = false;
                }
            }
        }

        UpdateSummary();
    }

    private static string? GetClipboardLinksText()
    {
        try
        {
            string? clipboardText = null;
            if (Clipboard.ContainsData(DataFormats.Html))
            {
                clipboardText = Clipboard.GetData(DataFormats.Html) as string;
            }
            if (string.IsNullOrWhiteSpace(clipboardText) && Clipboard.ContainsText())
            {
                clipboardText = Clipboard.GetText();
            }

            if (!string.IsNullOrWhiteSpace(clipboardText))
            {
                var extracted = LinkExtractor.ExtractLinks(clipboardText);
                if (extracted.Count > 0)
                {
                    return LinkExtractor.NormalizeInputText(clipboardText);
                }
            }
        }
        catch { }

        return null;
    }

    private void AutoExtractCheckBox_CheckedChanged(object sender, RoutedEventArgs e)
    {
        if (_isLockedDueToCompletedExtraction)
            return;
        bool isExtractActive = AutoExtractCheckBox.IsChecked == true;
        if (LowResourceContainer != null)
        {
            LowResourceContainer.IsEnabled = isExtractActive;
            if (!isExtractActive)
            {
                LowResourceCheckBox.IsChecked = false;
            }
        }

        if (DeleteArchivesCheckBox != null)
        {
            DeleteArchivesCheckBox.IsEnabled = isExtractActive;
            if (!isExtractActive)
            {
                DeleteArchivesCheckBox.IsChecked = false;
            }
        }

        if (RecycleArchivesCheckBox != null)
        {
            RecycleArchivesCheckBox.IsEnabled = isExtractActive;
            if (!isExtractActive)
            {
                RecycleArchivesCheckBox.IsChecked = false;
            }
        }
    }

    private void DeleteArchivesCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (DeleteArchivesCheckBox?.IsChecked == true && RecycleArchivesCheckBox != null)
        {
            RecycleArchivesCheckBox.IsChecked = false;
        }
    }

    private void RecycleArchivesCheckBox_Checked(object sender, RoutedEventArgs e)
    {
        if (RecycleArchivesCheckBox?.IsChecked == true && DeleteArchivesCheckBox != null)
        {
            DeleteArchivesCheckBox.IsChecked = false;
        }
    }

    private void LinksTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSummary();
    }

    /// <summary>
    /// Appends additional text (e.g. from repeated drag & drop while dialog is open)
    /// to the link textbox.
    /// </summary>
    public void AppendText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var normalized = LinkExtractor.NormalizeInputText(text);
        if (string.IsNullOrWhiteSpace(LinksTextBox.Text))
        {
            LinksTextBox.Text = normalized;
        }
        else
        {
            LinksTextBox.Text = LinksTextBox.Text.TrimEnd() + Environment.NewLine + normalized;
        }
    }

    private void PackageNameTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_isUpdatingPackageNameInternally)
        {
            _isPackageNameUserEdited = !string.IsNullOrWhiteSpace(PackageNameTextBox.Text);
        }

        if (PackageNameWatermark != null)
        {
            PackageNameWatermark.Visibility = string.IsNullOrEmpty(PackageNameTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void DownloadPathTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        UpdateSummary();
    }

    private void BrowseFolder_Click(object sender, RoutedEventArgs e)
    {
        if (_isAddingLinksToExisting)
            return;

        var current = DownloadPathTextBox.Text;
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = Services.Localization.Loc.Get("Dialog_SelectTargetDirTitle"),
            InitialDirectory = Directory.Exists(current) ? current : SettingsService.Instance.Settings.DefaultDownloadDirectory
        };
        if (dlg.ShowDialog(this) == true)
        {
            DownloadPathTextBox.Text = dlg.FolderName;
            UpdateSummary();
        }
    }

    private void UpdateSummary()
    {
        if (LinksWatermark != null)
        {
            LinksWatermark.Visibility = string.IsNullOrEmpty(LinksTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }
        if (PackageNameWatermark != null)
        {
            PackageNameWatermark.Visibility = string.IsNullOrEmpty(PackageNameTextBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        }

        var text = LinksTextBox.Text;
        var links = LinkExtractor.ExtractLinks(text);
        ExtractionSummaryText.Text = Services.Localization.Loc.Format("AddLinks_LinksDetectedSummary", links.Count);

        // Smart Update detection:
        bool isUpdate = UpdateDetector.IsUpdate(PackageNameTextBox.Text) ||
                        links.Any(l => UpdateDetector.IsUpdate(l.RawFileName) || UpdateDetector.IsUpdate(l.ContextTitle) || UpdateDetector.IsUpdate(l.Url)) ||
                        (!string.IsNullOrWhiteSpace(text) && UpdateDetector.IsUpdate(text));

        // Prefill package name if update detected and user hasn't manually typed a custom name
        if (isUpdate && !_isPackageNameUserEdited && _editingPackage == null)
        {
            var updateSource = links.Select(l => l.ContextTitle).FirstOrDefault(t => !string.IsNullOrWhiteSpace(t) && UpdateDetector.IsUpdate(t))
                               ?? links.Select(l => l.RawFileName).FirstOrDefault(f => !string.IsNullOrWhiteSpace(f) && UpdateDetector.IsUpdate(f))
                               ?? links.Select(l => l.Url).FirstOrDefault(u => !string.IsNullOrWhiteSpace(u) && UpdateDetector.IsUpdate(u))
                               ?? text;

            var updatePkgName = UpdateDetector.GetUpdatePackageName(updateSource);
            if (!string.IsNullOrWhiteSpace(updatePkgName) && updatePkgName != "Game - Updates" && PackageNameTextBox.Text != updatePkgName)
            {
                _isUpdatingPackageNameInternally = true;
                PackageNameTextBox.Text = updatePkgName;
                _isUpdatingPackageNameInternally = false;
            }
        }

        var downloadDir = DownloadPathTextBox.Text;
        if (string.IsNullOrWhiteSpace(downloadDir))
        {
            downloadDir = SettingsService.Instance.Settings.DefaultDownloadDirectory;
            if (string.IsNullOrWhiteSpace(downloadDir))
            {
                downloadDir = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            }
        }

        // Hardware recommendation for low-resource extraction
        var isLowResourceRecommended = DriveHardwareDetector.IsLowResourceRecommended(downloadDir);
        RecommendedText.Visibility = isLowResourceRecommended ? Visibility.Visible : Visibility.Collapsed;

        if (links.Count > 0)
        {
            AddButton.IsEnabled = true;
        }
        else
        {
            AddButton.IsEnabled = false;
        }
    }

    private void PasteClipboard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string? text = null;
            if (Clipboard.ContainsData(DataFormats.Html))
            {
                text = Clipboard.GetData(DataFormats.Html) as string;
            }
            if (string.IsNullOrWhiteSpace(text) && Clipboard.ContainsText())
            {
                text = Clipboard.GetText();
            }

            if (!string.IsNullOrWhiteSpace(text))
            {
                LinksTextBox.Text = LinkExtractor.NormalizeInputText(text);
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                Services.Localization.Loc.Format("Dialog_ClipboardReadErrorMessage", ex.Message), 
                Services.Localization.Loc.Get("Common_Error"), 
                MessageBoxButton.OK, 
                MessageBoxImage.Warning);
        }
    }

    private void AddLinks_Click(object sender, RoutedEventArgs e)
    {
        EnteredText = LinksTextBox.Text;
        CustomPackageName = PackageNameTextBox.Text.Trim();
        CustomDownloadDirectory = DownloadPathTextBox.Text.Trim();
        AutoExtractArchives = AutoExtractCheckBox.IsChecked == true;
        LowResourceExtraction = LowResourceCheckBox.IsChecked == true;
        DeleteArchiveAfterExtraction = DeleteArchivesCheckBox.IsChecked == true;
        MoveArchiveToRecycleBin = RecycleArchivesCheckBox.IsChecked == true;

        bool isUpdate = UpdateDetector.IsUpdate(CustomPackageName) ||
                        UpdateDetector.IsUpdate(EnteredText);

        AutoResolveHostLinks = false;

        DialogResult = true;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        ThemeService.ApplyDarkTitleBar(this, ThemeService.Instance.IsDarkMode);
        ThemeService.Instance.ThemeChanged += UpdateTitleBarTheme;

        try
        {
            var settings = SettingsService.Instance.Settings;
            if (settings.AddLinksWindowWidth > 300) Width = settings.AddLinksWindowWidth;
            if (settings.AddLinksWindowHeight > 200) Height = settings.AddLinksWindowHeight;

            if (settings.AddLinksWindowLeft.HasValue && settings.AddLinksWindowTop.HasValue)
            {
                var virtualLeft = SystemParameters.VirtualScreenLeft;
                var virtualTop = SystemParameters.VirtualScreenTop;
                var virtualWidth = SystemParameters.VirtualScreenWidth;
                var virtualHeight = SystemParameters.VirtualScreenHeight;

                if (settings.AddLinksWindowLeft.Value >= virtualLeft - 50 &&
                    settings.AddLinksWindowLeft.Value < virtualLeft + virtualWidth - 50 &&
                    settings.AddLinksWindowTop.Value >= virtualTop - 50 &&
                    settings.AddLinksWindowTop.Value < virtualTop + virtualHeight - 50)
                {
                    WindowStartupLocation = WindowStartupLocation.Manual;
                    Left = settings.AddLinksWindowLeft.Value;
                    Top = settings.AddLinksWindowTop.Value;
                }
            }

            if (settings.IsAddLinksWindowMaximized)
            {
                WindowState = WindowState.Maximized;
            }
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        base.OnClosing(e);

        try
        {
            var settings = SettingsService.Instance.Settings;
            if (WindowState == WindowState.Normal)
            {
                settings.AddLinksWindowWidth = Width;
                settings.AddLinksWindowHeight = Height;
                settings.AddLinksWindowLeft = Left;
                settings.AddLinksWindowTop = Top;
                settings.IsAddLinksWindowMaximized = false;
            }
            else if (WindowState == WindowState.Maximized)
            {
                settings.IsAddLinksWindowMaximized = true;
            }

            SettingsService.Instance.SaveSettings();
        }
        catch { }
    }

    private void UpdateTitleBarTheme(bool isDark)
    {
        ThemeService.ApplyDarkTitleBar(this, isDark);
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        ThemeService.Instance.ThemeChanged -= UpdateTitleBarTheme;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
