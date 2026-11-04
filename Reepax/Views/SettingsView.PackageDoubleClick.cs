using Reepax.Helpers;

namespace Reepax.Views;

public partial class SettingsView
{
    private void CloseSettingsDropdowns()
    {
        foreach (var selector in new[] { CompletionSoundButton, ErrorSoundButton, PackageDoubleClickSelector,
            ThemeSelector, LanguageSelector, PostDownloadActionSelector, CompletionSoundTriggerSelector })
            selector?.CloseDropdown();
    }
}
