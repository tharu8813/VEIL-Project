//! ADR-0005: long-lived vault session — persistence, recovery code, rotation and authenticated export.
use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::Path;
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};
use veil_security::{Keyring, Purpose, SecretString, vault_backup, vault_credentials, vault_generation};

struct Session {
    child: Child,
    stdin: ChildStdin,
    stdout: BufReader<ChildStdout>,
}

impl Session {
    fn start(dir: &Path, first: &str) -> (Self, String) {
        let mut child = Command::new(env!("CARGO_BIN_EXE_veil-vault"))
            .args(["serve", dir.to_str().unwrap()])
            .stdin(Stdio::piped())
            .stdout(Stdio::piped())
            .stderr(Stdio::null())
            .spawn()
            .unwrap();
        let stdin = child.stdin.take().unwrap();
        let stdout = BufReader::new(child.stdout.take().unwrap());
        let mut session = Self { child, stdin, stdout };
        let reply = session.command(first);
        (session, reply)
    }

    fn command(&mut self, line: &str) -> String {
        writeln!(self.stdin, "{line}").unwrap();
        self.stdin.flush().unwrap();
        self.reply()
    }

    fn reply(&mut self) -> String {
        let mut line = String::new();
        self.stdout.read_line(&mut line).unwrap();
        line.trim_end().to_owned()
    }

    fn seal(&mut self, path: &Path, data: &[u8], abort_after: Option<usize>) -> String {
        writeln!(self.stdin, "seal\t{}", path.display()).unwrap();
        for (index, chunk) in data.chunks(8192).enumerate() {
            if abort_after == Some(index) {
                self.stdin.write_all(&u32::MAX.to_le_bytes()).unwrap();
                self.stdin.flush().unwrap();
                return self.reply();
            }
            self.stdin.write_all(&(chunk.len() as u32).to_le_bytes()).unwrap();
            self.stdin.write_all(chunk).unwrap();
        }
        self.stdin.write_all(&0u32.to_le_bytes()).unwrap();
        self.stdin.write_all(&(data.len() as u64).to_le_bytes()).unwrap();
        self.stdin.flush().unwrap();
        self.reply()
    }

    fn read(&mut self, path: &Path) -> Vec<u8> {
        let header = self.command(&format!("read\t{}", path.display()));
        let length: usize = header.strip_prefix("ok\t").expect("read ok").parse().unwrap();
        let mut bytes = vec![0; length];
        self.stdout.read_exact(&mut bytes).unwrap();
        bytes
    }

    fn quit(mut self) {
        writeln!(self.stdin, "quit").unwrap();
        self.child.wait().unwrap();
    }
}

fn names(dir: &Path) -> Vec<String> {
    let mut names: Vec<String> = fs::read_dir(dir).unwrap().map(|e| e.unwrap().file_name().to_string_lossy().into_owned()).collect();
    names.sort();
    names
}

#[test]
fn persistent_vault_with_recovery_rotation_and_export() {
    let dir = tempfile::tempdir().unwrap();
    let vault = dir.path();
    let data: Vec<u8> = (0..200_000u32).map(|i| (i % 253) as u8).collect();
    let object = vault.join("a.veil");
    let meta = vault.join("a.meta.veil");

    // Create: passphrase + recovery code, then store an object and its metadata.
    let (mut s, reply) = Session::start(vault, "create\tfirst-pass\tRECOV-ERYCO-DE111");
    assert_eq!(reply, "ok");
    assert_eq!(s.seal(&object, &data, None), "ok\t200000");
    assert_eq!(s.seal(&meta, br#"{"name":"a.bin"}"#, None), "ok\t16");
    // An aborted object is not published and the session keeps working.
    assert!(s.seal(&vault.join("aborted.veil"), &data, Some(3)).starts_with("err\t"));
    assert!(!vault.join("aborted.veil").exists());
    assert_eq!(s.read(&meta), br#"{"name":"a.bin"}"#);
    s.quit();
    assert_eq!(names(vault), [".vault-credentials.lock", ".vault-session.lock", "a.meta.veil", "a.veil", "keyring.bundle"]);

    // A new process opens the same vault; wrong passphrase is refused.
    let (s, reply) = Session::start(vault, "unlock\twrong-pass");
    assert!(reply.starts_with("err\t"));
    drop(s);
    let (mut s, reply) = Session::start(vault, "unlock\tfirst-pass");
    assert_eq!(reply, "ok");

    // Export appears only complete; never overwrites; nothing is left on failure.
    let out = dir.path().join("exports");
    fs::create_dir(&out).unwrap();
    assert_eq!(s.command(&format!("export\t{}\t{}", object.display(), out.join("a.bin").display())), "ok\t200000");
    assert_eq!(fs::read(out.join("a.bin")).unwrap(), data);
    assert!(s.command(&format!("export\t{}\t{}", object.display(), out.join("a.bin").display())).starts_with("err\t"));
    assert_eq!(fs::read(out.join("a.bin")).unwrap(), data, "export collision preserves existing bytes");
    let mut tampered = fs::read(&object).unwrap();
    let middle = tampered.len() / 2;
    tampered[middle] ^= 0x40;
    fs::write(vault.join("t.veil"), tampered).unwrap();
    assert!(s.command(&format!("export\t{}\t{}", vault.join("t.veil").display(), out.join("t.bin").display())).starts_with("err\t"));
    assert_eq!(names(&out), ["a.bin"], "a tampered export must leave no file");

    // Rotate the passphrase and reissue the recovery code.
    let credentials = vault.join("keyring.bundle");
    let original = fs::read(&credentials).unwrap();
    assert!(s.command("rewrap-both\tsecond-pass\t").starts_with("err\t"));
    assert_eq!(fs::read(&credentials).unwrap(), original);
    assert_eq!(s.command("rewrap-both\tsecond-pass\tNEWCO-DE222-ABCDE"), "ok");
    s.quit();
    for (first, ok) in [
        ("unlock\tfirst-pass", false),
        ("recover\tRECOV-ERYCO-DE111", false),
        ("unlock\tsecond-pass", true),
        ("recover\tNEWCO-DE222-ABCDE", true),
    ] {
        let (mut s, reply) = Session::start(vault, first);
        assert_eq!(reply == "ok", ok, "{first}");
        if ok {
            assert_eq!(s.command(&format!("verify\t{}", object.display())), "ok\t200000");
            s.quit();
        }
    }
    assert!(names(vault).iter().all(|name| !name.starts_with(".veil-")), "no staging leftovers: {:?}", names(vault));
}

#[test]
fn legacy_envelopes_survive_a_failed_rotation_and_migrate_on_success() {
    let dir = tempfile::tempdir().unwrap();
    let vault = dir.path();
    let primary = vault.join("keyring.age");
    let recovery = vault.join("keyring.recovery.age");
    let secret = |value: &str| SecretString::from(value.to_owned());
    let keys = Keyring::create(&primary, secret("legacy-passphrase")).unwrap();
    keys.write_envelope(&recovery, secret("legacy-recovery"), false).unwrap();
    let previous = fs::read(&primary).unwrap();
    let previous_recovery = fs::read(&recovery).unwrap();
    let keys = vault_credentials::unlock(vault, secret("legacy-recovery"), true).unwrap();

    // The first envelope can be produced before the second fails. Neither is published.
    assert!(vault_credentials::replace(vault, &keys, Some(secret("new-passphrase")), Some(secret(""))).is_err());
    assert_eq!(fs::read(&primary).unwrap(), previous);
    assert_eq!(fs::read(&recovery).unwrap(), previous_recovery);

    // Migrating a single-slot edit preserves the other credential.
    let (mut session, reply) = Session::start(vault, "unlock\tlegacy-passphrase");
    assert_eq!(reply, "ok");
    assert_eq!(session.command("rewrap-recovery\tnew-code"), "ok");
    assert_eq!(session.command("rewrap\tnew-passphrase"), "ok");
    session.quit();
    assert!(!primary.exists() && !recovery.exists());
    // Simulate a stop after publication but before removal of the previous files.
    fs::write(&primary, previous).unwrap();
    fs::write(&recovery, previous_recovery).unwrap();
    assert!(vault_credentials::unlock(vault, secret("legacy-recovery"), true).is_err());
    assert!(vault_credentials::unlock(vault, secret("new-passphrase"), false).is_ok());
    assert!(!primary.exists() && !recovery.exists());
    assert!(vault_credentials::unlock(vault, secret("new-code"), true).is_ok());
}

#[test]
fn backup_restores_authenticated_objects_and_preserves_destination_on_failure() {
    let run = tempfile::tempdir().unwrap();
    let source = run.path().join("vault");
    fs::create_dir_all(source.join("objects")).unwrap();
    fs::create_dir(source.join("meta")).unwrap();
    let keys = Keyring::generate();
    let name = "1234567890abcdef1234567890abcdef.veil";
    let payload = vec![42; 200_000];
    let metadata = b"{\"name\":\"private filename\"}";
    keys.seal_new(Purpose::Download, &source.join("objects").join(name), payload.as_slice()).unwrap();
    keys.seal_new(Purpose::Download, &source.join("meta").join(name), metadata.as_slice()).unwrap();
    let password = || SecretString::from("synthetic-backup-pass".to_owned());
    let code = || SecretString::from("synthetic-restored-code".to_owned());
    let archive = run.path().join("backup.veil-backup");
    assert_eq!(vault_backup::export(&keys, &source, &archive, password()).unwrap(), 1);
    let data = fs::read(&archive).unwrap();
    assert!(!data.windows(metadata.len()).any(|window| window == metadata));
    assert!(!data.windows(32).any(|window| window == &payload[..32]));
    assert!(vault_backup::export(&keys, &source, &archive, password()).is_err());
    assert_eq!(fs::read(&archive).unwrap(), data);

    let target = run.path().join("restored");
    assert!(vault_backup::restore(&archive, &target, SecretString::from("wrong"), code()).is_err());
    assert!(!target.exists());
    let corrupted = run.path().join("bad.veil-backup");
    let mut damaged = data.clone();
    let offset = damaged.len() - 12;
    damaged[offset] ^= 1;
    for bytes in [damaged.as_slice(), &data[..data.len() - 12]] {
        fs::write(&corrupted, bytes).unwrap();
        assert!(vault_backup::restore(&corrupted, &target, password(), code()).is_err());
        assert!(!target.exists());
        assert!(names(run.path()).iter().all(|name| !name.starts_with(".veil-restore-")));
    }
    vault_backup::restore(&archive, &target, password(), code()).unwrap();
    let reopened = vault_credentials::unlock(&target, code(), true).unwrap();
    assert_eq!(&*reopened.read_small(Purpose::Download, &target.join("objects").join(name)).unwrap(), &payload);
    assert_eq!(&*reopened.read_small(Purpose::Download, &target.join("meta").join(name)).unwrap(), metadata);
    assert!(vault_backup::restore(&archive, &target, password(), code()).is_err());
    assert!(target.join("keyring.bundle").is_file());
    fs::write(source.join("meta").join(name), b"damaged metadata").unwrap();
    let failed_backup = run.path().join("failed.veil-backup");
    assert!(vault_backup::export(&keys, &source, &failed_backup, password()).is_err());
    assert!(!failed_backup.exists());
}

#[test]
fn content_rotation_rejects_corruption_and_rekeys_downloads_and_backups() {
    let run = tempfile::tempdir().unwrap();
    let base = run.path();
    let password = || SecretString::from("synthetic-content-pass".to_owned());
    let code = || SecretString::from("synthetic-new-code".to_owned());
    let _lock = vault_generation::lock_session(base).unwrap();
    assert!(vault_generation::lock_session(base).is_err());
    for folder in ["objects", "meta", "index"] {
        fs::create_dir(base.join(folder)).unwrap();
    }
    let keys = vault_credentials::create(base, password(), SecretString::from("synthetic-old-code")).unwrap();
    let filename = "1234567890abcdef1234567890abcdef.veil";
    let old_path = base.join("objects").join(filename);
    keys.seal_new(Purpose::Download, &old_path, &b"content to preserve"[..]).unwrap();
    let ciphertext = fs::read(&old_path).unwrap();
    fs::write(&old_path, b"damaged ciphertext").unwrap();
    assert!(vault_generation::rotate(base, base, &keys, password(), code()).is_err());
    assert_eq!(vault_generation::active(base).unwrap(), base);
    assert!(names(base).iter().all(|name| !name.starts_with("gen-")));
    fs::write(&old_path, &ciphertext).unwrap();
    let (next, next_keys, retired) =
        vault_generation::rotate(base, base, &keys, password(), code()).unwrap();
    assert!(retired);
    assert!(!old_path.exists());
    assert_eq!(vault_generation::active(base).unwrap(), next);
    let new_path = next.join("objects").join(filename);
    assert!(keys.verify(Purpose::Download, &new_path).is_err());
    assert_eq!(&*next_keys.read_small(Purpose::Download, &new_path).unwrap(), b"content to preserve");
    let backup_key_probe = base.join("backup-key-probe.veil");
    next_keys.seal_new(Purpose::Backup, &backup_key_probe, &b"new backup key"[..]).unwrap();
    assert!(keys.verify(Purpose::Backup, &backup_key_probe).is_err());
    assert!(vault_credentials::unlock(&next, code(), true).is_ok());
    assert!(vault_credentials::unlock(&next, password(), false).is_ok());
    assert!(vault_generation::require_no_generations(base).is_err());
}
