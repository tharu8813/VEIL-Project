using System.IO;
using Microsoft.Web.WebView2.Core;

namespace Veil.Desktop;

public partial class BrowserWindow
{
    private void ConfigureWebView2Tab(BrowserTab tab)
    {
        var web = tab.View.CoreWebView2;
        web.Settings.AreHostObjectsAllowed = false;
        web.Settings.IsWebMessageEnabled = false;
        web.Settings.IsPasswordAutosaveEnabled = false;
        web.Settings.IsGeneralAutofillEnabled = false;
        web.Settings.AreDefaultContextMenusEnabled = true;
        web.Settings.AreDevToolsEnabled = false;
        web.Settings.AreBrowserAcceleratorKeysEnabled = false;
        // ADR-0007: SmartScreen policy before the first navigation (an unset view re-enables it for all).
        if (!session.Security.Apply(web))
        {
            Status.Text = session.Security.Describe();
            UpdateSecurityIndicators();
        }
        // SEC-004/007: the engine writes download bytes (GUID.tmp) before DownloadStarting can cancel
        // them. Keep that partial data on the profile volume instead of the user's Downloads folder.
        string downloadScratch = Path.Combine(session.Root, ProfileScratch.FolderName,
            session.IsPrivate ? session.PrivateProfileName! : "downloads");
        Directory.CreateDirectory(downloadScratch);
        web.Profile.DefaultDownloadFolderPath = downloadScratch;
        // ADR-0006 standard protection: strict tracking prevention for every profile.
        web.Profile.PreferredTrackingPreventionLevel = CoreWebView2TrackingPreventionLevel.Strict;
        web.PermissionRequested += (_, args) => tab.Owner.OnSitePermissionRequested(tab, args);
        web.LaunchingExternalUriScheme += (_, args) => args.Cancel = true;
        web.FaviconChanged += async (_, _) => await tab.Owner.UpdateFavicon(tab, web);
        web.DownloadStarting += (_, args) => tab.Owner.OnDownloadStarting(tab, args);
        // Record navigation methods so the vault never replays a POST download as GET.
        if (!session.IsPrivate)
        {
            web.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.Document);
            web.WebResourceRequested += (_, args) =>
            {
                if (!IsWebUrl(args.Request.Uri)) return;
                session.Vault.NoteRequest(args.Request.Method, args.Request.Uri);
                tab.LastDownloadRequest = (args.Request.Method, args.Request.Uri, tab.NavigationEpoch);
            };
        }
        web.NewWindowRequested += (_, args) => tab.Owner.OnNewWindowRequested(tab, args);
        web.WindowCloseRequested += (_, _) => tab.Owner.Dispatcher.BeginInvoke(() => tab.Owner.CloseTab(tab.Item));
        web.IsDocumentPlayingAudioChanged += (_, _) => tab.Owner.UpdateAudio(tab);
        web.IsMutedChanged += (_, _) => tab.Owner.UpdateAudio(tab);
        web.ProcessFailed += (_, args) => tab.Owner.OnProcessFailed(tab, args);
        web.ContainsFullScreenElementChanged += (_, _) =>
        {
            if (tab.Owner.Active == tab) tab.Owner.SetFullScreen(web.ContainsFullScreenElement, page: true);
        };
    }
}
