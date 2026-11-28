// SPDX-License-Identifier: MPL-2.0
#include "product_preflight.h"
#include "protected_initialization_plan.h"
#include "protected_lifecycle_test.h"
#include "protected_fixture_observation_test.h"
#include <iostream>
#include <stdexcept>
#include <vector>

int main() {
  using namespace veil_cef;
  using Exit = ProductPreflightExit;
  const RegisteredBootstrap expected{
      fs::path(L"V:\\VEIL\\profile"),
      L"\\\\?\\Volume{01234567-89ab-cdef-0123-456789abcdef}\\", "synthetic"};
  std::vector<int> calls;
  auto load = [&](RegisteredBootstrap& result) {
    calls.push_back(1); result = expected; return true;
  };
  auto verify = [&](const RegisteredBootstrap& result) {
    calls.push_back(2);
    return result.volume_id == expected.volume_id ?
        BootstrapCheck::kPreflightVerified : BootstrapCheck::kBrokerRejected;
  };
  if (RunProductPreflight(false, load, verify) != Exit::kInvocationRejected ||
      !calls.empty()) return 1;
  if (RunProductPreflight(true, load, verify) != Exit::kVerifiedButDisabled ||
      calls != std::vector<int>{1, 2, 1}) return 2;
  calls.clear();
  auto missing = [&](RegisteredBootstrap&) { calls.push_back(1); return false; };
  if (RunProductPreflight(true, missing, verify) != Exit::kRegistrationRejected ||
      calls != std::vector<int>{1}) return 3;
  for (auto denial : {BootstrapCheck::kInvalidRegistration, BootstrapCheck::kInvalidEnvironment,
      BootstrapCheck::kInvalidPaths, BootstrapCheck::kBrokerRejected,
      BootstrapCheck::kChangedAfterReceipt}) {
    calls.clear();
    if (RunProductPreflight(true, load, [&](const RegisteredBootstrap&) {
          calls.push_back(2); return denial;
        }) != Exit::kBootstrapRejected || calls != std::vector<int>{1, 2}) return 4;
  }
  for (int field = 0; field < 4; ++field) {
    calls.clear();
    auto changed = [&](RegisteredBootstrap& result) {
      load(result);
      if (calls.size() == 3) {
        if (field == 0) result.product_root = fs::path(L"W:\\VEIL\\profile");
        if (field == 1) result.volume_guid += L"x";
        if (field == 2) result.volume_id += "x";
        if (field == 3) return false;
      }
      return true;
    };
    if (RunProductPreflight(true, changed, verify) != Exit::kRegistrationChanged ||
        calls != std::vector<int>{1, 2, 1}) return 5;
  }
  calls.clear();
  if (RunProductPreflight(true, load, [&](const RegisteredBootstrap&) -> BootstrapCheck {
        calls.push_back(2); throw std::runtime_error("synthetic");
      }) != Exit::kUnexpectedFailure || calls != std::vector<int>{1, 2}) return 6;
  // Configuration preparation is not a successful product preflight and
  // does not run CEF. The path observer here is synthetic.
  const auto root = expected.product_root / L"cef";
  const BootstrapEnvironment environment{
      {root, root / L"cache", root / L"temp", root / L"logs" / L"cef.log"},
      root / L"temp", root / L"temp"};
  int reads = 0, inspections = 0;
  const auto read = [&](BootstrapEnvironment& out) {
    ++reads; out = environment; return true;
  };
  const auto inspect = [&](const RegisteredBootstrap& registration, const Paths&) {
    ++inspections; return registration.volume_id == expected.volume_id;
  };
  auto plan = PrepareProtectedInitializationPlan(expected, read, inspect);
  if (!plan || plan->root_cache != root || plan->cache != root / L"cache" ||
      plan->scratch != root / L"temp" || plan->log != root / L"logs" / L"cef.log" ||
      reads != 2 || inspections != 2) return 7;
  for (int field = 0; field < 6; ++field) {
    reads = inspections = 0;
    auto invalid = [&](BootstrapEnvironment& out) {
      read(out);
      if (field == 0) out.paths.root = L"C:\\fallback";
      if (field == 1) out.paths.cache = root / L"..";
      if (field == 2) out.paths.scratch = L"C:\\temp";
      if (field == 3) out.paths.log = L"C:\\cef.log";
      if (field == 4) out.temp = L"C:\\temp";
      if (field == 5) out.tmp.clear();
      return true;
    };
    if (PrepareProtectedInitializationPlan(expected, invalid, inspect) ||
        inspections != 0) return 8;
  }
  reads = inspections = 0;
  if (PrepareProtectedInitializationPlan(expected, [&](BootstrapEnvironment& out) {
        read(out); if (reads == 2) out.paths.log = L"C:\\cef.log"; return true;
      }, inspect) || inspections != 1) return 9;
  reads = inspections = 0;
  if (PrepareProtectedInitializationPlan(expected, read,
        [&](const RegisteredBootstrap&, const Paths&) { return ++inspections == 1; }) ||
      reads != 2 || inspections != 2) return 10;
  if (PrepareProtectedInitializationPlan(expected, read,
        [](const RegisteredBootstrap&, const Paths&) -> bool {
          throw std::runtime_error("synthetic");
        })) return 11;
  auto invalid_registration = expected;
  invalid_registration.volume_id.clear();
  if (PrepareProtectedInitializationPlan(invalid_registration, read, inspect)) return 12;
  if (std::string(kProtectedSyntheticPage).find("data:text/html,") != 0) return 13;
  std::cout << "PASS: protected initialization settings plan, no fallback and repeat inspection (synthetic)\n";
  if (const int lifecycle = TestProtectedLifecycle(); lifecycle != 0) return 20 + lifecycle;
  if (const int fixture = TestProtectedFixtureObservation(); fixture != 0) return 50 + fixture;
  std::cout << "PASS: synthetic product preflight order, refusal and disabled startup\n";
  return 0;
}
