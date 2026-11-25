// SPDX-License-Identifier: MPL-2.0
#include "../../chromium/dev/config-schema.h"
#include <fstream>
#include <iostream>
using namespace veil::chromium_dev;
int main(int argc, char** argv) {
  if (argc != 2) return 1;
  std::ifstream file(argv[1], std::ios::binary);
  if (!file) return 2;
  std::string bytes;
  char c;
  while (file.get(c)) {
    if (bytes.size() == kConfigSchemaLimit) return 3;
    bytes.push_back(c);
  }
  if (!file.eof() || file.bad()) return 4;
  ConfigSnapshot snapshot;
  if (DecodeSyntheticConfig(bytes, &snapshot) != ConfigSchemaResult::kSnapshotOnly ||
      snapshot.pipe_name != "VEIL.VolumeBroker" ||
      snapshot.volume_id != "0123456789abcdef0123456789abcdef" ||
      snapshot.broker_image != L"C:\\Program Files\\VEIL\\Broker\\Veil.VolumeBroker.exe" ||
      snapshot.profile_path != L"D:\\VEIL Profile") return 5;
  std::cout << "PASS: installer-shaped PowerShell serialized fixture; no installation, registration trust or broker connection\n";
}
