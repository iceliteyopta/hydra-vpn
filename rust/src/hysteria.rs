use percent_encoding::percent_decode_str;
use serde_json::json;

use url::Url;

pub fn config(input: &str) -> Result<serde_json::Value, &'static str> {
    let uri = Url::parse(input.trim()).map_err(|_| "Invalid Hysteria2 link")?;
    if !matches!(uri.scheme(), "hy2" | "hysteria2") {
        return Err("Expected hy2:// or hysteria2://");
    }
    let host = uri
        .host_str()
        .ok_or("Missing server")?
        .trim_matches(['[', ']']);
    let mut auth = uri.username().to_owned();
    if let Some(pass) = uri.password() {
        auth.push(':');
        auth.push_str(pass);
    }
    let auth = percent_decode_str(&auth)
        .decode_utf8()
        .map_err(|_| "Invalid password encoding")?;
    if auth.is_empty() {
        return Err("Missing password");
    }
    let mut tls = json!({"enabled":true});
    let mut outbound = json!({"type":"hysteria2","tag":"VPN","server":host,"server_port":uri.port().unwrap_or(443),"password":auth});
    let mut obfs = String::new();
    let mut obfs_pass = String::new();
    for (key, value) in uri.query_pairs() {
        match key.as_ref() {
            "sni" if value.is_empty() => {}
            "security" if value.eq_ignore_ascii_case("tls") => {}
            // Browser fingerprint hints belong to TCP TLS clients, not the QUIC handshake.
            "fp" => {}
            "alpn" => {
                let protocols: Vec<_> = value.split(',').map(str::trim).collect();
                if protocols.iter().any(|p| p.is_empty() || p.len() > 255) {
                    return Err("Invalid ALPN list");
                }
                tls["alpn"] = json!(protocols);
            }
            "sni" => tls["server_name"] = json!(value),
            "insecure" => {
                tls["insecure"] = json!(match value.as_ref() {
                    "1" | "true" => true,
                    "0" | "false" => false,
                    _ => return Err("Invalid insecure flag"),
                })
            }
            "obfs" => obfs = value.into_owned(),
            "obfs-password" => obfs_pass = value.into_owned(),
            _ => return Err("Unsupported Hysteria2 parameter"),
        }
    }
    if !obfs.is_empty() || !obfs_pass.is_empty() {
        if obfs != "salamander" || obfs_pass.is_empty() {
            return Err("Invalid obfuscation settings");
        }
        outbound["obfs"] = json!({"type":obfs,"password":obfs_pass});
    }
    outbound["tls"] = tls;
    Ok(json!({
        "dns":{"servers":[{"type":"local","tag":"bootstrap"},{"type":"https","tag":"vpn-dns","server":"1.1.1.1","detour":"VPN"}],"final":"vpn-dns"},
        "inbounds":[{"type":"tun","tag":"tun-in","address":["172.19.0.1/30","fdfe:dcba:9876::1/126"],"auto_route":true}],
        "outbounds":[outbound],
        "route":{"default_domain_resolver":"bootstrap","auto_detect_interface":true,"rules":[{"action":"sniff"},{"protocol":"dns","action":"hijack-dns"}],"final":"VPN"}
    }))
}

#[cfg(test)]
mod tests {
    use super::*;
    #[test]
    fn imports_mobile_share_link() {
        let cfg = config("hysteria2://example@example.org:26257?alpn=h3&fp=chrome&security=tls&sni=#TEST_phone").unwrap();
        assert_eq!(cfg["outbounds"][0]["tls"], json!({"enabled":true,"alpn":["h3"]}));
        assert_eq!(cfg["outbounds"][0]["server_port"], 26257);
        assert!(config("hy2://example@example.org?security=none").is_err());
    }

    #[test]
    fn tunnel_is_default() {
        let cfg = config(
            "hy2://a%40b@example.com:8443?sni=test.example&obfs=salamander&obfs-password=test#NL",
        )
        .unwrap();
        assert_eq!(cfg["route"]["final"], "VPN");
        assert_eq!(cfg["outbounds"][0]["password"], "a@b");
        assert_eq!(cfg["outbounds"][0]["server_port"], 8443);
    }
}
