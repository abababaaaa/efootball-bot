fn main() {
    // 从 themes.json（由 ThemePalette.cs 解析导出）生成 Slint 六张主题表，
    // 写到 ui/ 供 app.slint include。单一真源是 C# ThemePalette.cs。
    gen_theme::generate(
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("../bot-theme/themes.json"),
        std::path::Path::new(env!("CARGO_MANIFEST_DIR")).join("ui/theme_generated.slint"),
    );
    // 只编译一次：app.slint 顶部 import LogViewer（log_viewer.slint 的导出组件），
    // 这样一个 OUT_DIR 里只有 app.rs，include_modules! 不会因两个 .rs 互相覆盖。
    slint_build::compile("ui/app.slint").expect("编译 app.slint 失败");
}

mod gen_theme {
    use std::fs;
    use std::path::Path;

    pub fn generate(src: impl AsRef<Path>, dst: impl AsRef<Path>) {
        let txt = match fs::read_to_string(src) { Ok(t) => t, Err(_) => return };
        let tables = parse(&txt);
        if tables.is_empty() { return; }
        let mut keys: Vec<String> = tables.iter().flat_map(|(_, m)| m.keys().cloned()).collect();
        keys.sort(); keys.dedup();
        let mut out = String::from("// @generated from ThemePalette.cs — 请勿手动编辑\n");
        out.push_str("export struct ThemeColors {\n");
        for k in &keys { out.push_str(&format!("    {}: color,\n", ident(k))); }
        out.push_str("}\n\n");
        // Slint 无顶层 const：用一个 global 暴露六张表（只读 out property）
        out.push_str("export global Palette {\n");
        for (name, map) in &tables {
            out.push_str(&format!("    out property <ThemeColors> {}: {{\n", name));
            for k in &keys {
                let v = map.get(k).map(|s| norm(s)).unwrap_or_else(|| "#00000000".into());
                out.push_str(&format!("        {}: {},\n", ident(k), v));
            }
            out.push_str("    };\n");
        }
        out.push_str("}\n");
        fs::write(dst, out).expect("写 theme_generated.slint 失败");
    }

    fn norm(s: &str) -> String {
        let s = s.trim();
        if s.len() == 9 && s.starts_with('#') && s[1..3].eq_ignore_ascii_case("ff") { format!("#{}", &s[3..]) } else { s.into() }
    }
    fn ident(s: &str) -> String {
        let mut o = String::new();
        for (i, c) in s.chars().enumerate() {
            if c.is_uppercase() && i > 0 { o.push('_'); }
            o.push(c.to_ascii_lowercase());
        }
        o
    }
    fn parse(s: &str) -> Vec<(String, std::collections::HashMap<String, String>)> {
        use std::collections::HashMap;
        let b = s.as_bytes(); let mut i = 0; let mut out = Vec::new();
        ws(b, &mut i); if b.get(i) != Some(&b'{') { return out; } i += 1;
        while i < b.len() {
            ws(b, &mut i); if b.get(i) == Some(&b'}') { break; }
            let name = str(b, &mut i); ws(b, &mut i); if b.get(i) == Some(&b':') { i += 1; }
            ws(b, &mut i); let mut m = HashMap::new();
            if b.get(i) == Some(&b'{') {
                i += 1;
                loop {
                    ws(b, &mut i); if b.get(i) == Some(&b'}') { i += 1; break; }
                    let k = str(b, &mut i); ws(b, &mut i); if b.get(i) == Some(&b':') { i += 1; }
                    ws(b, &mut i); let v = str(b, &mut i); m.insert(k, v);
                    ws(b, &mut i); if b.get(i) == Some(&b',') { i += 1; }
                }
            }
            out.push((name, m));
            ws(b, &mut i); if b.get(i) == Some(&b',') { i += 1; }
        }
        out
    }
    fn ws(b: &[u8], i: &mut usize) { while *i < b.len() && b[*i].is_ascii_whitespace() { *i += 1; } }
    fn str(b: &[u8], i: &mut usize) -> String {
        while *i < b.len() && b[*i] != b'"' { *i += 1; }
        if *i >= b.len() { return String::new(); } *i += 1;
        let st = *i;
        while *i < b.len() && b[*i] != b'"' { if b[*i] == b'\\' { *i += 1; } *i += 1; }
        let s = String::from_utf8_lossy(&b[st..*i]).to_string(); *i += 1; s
    }
}
