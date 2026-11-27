// SPDX-License-Identifier: MPL-2.0
#include "product_preflight.h"
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
  std::cout << "PASS: synthetic product preflight order, refusal and disabled startup\n";
  return 0;
}
