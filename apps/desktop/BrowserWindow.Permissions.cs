using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private async void SitePermissionsClick(object sender, RoutedEventArgs args)
    {
        if (FocusManagementPage("veil://settings/permissions")) return;
        if (!SettingsReady() || session.PermissionUiOpen) return;
        session.PermissionUiOpen = true;
        try
        {
            var dialog = new SitePermissionsDialog(this, SitePermissionPolicy.Origin(Active?.Url), data.SitePermissions);
            if (await ShowManagementPageAsync("veil://settings/permissions", dialog) != true || closing || session.IsEnding || session.EngineLost) return;
            if (!SitePermissionPolicy.ValidRules(dialog.Result.ToList())) return;
            var previous = data.SitePermissions;
            data.SitePermissions = dialog.Result.Count == 0 ? null : dialog.Result.ToList();
            try { data.Save(statePath); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                data.SitePermissions = previous;
                Status.Text = "사이트 권한을 저장하지 못했습니다. 이전 설정을 유지합니다.";
                return;
            }
            Status.Text = "사이트 권한을 저장했습니다. 다음 권한 요청부터 적용됩니다.";
        }
        catch (Exception error) when (RecoverableFailure.Is(error))
        {
            if (!closing) Status.Text = "사이트 권한 작업을 완료하지 못했습니다.";
        }
        finally { session.PermissionUiOpen = false; }
    }

    private long permissionSelectionEpoch;

    private bool PermissionContextMatches(BrowserTab tab, string origin, long epoch, long selectionEpoch)
    {
        if (closing || restoring || session.IsEnding || session.EngineLost ||
            !CanUseTab(tab.Item) || Active != tab || !IsActive || tab.NavigationEpoch != epoch ||
            permissionSelectionEpoch != selectionEpoch ||
            tab.PendingHttp is not null || tab.PendingThreat is not null ||
            SitePermissionPolicy.Origin(tab.Url) != origin)
            return false;
        try { return SitePermissionPolicy.Origin(tab.View.CoreWebView2?.Source) == origin; }
        catch (Exception error) when (error is InvalidOperationException or COMException) { return false; }
    }

    private void OnSitePermissionRequested(BrowserTab tab, CoreWebView2PermissionRequestedEventArgs args)
    {
        // No decision is persisted in Chromium. Every raised request starts denied.
        args.SavesInProfile = false;
        args.State = CoreWebView2PermissionState.Deny;
        if (session.IsPrivate) return;
        var kind = SitePermissionPolicy.Kind(args.PermissionKind);
        var origin = SitePermissionPolicy.Origin(args.Uri);
        long epoch = tab.NavigationEpoch;
        long selectionEpoch = permissionSelectionEpoch;
        if (kind is null || origin is null || !args.IsUserInitiated || session.PermissionUiOpen ||
            !PermissionContextMatches(tab, origin, epoch, selectionEpoch))
            return;

        string mode = SitePermissionPolicy.ModeFor(data.SitePermissions, origin, kind);
        if (mode == SitePermissionPolicy.Allow)
        {
            args.State = CoreWebView2PermissionState.Allow;
            return;
        }
        if (mode != SitePermissionPolicy.Ask) return;

        // WebView2 callbacks must return before opening a modal dialog. Hold only this request.
        var deferral = args.GetDeferral();
        session.PermissionUiOpen = true;
        try
        {
            Dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    if (!PermissionContextMatches(tab, origin, epoch, selectionEpoch) ||
                        SitePermissionPolicy.ModeFor(data.SitePermissions, origin, kind) != SitePermissionPolicy.Ask)
                        return;
                    string label = SitePermissionPolicy.Kinds.First(item => item.Key == kind).Label;
                    bool accepted = UserPrompts.Confirm(this,
                        $"{origin}\n이 사이트에 {label} 권한을 이번 요청에 한해 허용할까요?", "사이트 권한 요청", MessageBoxImage.Question);
                    // Navigation, window transfer, close or lock may occur during the nested message loop.
                    if (accepted && PermissionContextMatches(tab, origin, epoch, selectionEpoch) &&
                        SitePermissionPolicy.ModeFor(data.SitePermissions, origin, kind) == SitePermissionPolicy.Ask)
                        args.State = CoreWebView2PermissionState.Allow;
                }
                catch (Exception error) when (error is InvalidOperationException or COMException)
                {
                    // The engine may have been disposed during shutdown. Never grant on failure.
                }
                finally
                {
                    session.PermissionUiOpen = false;
                    try { deferral.Complete(); }
                    catch (Exception error) when (error is InvalidOperationException or COMException) { }
                }
            }));
        }
        catch (InvalidOperationException)
        {
            session.PermissionUiOpen = false;
            try { deferral.Complete(); }
            catch (Exception error) when (error is InvalidOperationException or COMException) { }
        }
    }
}
