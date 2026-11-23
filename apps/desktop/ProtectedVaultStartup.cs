using System.Windows;

namespace Veil.Desktop;

/// <summary>Uses the verified profile password only during startup, before a browser window can use the vault.</summary>
internal static class ProtectedVaultStartup
{
    internal static bool TryOpen(DownloadVault vault, char[] password, Func<bool> cancelled) =>
        TryOpen(vault, password, cancelled, () =>
        {
            var setup = new VaultSetupDialog(null, "금고 복구 코드 백업", askPassphrase: false, issueCode: true);
            return setup.ShowDialog() == true ? setup.RecoveryCode : null;
        }, error =>
        {
            var unlock = new VaultUnlockDialog(null, error, "기존 금고의 암호 또는 복구 코드를 입력해 보호 프로필 암호와 연결하세요.");
            return unlock.ShowDialog() == true ? (unlock.Passphrase, unlock.RecoveryCode) : null;
        }, setupFresh: () =>
        {
            var setup = new VaultSetupDialog(null, "다운로드 금고 설정", askPassphrase: false, issueCode: true, allowRestore: true);
            return setup.ShowDialog() == true ? (setup.RecoveryCode, setup.Backup) : (null, null);
        });

    internal static bool TryOpen(DownloadVault vault, char[] password, Func<bool> cancelled,
        Func<string?> confirmRecoveryCode, Func<string?, (string? Passphrase, string? Code)?> openPrevious,
        Func<(string? Code, VaultBackupSource? Backup)>? setupFresh = null)
    {
        if (cancelled() || !vault.Available) return false;
        bool opened = false;
        try
        {
            if (!vault.Exists)
            {
                var (code, backup) = setupFresh?.Invoke() ?? (confirmRecoveryCode(), null);
                if (backup is not null && !cancelled())
                {
                    if (Run(() => vault.RestoreAsync(backup)) is { } failure)
                        throw new System.IO.IOException(failure);
                    return opened = !cancelled() && Run(() => vault.BindProfilePasswordAsync(password, null)) && !cancelled();
                }
                return opened = code is not null && !cancelled() &&
                    Run(() => vault.OpenWithProfilePasswordAsync(password, code)) is null && !cancelled();
            }

            if (Run(() => vault.OpenWithProfilePasswordAsync(password)) is null)
                return opened = !cancelled();

            string? error = null;
            for (int attempt = 0; attempt < 5 && !cancelled(); attempt++)
            {
                if (openPrevious(error) is not { } credential || cancelled()) return false;
                error = Run(() => credential.Code is { } code
                    ? vault.RecoverAsync(code)
                    : vault.UnlockAsync(credential.Passphrase!));
                if (error is not null) continue;

                string? newCode = credential.Code is null ? null : confirmRecoveryCode();
                if (cancelled() || (credential.Code is not null && newCode is null)) return false;
                if (!Run(() => vault.BindProfilePasswordAsync(password, newCode)))
                    throw new System.IO.IOException("Could not bind the vault to the verified profile password.");
                return opened = !cancelled();
            }
            return false;
        }
        finally
        {
            if (!opened) vault.Lock();
        }
    }

    // The blocking broker startup is on the WPF thread. Run the pipe I/O without its dispatcher context.
    private static T Run<T>(Func<Task<T>> action) => Task.Run(action).GetAwaiter().GetResult();
}
