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
