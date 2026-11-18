"""Install the reviewed transparent-filter candidate on its own Linux appliance."""

import datetime as dt
import ipaddress
import json
import os
from pathlib import Path
import re
import shutil
import subprocess


HOST = re.compile(r"[a-z0-9](?:[a-z0-9.-]{0,251}[a-z0-9])?\Z")


def command(arguments):
    result = subprocess.run(arguments, stdin=subprocess.DEVNULL, capture_output=True, timeout=600, check=False)
    if result.returncode:
        raise RuntimeError(f"NVA installation step failed: {Path(arguments[0]).name}; output withheld.")


def policy_config(policy):
    if policy.get("contract") != "cohors-nva-policy-1":
        raise ValueError("Invalid NVA policy contract.")
    for key in ("exactHosts", "reviewedSuffixes", "privateDnsSuffixes", "privateOnlyHosts"):
        if not isinstance(policy.get(key), list) or any(not HOST.fullmatch(host) for host in policy[key]):
            raise ValueError("DNS/TLS names must be explicit, lowercase, and free of wildcards or script syntax.")
    network = ipaddress.ip_network(policy["sourceNetwork"], strict=True)
    if network.version != 4 or not network.is_private:
        raise ValueError("A private IPv4 source network is required.")
    listen = ipaddress.ip_address(policy["listenAddress"])
    if listen not in network:
        raise ValueError("DNS must bind the appliance's own private address.")
    expires = dt.datetime.fromisoformat(policy["expiresAtUtc"].replace("Z", "+00:00"))
    if expires.utcoffset() != dt.timedelta(0) or expires <= dt.datetime.now(dt.timezone.utc):
        raise ValueError("NVA policy is expired or has no UTC expiry.")
    exact = [f"QNameRule({json.dumps(name + '.')})" for name in policy["exactHosts"] + policy["privateOnlyHosts"]]
    suffixes = sorted(set(policy["reviewedSuffixes"] + policy["privateDnsSuffixes"]))
    return "\n".join([
        f'setLocal("{listen}:53")', f"setACL({{{json.dumps(str(network))}}})",
        'newServer({address="168.63.129.16:53", name="azure-private-dns"})',
        'local suffixes = newSuffixMatchNode()',
        *[f"suffixes:add(newDNSName({json.dumps(suffix + '.')}))" for suffix in suffixes],
        f"addAction(LuaRule(function(dq) return os.time() >= {int(expires.timestamp())} end), RCodeAction(DNSRCode.REFUSED))",
        "addAction(NotRule(QTypeRule(DNSQType.A)), RCodeAction(DNSRCode.REFUSED))",
        "addAction(OrRule({" + ", ".join(exact + ["SuffixMatchNodeRule(suffixes)"]) + '}), PoolAction(""))',
        "addAction(AllRule(), RCodeAction(DNSRCode.REFUSED))", "",
    ])


def firewall_config(source_network, proxy_uid):
    network = str(ipaddress.ip_network(source_network, strict=True))
    if not isinstance(proxy_uid, int) or proxy_uid <= 0:
        raise ValueError("A dedicated non-root proxy UID is required.")
    return f"""table inet cohors_filter {{
 chain input {{
  type filter hook input priority filter; policy drop;
  iifname "lo" accept
  meta nfproto ipv6 drop
  ct state established,related accept
  ip saddr {network} udp dport 53 accept
  ip saddr {network} tcp dport {{ 53, 8443 }} accept
  ip saddr 168.63.129.16 accept
 }}
 chain forward {{ type filter hook forward priority filter; policy drop; }}
 chain output {{
  type filter hook output priority filter; policy drop;
  oifname "lo" accept
  meta nfproto ipv6 drop
  ct state established,related accept
  ip daddr 168.63.129.16 udp dport {{ 53, 67, 68 }} accept
  ip daddr 168.63.129.16 tcp dport {{ 53, 80, 32526 }} accept
  ip daddr 169.254.169.254 tcp dport 80 accept
  meta skuid {proxy_uid} tcp dport 443 accept
 }}
}}
table ip cohors_redirect {{
 chain prerouting {{
  type nat hook prerouting priority dstnat; policy accept;
  ip saddr {network} tcp dport 443 redirect to :8443
 }}
 chain output {{
  type nat hook output priority dstnat; policy accept;
  meta skuid != {proxy_uid} tcp dport 443 redirect to :8443
 }}
}}
"""


def install(bundle, policy):
    dns = policy_config(policy)
    if os.name != "posix" or os.geteuid() != 0:
        raise RuntimeError("The installer runs only on its new Linux NVA through approved Managed Run Command.")
    import pwd
    command(["sysctl", "-w", "net.ipv4.ip_forward=0"])
    if not all(shutil.which(name) for name in ("dnsdist", "nft")):
        command(["apt-get", "update", "-qq"])
        command(["apt-get", "install", "-y", "-qq", "dnsdist", "nftables", "python3-venv"])
    try:
        account = pwd.getpwnam("cohorsproxy")
    except KeyError:
        command(["useradd", "--system", "--no-create-home", "--shell", "/usr/sbin/nologin", "cohorsproxy"])
        account = pwd.getpwnam("cohorsproxy")
    root = Path("/opt/cohors-nva")
    root.mkdir(mode=0o755, exist_ok=True)
    command(["python3", "-m", "venv", str(root / "venv")])
    command([str(root / "venv/bin/python"), "-m", "pip", "install", "--no-index",
             "--find-links", str(bundle / "wheels"), "-r", str(bundle / "infra/bootstrap/requirements-nva.txt")])
    (root / "nva_proxy.py").write_bytes((bundle / "infra/bootstrap/nva_proxy.py").read_bytes())
    (root / "policy.json").write_text(json.dumps(policy), encoding="utf-8")
    dns_file = Path("/etc/dnsdist/cohors.conf")
    dns_file.write_text(dns, encoding="utf-8")
    command(["dnsdist", "--check-config", "-C", str(dns_file)])
    proxy_unit = """[Unit]
Description=Cohors reviewed encrypted TLS passthrough (synthetic pilot SPOF)
After=network-online.target
Wants=network-online.target
[Service]
User=cohorsproxy
Group=cohorsproxy
RuntimeDirectory=cohors-nva
ExecStart=/opt/cohors-nva/venv/bin/python /opt/cohors-nva/nva_proxy.py --policy /opt/cohors-nva/policy.json --health-file /run/cohors-nva/health.json
Restart=on-failure
RestartSec=5
NoNewPrivileges=true
PrivateTmp=true
ProtectSystem=strict
ProtectHome=true
ReadWritePaths=/run/cohors-nva
StandardOutput=null
StandardError=null
[Install]
WantedBy=multi-user.target
"""
    Path("/etc/systemd/system/cohors-proxy.service").write_text(proxy_unit, encoding="utf-8")
    dns_unit = """[Unit]
Description=Cohors allowlisted DNS without query logging
After=network-online.target
[Service]
ExecStart=/usr/bin/dnsdist --supervised -C /etc/dnsdist/cohors.conf
Restart=on-failure
NoNewPrivileges=true
PrivateTmp=true
ProtectHome=true
StandardOutput=null
StandardError=null
[Install]
WantedBy=multi-user.target
"""
    Path("/etc/systemd/system/cohors-dns.service").write_text(dns_unit, encoding="utf-8")
    command(["systemctl", "disable", "--now", "dnsdist"])
    command(["systemctl", "daemon-reload"])
    command(["systemctl", "enable", "--now", "cohors-proxy", "cohors-dns"])
    command(["systemctl", "restart", "cohors-proxy", "cohors-dns"])
    command(["systemctl", "is-active", "--quiet", "cohors-proxy", "cohors-dns"])
    rules = ("destroy table inet cohors_filter\ndestroy table ip cohors_redirect\n"
             + firewall_config(policy["sourceNetwork"], account.pw_uid))
    rules_path = root / "filter.nft"
    rules_path.write_text(rules, encoding="utf-8")
    # This VM has only this owned ruleset; never flush an arbitrary shared host's rules.
    command(["nft", "--check", "--file", str(rules_path)])
    Path("/etc/systemd/system/cohors-filter.service").write_text("""[Unit]
Description=Cohors atomic owned-table default-deny rules
Before=cohors-routing.service
[Service]
Type=oneshot
ExecStart=/usr/sbin/nft --file /opt/cohors-nva/filter.nft
RemainAfterExit=true
[Install]
WantedBy=multi-user.target
""", encoding="utf-8")
    Path("/etc/systemd/system/cohors-routing.service").write_text("""[Unit]
Description=Enable forwarding only after owned default-deny rules
Requires=cohors-filter.service cohors-proxy.service cohors-dns.service
After=cohors-filter.service cohors-proxy.service cohors-dns.service
BindsTo=cohors-filter.service
[Service]
Type=oneshot
ExecStart=/usr/sbin/sysctl -w net.ipv4.ip_forward=1
ExecStop=/usr/sbin/sysctl -w net.ipv4.ip_forward=0
RemainAfterExit=true
[Install]
WantedBy=multi-user.target
""", encoding="utf-8")
    command(["systemctl", "daemon-reload"])
    command(["systemctl", "enable", "--now", "cohors-filter", "cohors-routing"])
    command(["systemctl", "restart", "cohors-filter", "cohors-routing"])
    return {"configured": True, "tlsTerminated": False, "nativePlatformAcceptance": False,
            "parser": "mitmproxy-12.3.1", "dns": "dnsdist", "forwardFallback": "drop"}
