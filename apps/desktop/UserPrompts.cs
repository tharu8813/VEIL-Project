using System.Windows;
using Microsoft.Win32;

namespace Veil.Desktop;

/// <summary>
/// Every native confirmation, notice and secret prompt goes through here. Debug smoke runs may answer
/// them without a modal window; the hooks do not exist in Release builds.
/// </summary>
internal static class UserPrompts
{
#if DEBUG
    /// <summary>Debug smoke only: answers a Yes/No confirmation (default No).</summary>
    internal static Func<string, string, bool>? SmokeConfirm;
    /// <summary>Debug smoke only: receives an OK notice instead of a modal message box.</summary>
    internal static Action<string, string>? SmokeNotice;
    /// <summary>Debug smoke only: answers a secret prompt by title; null means cancelled.</summary>
    internal static Func<string, char[]?>? SmokeSecret;
    /// <summary>Debug smoke only: records a modal dialog by title instead of showing it.</summary>
    internal static Action<string>? SmokeModal;
    // Debug only: substitute a synthetic path or cancellation for the native file picker.
    internal static Func<bool, string?>? SmokeFilePicker;
#endif

    internal static bool Confirm(Window owner, string message, string caption, MessageBoxImage icon = MessageBoxImage.Warning)
    {
#if DEBUG
        if (SmokeConfirm is { } answer) return answer(caption, message);
#endif
        return MessageBox.Show(owner, message, caption, MessageBoxButton.YesNo, icon, MessageBoxResult.No) == MessageBoxResult.Yes;
    }

    internal static void Notice(Window owner, string message, string caption, MessageBoxImage icon = MessageBoxImage.Warning)
    {
#if DEBUG
        if (SmokeNotice is { } record) { record(caption, message); return; }
#endif
        MessageBox.Show(owner, message, caption, MessageBoxButton.OK, icon);
    }

    internal static char[]? Secret(string title, string prompt, Window? owner)
    {
#if DEBUG
        if (SmokeSecret is { } answer) return answer(title);
#endif
        return ProfileUnlockDialog.Prompt(title, prompt, owner);
    }

    internal static string? PickFile(Window owner, FileDialog picker)
    {
#if DEBUG
        if (SmokeFilePicker is { } answer) return answer(picker is SaveFileDialog);
#endif
        return picker.ShowDialog(owner) == true ? picker.FileName : null;
    }

    internal static void ShowModal(VeilDialog dialog, string title)
    {
#if DEBUG
        if (SmokeModal is { } record) { record(title); dialog.Close(); return; }
#endif
        dialog.ShowDialog();
    }
}
