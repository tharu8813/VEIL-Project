// SPDX-License-Identifier: MPL-2.0
#pragma once
#include "protected_lifecycle.h"

namespace veil_cef {
// Single-browser test evidence only. Unexpected callbacks permanently
// invalidate success; additional/unknown browser lifetimes prevent shutdown.
class ProtectedFixtureObservation {
 public:
  void Created(int id) {
    if (id <= 0 || id_ != 0 || closed_) { ambiguous_ = true; failed_ = true; return; }
    id_ = id;
  }
  bool Matches(int id) const { return id_ > 0 && id_ == id && !closed_ && !ambiguous_; }
  void Load(int id, bool exact_page) {
    if (!Matches(id) || !exact_page) { failed_ = true; return; }
    loaded_ = true;
  }
  void Title(int id, bool exact_page, bool exact_title) {
    if (!Matches(id)) { failed_ = true; return; }
    if (exact_page && exact_title) titled_ = true;
    else if (exact_title) failed_ = true;
  }
  bool Ready() const { return loaded_ && titled_ && !failed_ && !ambiguous_ && !closed_; }
  bool Failed() const { return failed_ || ambiguous_; }
  void Fail() { failed_ = true; }
  void RequestClose(int id) {
    if (!Matches(id) || close_requested_) { failed_ = true; return; }
    close_requested_ = true;
    ready_at_close_ = Ready();
  }
  void Closed(int id) {
    if (!Matches(id)) { ambiguous_ = true; failed_ = true; return; }
    if (!close_requested_) failed_ = true;
    closed_ = true;
  }
  ProtectedFixtureResult Result() const {
    const bool confirmed = id_ > 0 && closed_ && !ambiguous_;
    return {confirmed && ready_at_close_ && !failed_, confirmed};
  }
 private:
  int id_ = 0;
  bool loaded_ = false, titled_ = false, failed_ = false, ambiguous_ = false;
  bool close_requested_ = false, ready_at_close_ = false, closed_ = false;
};
}  // namespace veil_cef
