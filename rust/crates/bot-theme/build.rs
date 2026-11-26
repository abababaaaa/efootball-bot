//! 从 C# Themes/Tokens.xaml 生成 Rust 常量（bot-theme 库用）。
//! Slint 六表由 bot-ui/build.rs 从 themes.json 生成到 ui/theme_generated.slint。

use std::env;
use std::fs;
use std::path::PathBuf;

fn normalize_color(raw: &str) -> String {
    let s = raw.trim();
    if s.len() == 9 && s.starts_with('#') && s[1..3].eq_ignore_ascii_case("ff") {
        format!("#{}", &s[3..])
    } else {
        s.to_string()
    }
}

fn main() {
    println!("cargo:rerun-if-changed=../../../src/EfootballBot/Themes/Tokens.xaml");

    let here = PathBuf::from(env::var("CARGO_MANIFEST_DIR").expect("CARGO_MANIFEST_DIR"));
    let out = PathBuf::from(env::var("OUT_DIR").expect("OUT_DIR"));
    let xaml = here.join("../../../src/EfootballBot/Themes/Tokens.xaml");
    let text = fs::read_to_string(&xaml).unwrap_or_default();

    let mut pairs: Vec<(String, String)> = Vec::new();
    for line in text.lines() {
        let line = line.trim();
        if !line.contains("<SolidColorBrush") && !line.starts_with("<Color") { continue; }
        let key = match extract_attr(line, "x:Key") { Some(k) => k, None => continue };
        let color = if let Some(c) = extract_attr(line, "Color") {
            normalize_color(&c)
        } else {
            let after = line.split_once('>').map(|(_, r)| r).unwrap_or("");
            normalize_color(after.split('<').next().unwrap_or("").trim())
        };
        if color.starts_with('#') { pairs.push((key, color)); }
    }

    let mut rs = String::from("// @generated from Tokens.xaml — 请勿手动编辑\n\n");
    rs.push_str("pub const TOKEN_DEFAULTS: &[(&str, &str)] = &[\n");
    for (k, v) in &pairs { rs.push_str(&format!("    ({:?}, {:?}),\n", k, v)); }
    rs.push_str("];\npub const TOKEN_NAMES: &[&str] = &[\n");
    for (k, _) in &pairs { rs.push_str(&format!("    {:?},\n", k)); }
    rs.push_str("];\n");
    rs.push_str(&format!("pub const TOKEN_COUNT: usize = {};\n", pairs.len()));
    fs::write(out.join("tokens_generated.rs"), rs).expect("写 tokens_generated.rs 失败");
}

fn extract_attr(line: &str, name: &str) -> Option<String> {
    let needle = format!("{}=", name);
    let start = line.find(&needle)? + needle.len();
    let rest = &line[start..];
    let quote = rest.chars().next()?;
    if quote != '"' && quote != '\'' { return None; }
    let end = rest[1..].find(quote)? + 1;
    Some(rest[1..end].to_string())
}
