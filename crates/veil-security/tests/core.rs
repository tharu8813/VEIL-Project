use std::fs;
use std::io::{self, Read};
use std::process::Command;
use std::thread;
use std::time::{Duration, Instant};
use veil_security::{Keyring, Purpose, SecretString};

const MARKER: &[u8] = b"VEIL_SYNTHETIC_PHASE2_TEST_9527";
const MB: u64 = 1024 * 1024;
const AGE_HEADER: &[u8] = b"age-encryption.org/v1\n";

fn passphrase() -> SecretString {
    "synthetic passphrase used only in Phase 2 tests".into()
}

fn marker_absent(bytes: &[u8]) {
    assert!(!bytes.windows(MARKER.len()).any(|window| window == MARKER));
}

#[test]
fn envelope_unlocks_and_backup_reopens_with_separate_keys() {
    let directory = tempfile::tempdir().unwrap();
    let root = directory.path();
    let envelope = root.join("keys.age");
    let backup_envelope = root.join("copied-keys.age");
    assert!(Keyring::create(&envelope, "".into()).is_err());
    assert!(!envelope.exists());
    let keys = Keyring::create(&envelope, passphrase()).unwrap();
    let profile = root.join("profile.age");
    let backup = root.join("backup.age");
    keys.seal_new(Purpose::Profile, &profile, MARKER).unwrap();
    keys.seal_new(Purpose::Backup, &backup, MARKER).unwrap();
    drop(keys);
    let key_bytes = fs::read(&envelope).unwrap();
    assert!(key_bytes.starts_with(AGE_HEADER));
    assert!(
        !key_bytes
            .windows(b"AGE-SECRET-KEY".len())
            .any(|x| x == b"AGE-SECRET-KEY")
    );
    fs::copy(&envelope, &backup_envelope).unwrap();
    assert!(Keyring::unlock(&backup_envelope, "wrong test passphrase".into()).is_err());
    let restored = Keyring::unlock(&backup_envelope, passphrase()).unwrap();
    assert_eq!(
        &restored.read_small(Purpose::Profile, &profile).unwrap()[..],
        MARKER
    );
    assert_eq!(
        &restored.read_small(Purpose::Backup, &backup).unwrap()[..],
        MARKER
    );
    assert!(restored.verify(Purpose::Download, &profile).is_err());
    assert!(restored.verify(Purpose::Profile, &backup).is_err());
    assert!(Keyring::create(&envelope, passphrase()).is_err());
    assert_eq!(fs::read(&envelope).unwrap(), key_bytes);
    let mut altered = key_bytes;
    *altered.last_mut().unwrap() ^= 1;
    fs::write(root.join("altered-keys.age"), altered).unwrap();
    assert!(Keyring::unlock(&root.join("altered-keys.age"), passphrase()).is_err());
}

#[test]
fn unsupported_key_schema_and_excessive_kdf_cost_are_rejected() {
    let directory = tempfile::tempdir().unwrap();
    let envelope = directory.path().join("keys.age");
    let mut recipient = age::scrypt::Recipient::new(passphrase());
    recipient.set_work_factor(17);
    let ciphertext = age::encrypt(&recipient, b"VEIL-KEYRING-2\n").unwrap();
    fs::write(&envelope, &ciphertext).unwrap();
    let Some(error) = Keyring::unlock(&envelope, passphrase()).err() else {
        panic!("accepted an unsupported key schema");
    };
    assert_eq!(error.to_string(), "Unsupported key envelope version");

    // Alter the work factor presented before authentication without performing that KDF.
    let mut expensive = ciphertext;
    let end_of_stanza = expensive[AGE_HEADER.len()..]
        .iter()
        .position(|byte| *byte == b'\n')
        .unwrap()
        + AGE_HEADER.len();
    assert_eq!(&expensive[end_of_stanza - 3..end_of_stanza], b" 17");
    expensive[end_of_stanza - 2..end_of_stanza].copy_from_slice(b"31");
    fs::write(&envelope, expensive).unwrap();
    assert!(Keyring::unlock(&envelope, passphrase()).is_err());
}

#[test]
fn streams_large_files_and_rejects_modified_truncated_and_reordered_chunks() {
    let directory = tempfile::tempdir().unwrap();
    let root = directory.path();
    let keys = Keyring::create(&root.join("keys.age"), passphrase()).unwrap();
    let large = root.join("large.age");
    let size = 64 * MB + 37;
    let source = MARKER.chain(io::repeat(0xe5).take(size - MARKER.len() as u64));
    keys.seal_new(Purpose::Download, &large, source).unwrap();
    assert_eq!(keys.verify(Purpose::Download, &large).unwrap(), size);
    assert!(keys.read_small(Purpose::Download, &large).is_err());
    let sample = root.join("sample.age");
    let input = MARKER.chain(io::repeat(0xe5).take(3 * MB));
    keys.seal_new(Purpose::Download, &sample, input).unwrap();
    let bytes = fs::read(&sample).unwrap();
    assert!(bytes.starts_with(AGE_HEADER));
    marker_absent(&bytes);
    let bad = root.join("bad.age");
    let mut tampered = bytes.clone();
    tampered[1024] ^= 1;
    fs::write(&bad, tampered).unwrap();
    assert!(keys.verify(Purpose::Download, &bad).is_err());
    fs::write(&bad, &bytes[..bytes.len() - 1]).unwrap();
    assert!(keys.verify(Purpose::Download, &bad).is_err());
    let mut appended = bytes.clone();
    appended.push(0);
    fs::write(&bad, appended).unwrap();
    assert!(keys.verify(Purpose::Download, &bad).is_err());
    let mut reordered = bytes.clone();
    let chunk = 64 * 1024 + 16;
    for index in 0..chunk {
        reordered.swap(1024 + index, 1024 + chunk + index);
    }
    fs::write(&bad, reordered).unwrap();
    assert!(keys.verify(Purpose::Download, &bad).is_err());
    fs::write(
        &bad,
        [AGE_HEADER, b"-> X25519 ", &vec![b'a'; 4096]].concat(),
    )
    .unwrap();
    assert!(keys.verify(Purpose::Download, &bad).is_err());
    let empty = root.join("empty.age");
    keys.seal_new(Purpose::Download, &empty, io::empty())
        .unwrap();
    assert!(
        keys.read_small(Purpose::Download, &empty)
            .unwrap()
            .is_empty()
    );
}

#[test]
fn write_error_and_destination_collision_preserve_existing_ciphertext() {
    struct ReadError;
    impl Read for ReadError {
        fn read(&mut self, _: &mut [u8]) -> io::Result<usize> {
            Err(io::Error::other("synthetic reader failure"))
        }
    }
    let directory = tempfile::tempdir().unwrap();
    let root = directory.path();
    let keys = Keyring::create(&root.join("keys.age"), passphrase()).unwrap();
    let failed = root.join("failed.age");
    let source = MARKER.chain(io::repeat(0x43).take(MB)).chain(ReadError);
    assert!(keys.seal_new(Purpose::Download, &failed, source).is_err());
    assert!(!failed.exists());
    assert_eq!(fs::read_dir(root).unwrap().count(), 1);
    let target = root.join("committed.age");
    thread::scope(|scope| {
        let first = scope.spawn(|| keys.seal_new(Purpose::Download, &target, MARKER));
        let second =
            scope.spawn(|| keys.seal_new(Purpose::Download, &target, &b"another value"[..]));
        let (first, second) = (first.join().unwrap(), second.join().unwrap());
        assert!(first.is_ok() ^ second.is_ok());
        let accepted = keys.read_small(Purpose::Download, &target).unwrap();
        assert_eq!(
            &accepted[..],
            if first.is_ok() {
                MARKER
            } else {
                b"another value"
            }
        );
    });
    assert_eq!(fs::read_dir(root).unwrap().count(), 2);
}

#[test]
fn forced_termination_never_publishes_partial_ciphertext() {
    let directory = tempfile::tempdir().unwrap();
    let root = directory.path();
    let keys = Keyring::create(&root.join("keys.age"), passphrase()).unwrap();
    let previous = root.join("previous.age");
    keys.seal_new(Purpose::Download, &previous, MARKER).unwrap();
    let mut child = Command::new(std::env::current_exe().unwrap())
        .args(["--exact", "interrupted_seal_child", "--ignored"])
        .env("VEIL_TEST_INTERRUPTED_DIRECTORY", root)
        .spawn()
        .unwrap();
    let limit = Instant::now() + Duration::from_secs(15);
    while !root.join("ready").exists() && Instant::now() < limit {
        if child.try_wait().unwrap().is_some() {
            break;
        }
        thread::sleep(Duration::from_millis(20));
    }
    let _ = child.kill();
    assert!(!child.wait().unwrap().success());
    assert!(
        root.join("ready").exists(),
        "child did not reach the partial write"
    );
    assert!(!root.join("interrupted.age").exists());
    let parts: Vec<_> = fs::read_dir(root)
        .unwrap()
        .map(|entry| entry.unwrap().path())
        .filter(|path| {
            path.file_name()
                .unwrap()
                .to_string_lossy()
                .starts_with(".veil-encrypted-")
        })
        .collect();
    assert_eq!(parts.len(), 1);
    let bytes = fs::read(&parts[0]).unwrap();
    assert!(bytes.len() > 64 * 1024 && bytes.starts_with(AGE_HEADER));
    marker_absent(&bytes);
    assert!(keys.verify(Purpose::Download, &parts[0]).is_err());
    assert_eq!(
        &keys.read_small(Purpose::Download, &previous).unwrap()[..],
        MARKER
    );
}

#[test]
#[ignore = "launched in a child process by the interruption test"]
fn interrupted_seal_child() {
    struct Stall(std::path::PathBuf);
    impl Read for Stall {
        fn read(&mut self, _: &mut [u8]) -> io::Result<usize> {
            fs::write(&self.0, b"ready")?;
            loop {
                thread::park();
            }
        }
    }
    let root =
        std::path::PathBuf::from(std::env::var_os("VEIL_TEST_INTERRUPTED_DIRECTORY").unwrap());
    let keys = Keyring::unlock(&root.join("keys.age"), passphrase()).unwrap();
    let source = MARKER
        .chain(io::repeat(0x32).take(MB))
        .chain(Stall(root.join("ready")));
    keys.seal_new(Purpose::Download, &root.join("interrupted.age"), source)
        .unwrap();
}
