# SPDX-License-Identifier: MPL-2.0
import importlib.util
import pathlib
import subprocess
import sys
import tempfile
import unittest

SCRIPT = pathlib.Path(__file__).resolve().parents[2] / "scripts/chromium/generate-cache-failure-candidate.py"
sys.path.insert(0, str(SCRIPT.parent))
spec = importlib.util.spec_from_file_location("cache_candidate", SCRIPT)
candidate = importlib.util.module_from_spec(spec)
spec.loader.exec_module(candidate)

PRE = r'''
#include <string>
#include <cassert>
std::wstring* g_user_data_dir = nullptr;
std::wstring* g_invalid_user_data_dir = nullptr;
struct InstallDetails {
  static InstallDetails Get() { return {}; }
  int mode() { return 0; }
};
int scenario = 0;
int calls = 0;
bool GetUserDataDirectoryUsingProcessCommandLine(int, std::wstring* path,
                                                std::wstring* invalid) {
  ++calls;
  *invalid = L"invalid";
  if (scenario == 1) return true;
  *path = L"synthetic";
  return scenario == 2;
}
void reset() {
  delete g_user_data_dir;
  delete g_invalid_user_data_dir;
  g_user_data_dir = nullptr;
  g_invalid_user_data_dir = nullptr;
  calls = 0;
}
'''
POST = r'''
int main() {
  for (scenario = 0; scenario < 3; ++scenario) {
    std::wstring out = L"unchanged", invalid = L"unchanged";
    const bool expected = scenario == 2;
    for (int i = 0; i < 2; ++i) {
      if (GetUserDataDirectory(&out, &invalid) != expected) return 1;
      if (out != (expected ? L"synthetic" : L"unchanged")) return 2;
      if (invalid != (expected ? L"invalid" : L"unchanged")) return 3;
    }
    if (calls != 1) return 4;
    reset();
  }
}
'''


class CacheTests(unittest.TestCase):
    def test_exact_rewrite(self):
        self.assertEqual(candidate.rewrite(candidate.ORIGINAL.encode()),
                         candidate.MODIFIED.encode())
        for raw in (b"", (candidate.ORIGINAL * 2).encode(), candidate.MODIFIED.encode()):
            with self.assertRaises(ValueError):
                candidate.rewrite(raw)

    def test_compile_and_run_synthetic_function(self):
        with tempfile.TemporaryDirectory() as tmp:
            src = pathlib.Path(tmp) / "fixture.cc"
            exe = pathlib.Path(tmp) / "fixture"
            src.write_text(PRE + candidate.MODIFIED + POST, encoding="utf-8")
            subprocess.run(["g++", "-std=c++17", "-Wall", "-Wextra", "-Werror",
                            str(src), "-o", str(exe)], check=True, timeout=30)
            subprocess.run([str(exe)], check=True, timeout=10)


if __name__ == "__main__":
    unittest.main()
