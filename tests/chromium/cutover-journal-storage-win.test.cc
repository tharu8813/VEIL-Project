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
      "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
      "2cf24dba5fb0a30e26e83b2ac5b9e29e1b161e5c1fa7425e73043362938b9824"};
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
  const CutoverRecoveryEvidence evidence{true,true,true,true,true,true,true,true,true,true,true,true};
  if (mode == L"inspect-crash") {
    SyntheticJournalSession<decltype(policy)> session(root, root, policy);
    auto next = record;
    next.state = J::kRegistrationReplaced;
    auto committed = next;
    committed.state = J::kActivationCommitted;
    if (session.status() != R::kLeaseOnly ||
        session.Read(&out) != R::kRecoveryReview || out.transaction_id != "sentinel" ||
        session.VerifyRegistrationArtifacts() != R::kRecoveryReview ||
        session.CreateRegistrationBackup(record) != R::kRecoveryReview ||
        session.Publish(next) != R::kRecoveryReview ||
        session.Publish(committed) != R::kRecoveryReview ||
        session.RestoreRegistrationFixture(evidence) != R::kRecoveryReview) return 36;
    std::cout << "PASS: new process reacquires lock but all interrupted restore operations refuse\n";
    return 0;
  }
  if (mode == L"crash-marker" || mode == L"crash-copy" ||
      mode == L"crash-displaced" || mode == L"crash-restored") {
    const auto registration_path = root + L"\\registration-fixture.json";
    auto write_bytes = [&](std::string_view bytes) {
      config_path_detail::Handle file(CreateFileW(registration_path.c_str(), GENERIC_WRITE, 0,
          nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
      DWORD count = 0;
      return file.valid() && WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
          &count, nullptr) && count == static_cast<DWORD>(bytes.size()) &&
          FlushFileBuffers(file.get());
    };
    bool armed = false, displaced_seen = false;
    // Fault injection exists only in this fixture executable. No production
    // crash hook; use real opened handle paths to identify exact boundaries.
    auto crash_policy = [&](HANDLE file, bool directory) {
      if (GetFileType(file) != FILE_TYPE_DISK) return false;
      if (!armed || directory) return true;
      std::array<wchar_t, 1024> path{};
      const DWORD count = GetFinalPathNameByHandleW(file, path.data(),
          static_cast<DWORD>(path.size()), FILE_NAME_NORMALIZED | VOLUME_NAME_DOS);
      if (!count || count >= static_cast<DWORD>(path.size())) return false;
      const std::wstring_view full(path.data(), count);
      const auto leaf = full.substr(full.rfind(L'\\') + 1);
      LARGE_INTEGER size{};
      if (!GetFileSizeEx(file, &size)) return false;
      if (leaf == L"restore-displaced.json") displaced_seen = true;
      const bool stop =
          (mode == L"crash-marker" && leaf == L"restore-started.json" && size.QuadPart == 0) ||
          (mode == L"crash-copy" && leaf == L"restore-copy.pending" && size.QuadPart == 3) ||
          (mode == L"crash-displaced" && leaf == L"restore-displaced.json") ||
          (mode == L"crash-restored" && displaced_seen && leaf == L"registration-fixture.json");
      if (stop) {
        // Self termination deliberately skips C++ unwinding and all fixture
        // cleanup. Parent requires exact exit code and verifies disk layout.
        TerminateProcess(GetCurrentProcess(), 77);
        return false; // if termination unexpectedly fails, fail the test
      }
      return true;
    };
    SyntheticJournalSession<decltype(crash_policy)> session(root, root, crash_policy);
    if (session.status() != R::kLeaseOnly || !write_bytes("abc") ||
        session.CreateRegistrationBackup(record) != R::kBackupOnly ||
        session.Publish(record) != R::kPublishedOnly || !write_bytes("hello") ||
        session.VerifyRegistrationArtifacts() != R::kTargetBytesOnly) return 37;
    armed = true;
    (void)session.RestoreRegistrationFixture(evidence);
    return 38; // reaching here means requested crash boundary was missed
  }
  if (mode == L"restore") {
    const auto registration_path = root + L"\\registration-fixture.json";
    auto write_bytes = [&](std::string_view bytes) {
      config_path_detail::Handle file(CreateFileW(registration_path.c_str(), GENERIC_WRITE, 0,
          nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
      DWORD count = 0;
      return file.valid() && WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
          &count, nullptr) && count == static_cast<DWORD>(bytes.size());
    };
    SyntheticJournalSession<decltype(policy)> session(root, root, policy);
    if (session.status() != R::kLeaseOnly || !write_bytes("abc") ||
        session.CreateRegistrationBackup(record) != R::kBackupOnly ||
        session.Publish(record) != R::kPublishedOnly ||
        session.RestoreRegistrationFixture(evidence) != R::kLegacyBytesOnly ||
        !write_bytes("hello")) return 31;
    auto missing = evidence;
    missing.activation_excluded = false;
    if (session.RestoreRegistrationFixture(missing) != R::kRecoveryReview ||
        session.VerifyRegistrationArtifacts() != R::kTargetBytesOnly ||
        !write_bytes("abd") || session.RestoreRegistrationFixture(evidence) != R::kRecoveryReview ||
        !write_bytes("hello")) return 32;
    if (session.RestoreRegistrationFixture(evidence) != R::kRestoredOnly ||
        session.Read(&out) != R::kRecoveryReview || out.transaction_id != "sentinel" ||
        session.RestoreRegistrationFixture(evidence) != R::kRecoveryReview) return 33;
    auto replaced = record;
    replaced.state = J::kRegistrationReplaced;
    if (session.Publish(replaced) != R::kRecoveryReview) return 34;
    auto check = [&](std::wstring_view leaf, std::string_view expected) {
      const auto path = journal_storage_detail::Path(root, leaf);
      config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_READ, FILE_SHARE_READ,
          nullptr, OPEN_EXISTING, FILE_FLAG_OPEN_REPARSE_POINT, nullptr));
      std::string bytes, digest;
      return file.valid() && journal_storage_detail::ReadRegistrationBytes(
          file.get(), path, policy, &bytes, &digest) && bytes == expected;
    };
    if (!check(L"registration-fixture.json", "abc") ||
        !check(L"registration-backup.json", "abc") ||
        !check(L"restore-displaced.json", "hello")) return 35;
    std::cout << "PASS: fixture restore preserves backup and displaced target, blocks future transitions; missing evidence and unknown bytes refused\n";
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
  const auto registration = root + L"\\registration-fixture.json";
  const auto backup = root + L"\\registration-backup.json";
  auto replace_bytes = [](const std::wstring& path, std::string_view bytes) {
    config_path_detail::Handle file(CreateFileW(path.c_str(), GENERIC_WRITE, 0,
        nullptr, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, nullptr));
    DWORD count = 0;
    return file.valid() && WriteFile(file.get(), bytes.data(), static_cast<DWORD>(bytes.size()),
        &count, nullptr) && count == static_cast<DWORD>(bytes.size());
  };
  if (!replace_bytes(registration, "abc")) return 27;
  auto next = record;
  next.state = J::kRegistrationReplaced;
  if (publish(next) != R::kConflict) return 4;
  {
    SyntheticJournalSession<decltype(policy)> session(root, root, policy);
    if (session.status() != R::kLeaseOnly ||
        session.Read(&out) != R::kEmpty || out.transaction_id != "sentinel" ||
        session.VerifyRegistrationArtifacts() != R::kEmpty ||
        session.Publish(record) != R::kUnavailable) return 28;
    auto wrong_backup = record;
    wrong_backup.backup_sha256[0] = 'f';
    if (session.CreateRegistrationBackup(wrong_backup) != R::kDigestMismatch ||
        session.CreateRegistrationBackup(record) != R::kBackupOnly ||
        session.CreateRegistrationBackup(record) != R::kConflict ||
        !replace_bytes(registration, "hello") || session.Publish(record) != R::kConflict ||
        !replace_bytes(registration, "abc") || !replace_bytes(backup, "abd") ||
        session.Publish(record) != R::kDigestMismatch ||
        !replace_bytes(backup, "abc") ||
        session.Publish(record) != R::kPublishedOnly ||
        session.Read(&out) != R::kHistoryOnly || out.state != J::kPrepared ||
        out.transaction_id != record.transaction_id ||
        session.Publish(record) != R::kConflict ||
        session.VerifyRegistrationArtifacts() != R::kLegacyBytesOnly) return 22;
    if (!replace_bytes(registration, "hello") ||
        session.VerifyRegistrationArtifacts() != R::kTargetBytesOnly ||
        !replace_bytes(registration, "abd") ||
        session.VerifyRegistrationArtifacts() != R::kDigestMismatch ||
        !replace_bytes(registration, "abc")) return 29;
    if (!replace_bytes(backup, "abd") ||
        session.VerifyRegistrationArtifacts() != R::kDigestMismatch ||
        !replace_bytes(backup, "abc") ||
        session.VerifyRegistrationArtifacts() != R::kLegacyBytesOnly) return 30;
    std::cout << "PASS: same-handle fixture backup copy and digest binding, current legacy/target/mismatch classification, tampered backup refusal\n";
    // Competing API calls must fail even between session operations.
    if (read() != R::kBusy || publish(next) != R::kBusy ||
        out.state != J::kPrepared) return 23;
    SyntheticJournalSession<decltype(policy)> competitor(root, root, policy);
    if (competitor.status() != R::kBusy || competitor.Read(&out) != R::kBusy ||
        competitor.VerifyRegistrationArtifacts() != R::kBusy ||
        competitor.CreateRegistrationBackup(record) != R::kBusy ||
        competitor.Publish(next) != R::kBusy) return 24;
    std::cout << "PASS: retained journal session excludes competing calls between stages\n";
  }
  if (read() != R::kHistoryOnly || out.state != J::kPrepared ||
      publish(record) != R::kConflict) return 25;
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
  {
    SyntheticJournalSession<decltype(policy)> session(root, root, policy);
    if (session.status() != R::kLeaseOnly || session.Publish(changed) != R::kConflict ||
        session.Publish(next) != R::kConflict ||
        !replace_bytes(registration, "abd") || session.Publish(next) != R::kDigestMismatch ||
        !replace_bytes(registration, "hello") || !replace_bytes(backup, "abd") ||
        session.Publish(next) != R::kDigestMismatch ||
        !replace_bytes(backup, "abc") || session.Publish(next) != R::kPublishedOnly ||
        session.Read(&out) != R::kHistoryOnly || out.state != J::kRegistrationReplaced)
      return 6;
    auto committed = next;
    committed.state = J::kActivationCommitted;
    if (read() != R::kBusy || publish(committed) != R::kBusy ||
        !replace_bytes(registration, "abc") || session.Publish(committed) != R::kConflict ||
        !replace_bytes(registration, "hello") || !replace_bytes(backup, "abd") ||
        session.Publish(committed) != R::kDigestMismatch ||
        !replace_bytes(backup, "abc") || session.Publish(committed) != R::kPublishedOnly ||
        session.Read(&out) != R::kHistoryOnly || out.state != J::kActivationCommitted ||
        session.Publish(next) != R::kConflict ||
        session.RestoreRegistrationFixture(evidence) != R::kRecoveryReview ||
        session.VerifyRegistrationArtifacts() != R::kTargetBytesOnly) return 7;
  }
  if (read() != R::kHistoryOnly || out.state != J::kActivationCommitted) return 26;
  std::cout << "PASS: all three journal stages require intact backup and correct registration bytes; missing, wrong stage and altered digest refused\n";
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
