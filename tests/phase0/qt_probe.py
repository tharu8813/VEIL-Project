"""Synthetic-data-only Qt WebEngine experiment."""
import json
import sys
from pathlib import Path

from PySide6.QtCore import QEvent, QTimer, QUrl, qVersion
from PySide6.QtWebEngineCore import (
    QWebEnginePage,
    QWebEngineProfile,
    qWebEngineChromiumVersion,
)
from PySide6.QtWidgets import QApplication

profile_path, origin, action = sys.argv[1:]
app = QApplication([sys.argv[0]])
profile = QWebEngineProfile("VEIL Phase 0")
profile.setPersistentStoragePath(str(Path(profile_path) / "storage"))
profile.setCachePath(str(Path(profile_path) / "cache"))
profile.setDownloadPath(str(Path(profile_path) / "downloads"))
profile.setPersistentCookiesPolicy(QWebEngineProfile.PersistentCookiesPolicy.AllowPersistentCookies)
page = QWebEnginePage(profile)
result = {}


def finish(value):
    result.update(value)
    app.quit()


def download(request):
    request.cancel()
    finish({"download": request.state().name})


def loaded(ok):
    if not ok:
        finish({"error": "Qt navigation failed"})
        return
    if action == "download":
        page.loadFinished.disconnect(loaded)
        QTimer.singleShot(0, lambda: page.setUrl(QUrl(origin + "/download?kind=get")))
        return
    page.runJavaScript(
        f"probe({json.dumps(action)}).then(r => window.probeResult = JSON.stringify(r), "
        "e => window.probeResult = JSON.stringify({error: String(e)})); null"
    )
    poll()


def poll():
    page.runJavaScript("window.probeResult || ''", checked)


def checked(value):
    if value:
        finish(json.loads(value))
    else:
        QTimer.singleShot(75, poll)


profile.downloadRequested.connect(download)
page.loadFinished.connect(loaded)
QTimer.singleShot(15000, lambda: finish({"error": "Qt experiment timed out"}))
page.setUrl(QUrl(origin))
app.exec()
page.deleteLater()
app.sendPostedEvents(None, QEvent.Type.DeferredDelete)
profile.deleteLater()
app.sendPostedEvents(None, QEvent.Type.DeferredDelete)
print(json.dumps({"engine": f"Qt {qVersion()} Chromium {qWebEngineChromiumVersion()}", **result}))
sys.exit(1 if "error" in result or not result else 0)
