# 32. PDF raster preview (Stage 3-1b)

Status: **Debug-only experimental implementation**. Release PDF preview remains disabled until the isolated Windows VM tests and renderer hardening are complete.

- Authenticated vault object read via existing 8 MiB preview pipe; non-recovered .pdf only, fixed PDF signature, page count <=200, finite page dimensions, aspect ratio <=50, raster side <=1800, encoded output <=8 MiB.
- Windows.Data.Pdf LoadFromStreamAsync and RenderToStreamAsync use in-memory WinRT streams. No WebView, shell viewer, file paths, hyperlink actions, document text extraction, print, or export. The parent receives one PNG raster at a time, checks its size and decodes only pixels.
- Each page uses a child of the Debug desktop executable, a fixed bounded binary stdin/stdout protocol, no PDF data in arguments; parent 15s deadline, child 20s watchdog, cancellation and kill on close. The worker bypasses browser/profile initialization but still refuses elevated execution. It is **not** an AppContainer, low-integrity token or network sandbox; WinRT parser remains an attack surface. No claim of parser exploit containment, complete memory wiping or absence of OS cache/pagefile/dump writes.
- Target framework exposes Windows 10 19041 SDK API contracts; existing local build output path is preserved. No new explicit third-party package.
- Actual Windows raster, malformed/password-protected PDF, hung worker, parent close, output size, protected profile, network and write trace tests NOT RUN. Before Release enablement require OS-level worker isolation/resource limits and VM evidence; no release gate change.

Microsoft API references: https://learn.microsoft.com/uwp/api/windows.data.pdf.pdfdocument.loadfromstreamasync and https://learn.microsoft.com/uwp/api/windows.data.pdf.pdfpage.rendertostreamasync.
