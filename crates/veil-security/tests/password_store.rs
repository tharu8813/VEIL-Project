use std::fs;
use veil_security::{SecretString, password_store};

fn secret() -> SecretString {
    SecretString::from("synthetic-only long passphrase".to_owned())
}

#[test]
fn authenticated_replacement_and_corruption_do_not_overwrite() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("passwords.age");
    let original = br#"{"synthetic":"credential-test-marker"}"#;
    password_store::write(&path, secret(), original.as_slice(), false).unwrap();
    let before = fs::read(&path).unwrap();
    assert!(!before.windows(b"credential-test-marker".len()).any(|w| w == b"credential-test-marker"));
    assert_eq!(password_store::read(&path, secret()).unwrap().as_slice(), original);
    assert!(password_store::write(&path, secret(), b"[]".as_slice(), false).is_err());
    assert!(password_store::write(&path, SecretString::from("wrong".to_owned()), b"[]".as_slice(), true).is_err());
    assert_eq!(fs::read(&path).unwrap(), before);
    password_store::write(&path, secret(), b"[]".as_slice(), true).unwrap();
    assert_eq!(password_store::read(&path, secret()).unwrap().as_slice(), b"[]");
    let mut corrupt = fs::read(&path).unwrap();
    let last = corrupt.len() - 1;
    corrupt[last] ^= 1;
    fs::write(&path, &corrupt).unwrap();
    assert!(password_store::read(&path, secret()).is_err());
    assert!(password_store::write(&path, secret(), b"[]".as_slice(), true).is_err());
    assert_eq!(fs::read(&path).unwrap(), corrupt);
}

#[test]
fn oversized_or_empty_payload_never_publishes() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("passwords.age");
    assert!(password_store::write(&path, secret(), b"".as_slice(), false).is_err());
    assert!(!path.exists());
    let too_large = vec![b'x'; password_store::LIMIT + 1];
    assert!(password_store::write(&path, secret(), too_large.as_slice(), false).is_err());
    assert!(!path.exists());
}


#[test]
fn rotate_authenticates_old_and_preserves_payload() {
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("passwords.age");
    let payload = br#"[{"synthetic":"rotation-test"}]"#;
    let new = || SecretString::from("synthetic replacement passphrase".to_owned());
    password_store::write(&path, secret(), payload.as_slice(), false).unwrap();
    let before = fs::read(&path).unwrap();
    assert!(password_store::rotate(&path, SecretString::from("wrong".to_owned()), new()).is_err());
    assert_eq!(fs::read(&path).unwrap(), before);
    password_store::rotate(&path, secret(), new()).unwrap();
    assert!(password_store::read(&path, secret()).is_err());
    assert_eq!(password_store::read(&path, new()).unwrap().as_slice(), payload);
    let mut corrupt = fs::read(&path).unwrap();
    let last = corrupt.len() - 1;
    corrupt[last] ^= 1;
    fs::write(&path, &corrupt).unwrap();
    assert!(password_store::rotate(&path, new(), secret()).is_err());
    assert_eq!(fs::read(&path).unwrap(), corrupt);
}

#[test]
fn rotation_cli_requires_two_complete_secret_lines() {
    use std::io::Write;
    use std::process::{Command, Stdio};
    let dir = tempfile::tempdir().unwrap();
    let path = dir.path().join("passwords.age");
    password_store::write(&path, secret(), b"[]".as_slice(), false).unwrap();
    let hex = |s: &str| s.as_bytes().iter().map(|b| format!("{b:02x}")).collect::<Vec<_>>().join("");
    let old = hex("synthetic-only long passphrase");
    let new = hex("synthetic cli replacement passphrase");
    let run = |input: String| {
        let mut child = Command::new(env!("CARGO_BIN_EXE_veil-vault"))
            .arg("passwords-rotate").arg(&path)
            .stdin(Stdio::piped()).stdout(Stdio::null()).stderr(Stdio::null())
            .spawn().unwrap();
        child.stdin.take().unwrap().write_all(input.as_bytes()).unwrap();
        child.wait().unwrap().success()
    };
    let before = fs::read(&path).unwrap();
    assert!(!run(format!("{old}\n{new}")));
    assert_eq!(fs::read(&path).unwrap(), before);
    assert!(run(format!("{old}\n{new}\n")));
    assert_eq!(password_store::read(&path, SecretString::from("synthetic cli replacement passphrase".to_owned())).unwrap().as_slice(), b"[]");
}
