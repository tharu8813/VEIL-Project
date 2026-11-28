// SPDX-License-Identifier: MPL-2.0
#pragma once

namespace veil_cef {
// Role and authority must originate from a future independently reviewed
// broker admission path. This helper does not authenticate either.
enum class ProtectedProcessRole { kBrowser, kSubprocess };
enum class ProtectedLifecycleStatus {
  kAuthorityRefused,
  kDispatchRejected,
  kSubprocessExited,
  kAuthorityLostAfterDispatch,
  kSettingsRejected,
  kInitializationFailed,
  kCleanupUnconfirmed,
  kAuthorityLostAfterShutdown,
  kFixtureFailedAfterShutdown,
  kFixtureVerifiedAfterShutdown,
};
struct ProtectedLifecycleResult {
  ProtectedLifecycleStatus status;
  int subprocess_exit = -1;
};
struct ProtectedFixtureResult {
  bool passed;
  bool browsers_closed;
};

// Must run on the CEF main thread. The caller remains responsible for
// retained Jobs, continuous authority, and terminal drain on every outcome.
// A true initialize result permits exactly one shutdown only when there is
// no browser or all browsers are confirmed closed. A lost/throwing fixture
// cannot prove closure; leave termination to the owning broker.
template <typename Require, typename Execute, typename Prepare,
          typename Initialize, typename Fixture, typename Shutdown>
ProtectedLifecycleResult RunProtectedLifecycle(ProtectedProcessRole role,
    Require require_current, Execute execute, Prepare prepare,
    Initialize initialize, Fixture fixture, Shutdown shutdown) {
  using Status = ProtectedLifecycleStatus;
  // A denial or exception is permanent for this invocation, including
  // checks made inside the fixture. Never refresh authority after refusal.
  bool retired = false;
  const auto current = [&]() {
    if (retired) return false;
    try { if (require_current()) return true; } catch (...) {}
    retired = true;
    return false;
  };
  if ((role != ProtectedProcessRole::kBrowser &&
       role != ProtectedProcessRole::kSubprocess) || !current())
    return {Status::kAuthorityRefused};
  int dispatch;
  try { dispatch = execute(); }
  catch (...) { return {Status::kCleanupUnconfirmed}; }
  if (role == ProtectedProcessRole::kSubprocess) {
    if (dispatch < 0) return {Status::kDispatchRejected};
    if (!current()) return {Status::kAuthorityLostAfterDispatch};
    return {Status::kSubprocessExited, dispatch};
  }
  if (dispatch != -1) return {Status::kDispatchRejected};
  if (!current()) return {Status::kAuthorityRefused};
  try { if (!prepare()) return {Status::kSettingsRejected}; }
  catch (...) { return {Status::kSettingsRejected}; }
  if (!current()) return {Status::kAuthorityRefused};
  try { if (!initialize()) return {Status::kInitializationFailed}; }
  catch (...) { return {Status::kCleanupUnconfirmed}; }

  bool authorized = current();
  ProtectedFixtureResult outcome{false, true};
  if (authorized) {
    try { outcome = fixture(current); }
    catch (...) { return {Status::kCleanupUnconfirmed}; }
  }
  if (!outcome.browsers_closed) return {Status::kCleanupUnconfirmed};
  // Revocation never skips safe shutdown. Do not retry shutdown on failure.
  if (!current()) authorized = false;
  try { shutdown(); }
  catch (...) { return {Status::kCleanupUnconfirmed}; }
  if (!current() || !authorized) return {Status::kAuthorityLostAfterShutdown};
  return {outcome.passed ? Status::kFixtureVerifiedAfterShutdown
                         : Status::kFixtureFailedAfterShutdown};
}
}  // namespace veil_cef
