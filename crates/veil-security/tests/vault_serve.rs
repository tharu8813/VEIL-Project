//! ADR-0005: long-lived vault session — persistence, recovery code, rotation and authenticated export.
use std::fs;
use std::io::{BufRead, BufReader, Read, Write};
use std::path::Path;
use std::process::{Child, ChildStdin, ChildStdout, Command, Stdio};

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
    assert_eq!(names(vault), ["a.meta.veil", "a.veil", "keyring.age", "keyring.recovery.age"]);

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
    let mut tampered = fs::read(&object).unwrap();
    let middle = tampered.len() / 2;
    tampered[middle] ^= 0x40;
    fs::write(vault.join("t.veil"), tampered).unwrap();
    assert!(s.command(&format!("export\t{}\t{}", vault.join("t.veil").display(), out.join("t.bin").display())).starts_with("err\t"));
    assert_eq!(names(&out), ["a.bin"], "a tampered export must leave no file");

    // Rotate the passphrase and reissue the recovery code.
    assert_eq!(s.command("rewrap\tsecond-pass"), "ok");
    assert_eq!(s.command("rewrap-recovery\tNEWCO-DE222-ABCDE"), "ok");
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
