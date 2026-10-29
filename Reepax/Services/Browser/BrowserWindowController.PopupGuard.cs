using System;
using System.Runtime.InteropServices;
using Reepax.Services.AdBlock;

namespace Reepax.Services.Browser;

public partial class BrowserWindowController
{
    private const string PopupGuardHostName = "reepaxPopupGuard";

    private bool TryBlockPopup(string targetUrl, bool userInitiated)
    {
        var engine = AdBlockRustEngine.Instance;
        var source = _currentTopLevelUrl ?? _webView.Source?.ToString();
        var host = GetHostFromUrl(source);
        if (!IsAdBlockerEnabled || !engine.IsEnabled || !engine.IsAntiPopupGuardEnabled ||
            (!string.IsNullOrEmpty(host) && engine.IsDomainWhitelisted(host)))
            return false;
        if (!BrowserSecurityGuard.ShouldBlockPopup(source, targetUrl, userInitiated))
            return false;

        var label = Uri.TryCreate(targetUrl, UriKind.Absolute, out var target) &&
            (target.Scheme == Uri.UriSchemeHttp || target.Scheme == Uri.UriSchemeHttps) ? target.Host : string.Empty;
        IncrementBlockedCount(label, "PopupGuard");
        return true;
    }

    private bool ShouldApplyCosmeticFiltering()
    {
        var engine = AdBlockRustEngine.Instance;
        var host = GetHostFromUrl(_currentTopLevelUrl ?? _webView.Source?.ToString());
        return IsAdBlockerEnabled && engine.IsEnabled && engine.IsCosmeticFilteringEnabled &&
            (string.IsNullOrEmpty(host) || !engine.IsDomainWhitelisted(host));
    }

    [ComVisible(true), Guid("C0347794-DC52-48B3-BC29-45009687FBC8"), InterfaceType(ComInterfaceType.InterfaceIsIDispatch)]
    public interface IPopupGuardBridge
    {
        [DispId(1)] bool ShouldBlockPopup(string targetUrl, bool userInitiated);
        [DispId(2)] bool ShouldApplyCosmeticFiltering();
    }

    [ComVisible(true), ClassInterface(ClassInterfaceType.None), ComDefaultInterface(typeof(IPopupGuardBridge))]
    public sealed class PopupGuardBridge : IPopupGuardBridge
    {
        private readonly Func<string, bool, bool> _block;
        private readonly Func<bool> _cosmetic;

        internal PopupGuardBridge(Func<string, bool, bool> block, Func<bool> cosmetic)
        {
            _block = block;
            _cosmetic = cosmetic;
        }

        public bool ShouldBlockPopup(string targetUrl, bool userInitiated) => _block(targetUrl, userInitiated);
        public bool ShouldApplyCosmeticFiltering() => _cosmetic();
    }
}
