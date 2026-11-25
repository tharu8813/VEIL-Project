// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/cutover-journal-storage-win.h"
#include <iostream>
using namespace veil::chromium_dev;
int wmain(int argc, wchar_t** argv) {
  if (argc != 3) return 1;
  const std::wstring_view mode(argv[2]);
  const std::wstring root(argv[1]);
  using R = JournalStorageObservation;
  using J = CutoverJournalState;
  CutoverJournalRecord record{std::string(32, 'a'), J::kPrepared,
      {L"C:\\Program Files\\VEIL\\Desktop\\Veil.Desktop.exe", std::string(64, 'b')},
      {L"C:\\Program Files\\VEIL\\Chromium\\client-fixture.exe", std::string(64, 'c')},
      std::string(64, 'd'), std::string(64, 'e')};
  CutoverJournalRecord out;
  out.transaction_id = "sentinel";
  auto policy = [](HANDLE file, bool) { return GetFileType(file) == FILE_TYPE_DISK; };
  auto read = [&] { return ObserveSyntheticJournalHistoryWith(root, root, &out, policy); };
  auto publish = [&](const CutoverJournalRecord& next) {
    return PublishSyntheticJournalRecordWith(root, root, next, policy);
  };
  if (mode == L"busy") {
    if (read() != R::kBusy || publish(record) != R::kBusy ||
        out.transaction_id != "sentinel") return 12;
    if (journal_storage_detail::Exists(root + L"\\record.pending") !=
        journal_storage_detail::Presence::kAbsent) return 13;
    std::cout << "PASS: another process holds maintenance lock; read and publication refused without mutation\n";
    return 0;
  }
  if (mode != L"normal") return 14;
  const auto lock = root + L"\\maintenance.lock";
  const auto alias_lock = root + L"\\alias-lock";
  if (!CreateHardLinkW(alias_lock.c_str(), lock.c_str(), nullptr)) return 15;
  const auto linked_lock = read();
  if (!DeleteFileW(alias_lock.c_str()) || linked_lock != R::kUnsafe ||
      out.transaction_id != "sentinel") return 16;
  if (!DeleteFileW(lock.c_str()) || read() != R::kUnavailable ||
      publish(record) != R::kUnavailable || out.transaction_id != "sentinel") return 17;
  {
    config_path_detail::Handle recreated(CreateFileW(lock.c_str(), GENERIC_WRITE, 0,
        nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!recreated.valid()) return 18;
    DWORD written = 0;
    if (!WriteFile(recreated.get(), "x", 1, &written, nullptr) || written != 1) return 19;
  }
  if (read() != R::kUnsafe || publish(record) != R::kUnsafe ||
      !DeleteFileW(lock.c_str()) || out.transaction_id != "sentinel") return 20;
  {
    config_path_detail::Handle recreated(CreateFileW(lock.c_str(), GENERIC_WRITE, 0,
        nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    if (!recreated.valid()) return 21;
  }
  if (ObserveSyntheticJournalHistory(root, root, &out) != R::kUnsafe ||
      PublishSyntheticJournalRecord(root, root, record) != R::kUnsafe ||
      out.transaction_id != "sentinel") return 2;
  if (read() != R::kEmpty || out.transaction_id != "sentinel" ||
      ObserveSyntheticJournalHistoryWith(root, root + L"\\wrong", &out, policy) != R::kRejected)
    return 3;
  auto next = record;
  next.state = J::kRegistrationReplaced;
  if (publish(next) != R::kConflict || publish(record) != R::kPublishedOnly ||
      read() != R::kHistoryOnly || out.state != J::kPrepared ||
      out.transaction_id != record.transaction_id || publish(record) != R::kConflict) return 4;
  const auto pending = root + L"\\record.pending";
  auto write = [](const std::wstring& path, std::string_view bytes) {
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0,
        nullptr, CREATE_NEW, FILE_ATTRIBUTE_NORMAL, nullptr));
    DWORD written = 0;
    return file.valid() && WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
        &written, nullptr) && written == static_cast<DWORD>(bytes.size());
  };
  if (!write(pending, "{") || read() != R::kPending || out.state != J::kPrepared ||
      publish(next) != R::kPending || !DeleteFileW(pending.c_str())) return 5;
  auto changed = next;
  changed.transaction_id[0] = 'f';
  if (publish(changed) != R::kConflict || publish(next) != R::kPublishedOnly ||
      read() != R::kHistoryOnly || out.state != J::kRegistrationReplaced) return 6;
  auto committed = next;
  committed.state = J::kActivationCommitted;
  if (publish(committed) != R::kPublishedOnly || read() != R::kHistoryOnly ||
      out.state != J::kActivationCommitted || publish(next) != R::kConflict) return 7;
  const auto first = root + L"\\prepared.json";
  const auto second = root + L"\\registration-replaced.json";
  const auto alias = root + L"\\alias.json";
  if (!CreateHardLinkW(alias.c_str(), first.c_str(), nullptr)) return 8;
  const auto linked = read();
  if (!DeleteFileW(alias.c_str()) || linked != R::kUnsafe || out.state != J::kActivationCommitted)
    return 9;
  if (!DeleteFileW(second.c_str()) || read() != R::kInvalidHistory ||
      out.state != J::kActivationCommitted || publish(next) != R::kInvalidHistory) return 10;
  if (!write(second, "{") || read() != R::kInvalidHistory ||
      out.state != J::kActivationCommitted) return 11;
  std::cout << "PASS: native fixture write/flush/no-replace rename, monotonic readback, pending/gap/malformed/hardlink refusal and ordinary ACL refusal; injected policy positive only, power-loss durability NOT RUN\n";
}
