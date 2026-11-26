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

On Windows x64, the Win32 host returned `0` and left no matching processes. The browser ran at Medium integrity. Three renderer processes ran at Untrusted integrity with restricted tokens and no observed `--no-sandbox` switch. Cross-site frame-to-process mapping and protected storage have yet to be verified.

## WPF window fixture

Use the same CEF distribution and point `$dotnetHostPack` to the x64 .NET 10 host pack's `runtimes/win-x64/native` directory. The official bootstrap starts CEF and loads the WPF assembly on the same STA thread. WPF hosts the browser in a child HWND and pumps CEF work on that thread. This fixture accepts loopback HTTP addresses and uses a separate temporary profile.

```powershell
cmake -S apps/cef-smoke -B $build -G 'Visual Studio 18 2026' -A x64 "-DCEF_ROOT=$cefRoot" "-DDOTNET_HOST_PACK=$dotnetHostPack"
cmake --build $build --config Release
dotnet build apps/cef-smoke/wpf/Veil.Cef.WpfSmoke.csproj -c Release -o "$build/Release"
node tests/cef/wpf-smoke.mjs "$build/Release/veil-cef-wpf.exe" $vaultExe
```

The fixture compiles the desktop product's `IBrowserEngine` and `IBrowserEngineView` contracts and `NetworkSettings` type from their original source files. `CefDevelopmentEngine` uses the CEF bootstrap's synthetic profile. It refuses other profile locations, proxy settings and private views. The view handles loopback HTTP navigation and history. Privileged internal pages remain unavailable in the fixture.

The automated test submits addresses through the WPF click handler. It verifies cancellation before the server receives a request, preservation of the navigation ID through a redirect, successful and failed load completions, back and forward navigation, and the order of WPF window, CEF browser, and dispatcher shutdown. It also exercises refusal of a different profile, proxy settings, a private view and a non-loopback address.

The CEF adapter serializes top-level fixture loads and correlates their completion by URL. Restored history entries can update the title without load callbacks. Concurrent loads, site isolation and product policy integration need separate verification. An earlier sampled WPF run observed three restricted renderer tokens and no remaining fixture processes. The VEIL desktop product still uses WebView2.

The CEF fixture denies downloads and permission prompts through native handlers. The smoke page requests geolocation and camera access, then triggers loopback HTTP attachment and Blob downloads. Only the automated fixture provides a fake camera device so the test can exercise permission denial without physical hardware. The test checks both permission results and the matching native rejection callbacks.

The response probe compares bytes in memory on two loopback GET requests. The inline response matched all 98,304 bytes in both the native CEF filter and the page. For a delayed 98,304-byte attachment, the filter matched 16,384 bytes before cancellation in two runs. Both requests were sent once. An earlier non-delayed run delivered more attachment bytes, so a denied download cannot provide a reliable complete stream for the vault.

The smoke runner watches its own temporary directory for the fixture download names and `.crdownload` files. It also scans that directory after shutdown. No matching artifacts were observed. This measurement is limited to that directory and those names. Kernel file tracing and product vault integration remain unverified.

The WPF regression passes a built repository `veil-vault.exe` as `$vaultExe`. A test-only `CefURLRequest` starts each loopback GET once, rejects redirects, disables caching and disables retries for 5xx responses. The fixture delivers chunks of up to 16 KiB through a bounded eight-entry queue to the existing Rust vault protocol. The saturation fixture pauses the vault reader, fills all eight queue entries, and checks that CEF reports request cancellation and Rust publishes no object. The full 768 KiB attachment produces one encrypted object, and decrypted bytes match the source. The truncated attachment reaches the vault with partial data and aborts without publishing an object.

The full-response case starts with a scripted click on a known link in the loopback fixture page. The native host cancels that top-level GET navigation in `OnBeforeBrowse` and then issues one explicit `CefURLRequest` for the same target. The server checks that only the native handoff reaches HTTP. The fixture uses a predeclared route and does not transfer cookies, request headers, or POST data.

### Redirect rejection regression (pending Windows execution)

The owned GET fixture now also attempts a zero-body HTTP 302 to a distinct attachment route. The expected outcome is no request to the redirect target, zero bytes delivered to the vault, and no published encrypted object. All owned fixture URLs must match the current loopback page origin (including port). This is a fixture-level constraint, not a general browser network policy. Windows build and runtime results: NOT RUN at authoring time.
