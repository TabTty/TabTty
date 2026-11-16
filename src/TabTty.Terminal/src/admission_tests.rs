use super::*;

#[test]
fn printable_ascii_cluster_count_keeps_unicode_boundaries_exact() {
    let unicode = Unicode::new();
    for start in 0..95 {
        let text: Vec<u16> = (0..1024)
            .map(|index| 32 + ((start + index) % 95) as u16)
            .collect();
        assert_eq!(unicode.count_utf16(&text), text.len());
    }
    for (text, expected) in [
        ("", 0),
        (" A  B ~ ", 8),
        ("\r\n", 1),
        ("A\u{7f}B", 3),
        ("\u{600}ABC", 3),
        ("A\u{301}B", 2),
        ("界 A", 3),
        ("\u{1f469}\u{200d}\u{1f4bb} X", 3),
    ] {
        assert_eq!(
            unicode.count_utf16(&text.encode_utf16().collect::<Vec<_>>()),
            expected
        );
    }
    assert_eq!(unicode.count_utf16(&[0xd800, 0xdc00]), 1);
    assert_eq!(unicode.count_utf16(&[0xd800, 0x20]), 2);
}

#[test]
fn prepared_payload_is_owned_and_compared_exactly() {
    let mut model = Model::new(120, 40, 5000, 1);
    model.feed(0, b"before", 0).unwrap();
    let mut payload = *b"ABCDEF";
    let required = model.preflight(0, 0, &payload, 0).unwrap();
    model.set_limit(required).unwrap();
    payload.copy_from_slice(b"\x1b[?6hX");
    assert_eq!(model.admit(0, 0, &payload, 0), Err(UNSUPPORTED));
    assert_eq!(model.feed_bytes, 6);
    assert_eq!(model.sources[0].escape, 0);
}

#[test]
fn prepared_admission_rechecks_allowance_source_and_flags() {
    let mut model = Model::new(120, 40, 5000, 1);
    model.feed(0, b"before", 0).unwrap();
    model.feed(1, b"\x1b[?1049", 0).unwrap();
    let required = model.preflight(0, 0, b"h", 0).unwrap();
    model.set_limit(model.quota_bytes()).unwrap();
    assert_eq!(model.admit(0, 0, b"h", 0), Err(UNSUPPORTED));
    model.set_limit(required).unwrap();
    model.admit(0, 0, b"h", 0).unwrap();
    assert_eq!(model.admit(0, 1, b"h", 0), Err(UNSUPPORTED));
    model.preflight(0, 0, b"h", 0).unwrap();
    assert_eq!(model.admit(0, 0, b"h", 1), Err(UNSUPPORTED));
    assert_eq!(model.sources[1].escape, 2);
}

#[test]
fn prepared_admission_does_not_survive_state_or_capacity_changes() {
    let mut model = Model::new(8, 4, 100, 1);
    let first = model.preflight(0, 0, b"X", 0).unwrap();
    model.resize(1024, 4).unwrap();
    assert!(model.preflight(0, 0, b"X", 0).unwrap() > first);
    model.snapshot().unwrap();
    assert!(!model.prepared.valid);
    model.preflight(0, 0, b"X", 0).unwrap();
    model.export().unwrap();
    assert!(!model.prepared.valid);
    model.preflight(0, 0, b"X", 0).unwrap();
    model.feed(0, b"\x1b[?1049", 0).unwrap();
    assert_eq!(model.admit(0, 0, b"X", 0), Err(UNSUPPORTED));
}

#[test]
fn prepared_admission_is_bounded_and_operation_specific() {
    let mut model = Model::new(120, 40, 5000, 1);
    model.preflight(0, 0, &[b'x'; 1025], 0).unwrap();
    assert!(!model.prepared.valid);
    model.preflight(0, 0, &[b'x'; 1024], 0).unwrap();
    assert!(model.prepared.valid);
    model.preflight(1, 0, &[], 0).unwrap();
    assert_eq!(model.prepared.operation, 1);
    model.complete(0).unwrap();
    assert_eq!(model.admit(0, 0, &[], 0), Err(UNSUPPORTED));
}

fn assert_probe_equal(left: &Probe, right: &Probe) {
    assert_eq!(left.pending_count, right.pending_count);
    assert_eq!(left.pending, right.pending);
    assert_eq!(left.started, right.started);
    assert_eq!(left.escape, right.escape);
    assert_eq!(left.osc, right.osc);
    assert_eq!(left.string_bytes, right.string_bytes);
    assert_eq!(left.parameter_count, right.parameter_count);
    assert_eq!(
        left.parameters[..left.parameter_count],
        right.parameters[..right.parameter_count]
    );
    assert_eq!(left.source_was_graphic, right.source_was_graphic);
    assert_eq!(
        left.source_graphic[..left.source_count],
        right.source_graphic[..right.source_count]
    );
    assert_eq!(left.cluster_active, right.cluster_active);
    assert_eq!(
        left.cluster[..left.cluster_count],
        right.cluster[..right.cluster_count]
    );
    assert_eq!(left.revisits_rows, right.revisits_rows);
}

fn check_preview(model: &Model, payload: &[u8]) {
    let mut bulk = Probe::from_source(&model.sources[0], model);
    let mut scalar = Probe::from_source(&model.sources[0], model);
    let actual = bulk.decode(payload, false);
    let mut expected = Ok(());
    for byte in payload {
        expected = scalar.decode(std::slice::from_ref(byte), false);
        if expected.is_err() {
            break;
        }
    }
    assert_eq!(actual, expected);
    assert_probe_equal(&bulk, &scalar);
}

#[test]
fn ascii_preview_matches_scalar_parser_for_partial_and_independent_sources() {
    let prefixes = vec![
        Vec::new(),
        b"a".to_vec(),
        vec![0xe7],
        b"\x1b[3".to_vec(),
        b"\x1b]0;title".to_vec(),
        b"\x1b]0;title\x1b".to_vec(),
        "\u{600}".as_bytes().to_vec(),
        "\u{600}".repeat(255).into_bytes(),
        "\u{600}".repeat(256).into_bytes(),
        "\u{1f469}\u{200d}".as_bytes().to_vec(),
    ];
    let suffixes = vec![
        b" printable ASCII text ~ ".to_vec(),
        "ABC\u{301}\u{fe0f}\u{fe0e}DEF\r\n".as_bytes().to_vec(),
        b"\x1b[31mHELLO\x1b[0m WORLD\r\n".to_vec(),
        b"\x1b]0;ignored\x07HELLO\x1b[?2004h".to_vec(),
        vec![0x95, 0x8c, b'A', b'B', 0xff, b'C'],
        format!("AB{}", "\u{301}".repeat(256)).into_bytes(),
    ];
    for prefix in &prefixes {
        for other in [&b""[..], &b"Q"[..], "\u{600}".as_bytes()] {
            let mut model = Model::new(120, 40, 5000, 1);
            model.feed(0, prefix, 0).unwrap();
            if !other.is_empty() && model.feed(1, other, 0).is_err() {
                continue;
            }
            for suffix in &suffixes {
                check_preview(&model, suffix);
            }
        }
    }
}

#[test]
fn ascii_preview_matches_scalar_parser_for_deterministic_byte_streams() {
    let mut model = Model::new(120, 40, 5000, 1);
    model.feed(0, "prefix\u{600}".as_bytes(), 0).unwrap();
    let mut seed = 0x5d924177u32;
    for _ in 0..256 {
        let mut bytes = Vec::new();
        for _ in 0..1024 {
            seed = seed.wrapping_mul(1664525).wrapping_add(1013904223);
            bytes.push(if seed & 7 < 6 {
                32 + ((seed >> 8) % 95) as u8
            } else {
                (seed >> 16) as u8
            });
        }
        check_preview(&model, &bytes);
    }
}
