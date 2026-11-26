// SPDX-License-Identifier: MPL-2.0
namespace Veil.Desktop;

internal sealed record SavedLogin(string Origin, string UserName, string Password)
{
    public override string ToString() => "[SavedLogin redacted]";
}

// Optional trusted-host capability. No generic script execution, form submission,
// storage access or automatic credential capture. The host owns authorization,
// navigation/selection lifetime, explicit consent and encrypted storage.
// Implementations must verify the exact native page and current authorization
// before dispatch and discard stale results after asynchronous execution.
internal interface IEngineLoginForms
{
    Task<bool> ProbeLoginFormAsync(string origin, string page, Func<bool> stillAuthorized);
    Task<SavedLogin?> ReadLoginFormAsync(string origin, string page, Func<bool> stillAuthorized);
    Task<bool> FillLoginFormAsync(SavedLogin login, string page, Func<bool> stillAuthorized);
}
