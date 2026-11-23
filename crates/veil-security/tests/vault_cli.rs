//! Phase 4-0: the vault bridge publishes only complete, length-checked download streams.
use std::fs;
use std::io::Write;
use std::path::Path;
use std::process::{Command, Stdio};

const PASS: &str = "synthetic-vault-test-passphrase";

fn vault(args: &[&str], stdin: &[u8]) -> (bool, String) {
    let mut child = Command::new(env!("CARGO_BIN_EXE_veil-vault"))
        .args(args)
        .stdin(Stdio::piped())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .expect("spawn veil-vault");
    child.stdin.take().unwrap().write_all(stdin).unwrap();
    let output = child.wait_with_output().unwrap();
    (output.status.success(), String::from_utf8_lossy(&output.stdout).into_owned())
}

fn framed(data: &[u8], frame: usize, terminate: bool, claimed_total: Option<u64>) -> Vec<u8> {
    let mut stream = format!("{PASS}\n").into_bytes();
    for chunk in data.chunks(frame) {
        stream.extend((chunk.len() as u32).to_le_bytes());
        stream.extend(chunk);
    }
    if terminate {
        stream.extend(0u32.to_le_bytes());
        stream.extend(claimed_total.unwrap_or(data.len() as u64).to_le_bytes());
    }
    stream
}

fn setup(name: &str) -> (tempfile::TempDir, String) {
    let dir = tempfile::Builder::new().prefix(name).tempdir().unwrap();
    let keyring = dir.path().join("keyring.age").to_string_lossy().into_owned();
    let (ok, _) = vault(&["init", &keyring], format!("{PASS}\n").as_bytes());
    assert!(ok, "init");
    (dir, keyring)
}

fn leftovers(dir: &Path) -> Vec<String> {
    fs::read_dir(dir)
        .unwrap()
        .map(|entry| entry.unwrap().file_name().to_string_lossy().into_owned())
        .filter(|name| name != "keyring.age")
        .collect()
}

#[test]
fn complete_stream_is_sealed_and_verifies() {
    let (dir, keyring) = setup("vault-ok");
    let data: Vec<u8> = (0..3_000_000u32).map(|i| (i % 251) as u8).collect();
    let out = dir.path().join("item.veil").to_string_lossy().into_owned();
    let (ok, stdout) = vault(&["seal", &keyring, &out], &framed(&data, 65_536, true, None));
    assert!(ok, "seal");
    assert!(stdout.contains("\"bytes\":3000000"));
    let ciphertext = fs::read(&out).unwrap();
    assert!(!ciphertext.windows(64).any(|window| window == &data[1000..1064]), "plaintext visible in vault file");
    let (ok, stdout) = vault(&["verify", &keyring, &out], format!("{PASS}\n").as_bytes());
    assert!(ok && stdout.contains("\"bytes\":3000000"));
}

#[test]
fn interrupted_stream_is_not_published() {
    let (dir, keyring) = setup("vault-cut");
    let data = vec![7u8; 500_000];
    let out = dir.path().join("item.veil").to_string_lossy().into_owned();
    // Cut mid-frame and also cleanly between frames without the terminator.
    let mut cut = framed(&data, 65_536, false, None);
    cut.truncate(cut.len() - 1000);
    for stream in [cut, framed(&data, 65_536, false, None)] {
        let (ok, _) = vault(&["seal", &keyring, &out], &stream);
        assert!(!ok, "a truncated download must fail");
        assert!(leftovers(dir.path()).is_empty(), "no object or staging file may remain: {:?}", leftovers(dir.path()));
    }
}

#[test]
fn length_mismatch_and_wrong_passphrase_fail() {
    let (dir, keyring) = setup("vault-bad");
    let data = vec![1u8; 10_000];
    let out = dir.path().join("item.veil").to_string_lossy().into_owned();
    let (ok, _) = vault(&["seal", &keyring, &out], &framed(&data, 4096, true, Some(9_999)));
    assert!(!ok, "length mismatch must fail");
    assert!(leftovers(dir.path()).is_empty());
    let mut wrong = framed(&data, 4096, true, None);
    wrong.splice(0..PASS.len(), b"not-the-passphrase-for-this-ring".iter().copied());
    let (ok, _) = vault(&["seal", &keyring, &out], &wrong);
    assert!(!ok, "wrong passphrase must fail");
    assert!(leftovers(dir.path()).is_empty());
}
