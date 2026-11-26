# VEIL CEF Windows smoke host

The standalone Win32 DLL runs a local JavaScript fixture in a hidden child window. It returns `0` after the JavaScript-generated title is observed, the browser closes, and CEF shuts down. The host requires the sandbox pointer provided by the official bootstrap and uses a fresh temporary profile. Run as a standard user with a bounded test timeout. The profile and its `cef.log` are retained in `%TEMP%` as `veil-cef-smoke-profile-<pid>`.

## Pinned Windows x64 build

Use `cef_binary_158.0.6+g23f1736+chromium-158.0.8127.89_windows64_minimal.tar.bz2` from the [official CEF distributions](https://cef-builds.spotifycdn.com/index.html). Verify the archive before extraction.

- Published SHA-1 `2abb78fcd3b07ea0fb2014e3867a0902f19f1731`
- Observed SHA-256 `1da0b9fe0e915bd41448939f944003d848f8d9e4d15c7d85d7a17cce656b1a9a`

From the repository root, configure with CMake and Visual Studio 2026. `CEF_ROOT` points to the extracted distribution.

```powershell
cmake -S apps/cef-smoke -B $build -G 'Visual Studio 18 2026' -A x64 "-DCEF_ROOT=$cefRoot"
cmake --build $build --config Release
```

Run `Release/veil-cef-smoke.exe` from the build directory. CMake copies the official `bootstrap.exe` under that name so it loads `veil-cef-smoke.dll`. The same executable serves as the browser and subprocess entry point. [CEF Windows sandbox setup](https://github.com/chromiumembedded/cef/blob/master/docs/sandbox_setup.md)

## Observed results

On Windows x64, the host returned `0` and left no matching processes. The browser ran at Medium integrity. Three renderer processes ran at Untrusted integrity with restricted tokens and no observed `--no-sandbox` switch. WPF embedding, cross-site frame-to-process mapping, and protected storage have yet to be verified.
