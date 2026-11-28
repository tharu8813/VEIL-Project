// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_lifecycle.h"
#include <iostream>
#include <stdexcept>
#include <vector>

inline int TestProtectedLifecycle() {
  using namespace veil_cef;
  using Role = ProtectedProcessRole;
  using Status = ProtectedLifecycleStatus;
  std::vector<int> calls;
  int authority_checks = 0, revoke_at = 0, dispatch = -1;
  bool settings_ok = true, init_ok = true, fixture_ok = true, closed = true;
  int throw_at = 0;
  bool check_inside_fixture = false;
  bool denied_inside_fixture = false;
  auto run = [&](Role role) {
    calls.clear(); authority_checks = 0;
    return RunProtectedLifecycle(role,
        [&] { calls.push_back(1); return ++authority_checks != revoke_at; },
        [&] { calls.push_back(2); if (throw_at == 2) throw std::runtime_error("dispatch"); return dispatch; },
        [&] { calls.push_back(3); if (throw_at == 3) throw std::runtime_error("settings"); return settings_ok; },
        [&] { calls.push_back(4); if (throw_at == 4) throw std::runtime_error("initialize"); return init_ok; },
        [&](const auto& current) {
          calls.push_back(5);
          if (throw_at == 5) throw std::runtime_error("fixture");
          if (check_inside_fixture) {
            denied_inside_fixture = !current() && !current();
          }
          return ProtectedFixtureResult{fixture_ok, closed};
        },
        [&] { calls.push_back(6); if (throw_at == 6) throw std::runtime_error("shutdown"); });
  };
  if (run(Role::kBrowser).status != Status::kFixtureVerifiedAfterShutdown ||
      calls != std::vector<int>{1,2,1,3,1,4,1,5,1,6,1}) return 1;
  dispatch = 7;
  auto subprocess = run(Role::kSubprocess);
  if (subprocess.status != Status::kSubprocessExited || subprocess.subprocess_exit != 7 ||
      calls != std::vector<int>{1,2,1}) return 2;
  revoke_at = 2;
  if (run(Role::kSubprocess).status != Status::kAuthorityLostAfterDispatch ||
      calls != std::vector<int>{1,2,1}) return 16;
  revoke_at = 0;
  if (run(Role::kBrowser).status != Status::kDispatchRejected ||
      calls != std::vector<int>{1,2}) return 3;
  dispatch = -1;
  if (run(Role::kSubprocess).status != Status::kDispatchRejected ||
      calls != std::vector<int>{1,2}) return 4;
  revoke_at = 1;
  if (run(Role::kBrowser).status != Status::kAuthorityRefused || calls != std::vector<int>{1}) return 5;
  revoke_at = 3;
  if (run(Role::kBrowser).status != Status::kAuthorityRefused ||
      calls != std::vector<int>{1,2,1,3,1}) return 6;
  revoke_at = 4;
  if (run(Role::kBrowser).status != Status::kAuthorityLostAfterShutdown ||
      calls != std::vector<int>{1,2,1,3,1,4,1,6}) return 7;
  revoke_at = 5;
  if (run(Role::kBrowser).status != Status::kAuthorityLostAfterShutdown ||
      calls != std::vector<int>{1,2,1,3,1,4,1,5,1,6}) return 8;
  revoke_at = 6;
  if (run(Role::kBrowser).status != Status::kAuthorityLostAfterShutdown) return 9;
  // The fixture observes a transient refusal. The supplier would return
  // true again, but no subsequent supplier call or success is allowed.
  revoke_at = 5; check_inside_fixture = true;
  if (run(Role::kBrowser).status != Status::kAuthorityLostAfterShutdown ||
      !denied_inside_fixture || authority_checks != 5 ||
      calls != std::vector<int>{1,2,1,3,1,4,1,5,1,6}) return 17;
  check_inside_fixture = false;
  revoke_at = 0; settings_ok = false;
  if (run(Role::kBrowser).status != Status::kSettingsRejected ||
      calls != std::vector<int>{1,2,1,3}) return 10;
  settings_ok = true; init_ok = false;
  if (run(Role::kBrowser).status != Status::kInitializationFailed ||
      calls != std::vector<int>{1,2,1,3,1,4}) return 11;
  init_ok = true; fixture_ok = false;
  if (run(Role::kBrowser).status != Status::kFixtureFailedAfterShutdown) return 12;
  closed = false;
  if (run(Role::kBrowser).status != Status::kCleanupUnconfirmed ||
      calls != std::vector<int>{1,2,1,3,1,4,1,5}) return 13;
  closed = true;
  for (int failure : {2,3,4,5,6}) {
    throw_at = failure;
    if (run(Role::kBrowser).status != (failure == 3 ? Status::kSettingsRejected : Status::kCleanupUnconfirmed)) return 14;
    if (calls.back() != failure) return 15;
  }
  std::cout << "PASS: synthetic protected lifecycle ordering, role dispatch, revocation and cleanup\n";
  return 0;
}
