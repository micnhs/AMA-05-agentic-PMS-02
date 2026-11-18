"""Hosted-only Linux kernel/TLS checks. Explicitly not an Azure ACA/Flex acceptance."""

import argparse
import datetime as dt
import importlib.util
import json
import os
from pathlib import Path
import shutil
import socket
import ssl
import subprocess
import sys
import tempfile
import threading
import time
import uuid


ROOT = Path(__file__).resolve().parents[2]


def command(*args):
    result = subprocess.run(args, capture_output=True, check=False, timeout=30)
    if result.returncode:
        raise RuntimeError(f"Native fixture command failed: {args[0]}: {result.stderr.decode(errors='replace')[:500]}")
    return result.stdout


def namespace_names(output):
    # iproute2 prints no JSON bytes when the namespace set is empty.
    if not output.strip():
        return set()
    value = json.loads(output)
    if not isinstance(value, list) or any(
            not isinstance(item, dict) or not isinstance(item.get("name"), str) or not item["name"] for item in value):
        raise RuntimeError("The prospective namespace inventory is malformed.")
    return {item["name"] for item in value}


def server(folder):
    context = ssl.SSLContext(ssl.PROTOCOL_TLS_SERVER)
    context.load_cert_chain(str(folder / "cert.pem"), str(folder / "key.pem"))
    context.set_alpn_protocols(["http/1.1"])

    def tls(address):
        listener = socket.create_server((address, 443))
        while True:
            peer, _ = listener.accept()
            try:
                with context.wrap_socket(peer, server_side=True) as channel:
                    channel.recv(8192)
                    channel.sendall(b"HTTP/1.1 200 OK\r\nContent-Length: 2\r\nConnection: close\r\n\r\nok")
            except (OSError, ssl.SSLError):
                peer.close()

    def udp():
        with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as listener:
            listener.bind(("10.231.92.1", 443))
            while True:
                payload, peer = listener.recvfrom(1024)
                listener.sendto(payload, peer)

    for action, args in ((tls, ("10.231.92.1",)), (tls, ("10.231.92.3",)), (udp, ())):
        threading.Thread(target=action, args=args, daemon=True).start()
    while True:
        time.sleep(1)


def client(folder, host, target, udp=False):
    try:
        if udp:
            with socket.socket(socket.AF_INET, socket.SOCK_DGRAM) as channel:
                channel.settimeout(2)
                channel.sendto(b"synthetic-quic-path", (target, 443))
                channel.recvfrom(128)
        else:
            context = ssl.create_default_context(cafile=str(folder / "cert.pem"))
            context.set_alpn_protocols(["http/1.1"])
            with socket.create_connection((target, 443), timeout=3) as raw:
                with context.wrap_socket(raw, server_hostname=host) as channel:
                    channel.sendall(b"GET /probe HTTP/1.1\r\nHost: allowed.cohors.test\r\nConnection: close\r\n\r\n")
                    if not channel.recv(1024).startswith(b"HTTP/1.1 200"):
                        return 2
        return 0
    except (OSError, ssl.SSLError):
        return 2


def main():
    if os.environ.get("GITHUB_ACTIONS") != "true" or os.name != "posix" or os.geteuid() != 0:
        raise RuntimeError("Run only in the explicitly authorized hosted Linux job, never on a developer host.")
    suffix = uuid.uuid4().hex[:12]
    appliance, source, destination = "cohorsn" + suffix, "cohorsc" + suffix, "cohorst" + suffix
    names = (appliance, source, destination)
    existing = namespace_names(command("ip", "-j", "netns", "list"))
    if any(name in existing or (Path("/etc/netns") / name).exists() for name in names):
        raise RuntimeError("Fixture namespace already exists.")
    processes = []
    created_namespaces, created_configs = {}, []
    with tempfile.TemporaryDirectory(prefix="cohors-nva-native-") as directory:
        folder = Path(directory)
        folder.chmod(0o755)
        health = folder / "health"
        health.mkdir()
        os.chown(health, 65534, 65534)
        proxy_script = folder / "nva_proxy.py"
        shutil.copyfile(ROOT / "infra/bootstrap/nva_proxy.py", proxy_script)
        proxy_script.chmod(0o644)
        spec = importlib.util.spec_from_file_location("install_nva", ROOT / "infra/bootstrap/install_nva.py")
        installer = importlib.util.module_from_spec(spec)
        spec.loader.exec_module(installer)
        policy = {"contract": "cohors-nva-policy-1",
                  "expiresAtUtc": (dt.datetime.now(dt.timezone.utc) + dt.timedelta(hours=1)).isoformat(),
                  "sourceNetwork": "10.231.0.0/16", "listenAddress": "10.231.91.1",
                  "exactHosts": ["allowed.cohors.test"], "reviewedSuffixes": [],
                  "privateDnsSuffixes": [], "privateOnlyHosts": [], "reviewedEmptyAlpnHosts": []}
        (folder / "policy.json").write_text(json.dumps(policy), encoding="utf-8")
        (folder / "dns.conf").write_text(installer.policy_config(policy), encoding="utf-8")
        command("dnsdist", "--check-config", "-C", str(folder / "dns.conf"))
        command("openssl", "req", "-x509", "-newkey", "rsa:2048", "-nodes", "-days", "1",
                "-keyout", str(folder / "key.pem"), "-out", str(folder / "cert.pem"),
                "-subj", "/CN=allowed.cohors.test", "-addext",
                "subjectAltName=DNS:allowed.cohors.test,DNS:blocked.cohors.test,IP:10.231.92.1")
        try:
            for name in names:
                command("ip", "netns", "add", name)
                created_namespaces[name] = (Path("/run/netns") / name).stat().st_ino
                command("ip", "-n", name, "link", "set", "lo", "up")
                config = Path("/etc/netns") / name
                config.mkdir(parents=True, exist_ok=False)
                created_configs.append(config)
                (config / "hosts").write_text("127.0.0.1 localhost\n10.231.92.1 allowed.cohors.test blocked.cohors.test\n")
            command("ip", "-n", appliance, "link", "add", "nva-source", "type", "veth",
                    "peer", "name", "source-nva", "netns", source)
            command("ip", "-n", appliance, "addr", "add", "10.231.91.1/24", "dev", "nva-source")
            command("ip", "-n", source, "addr", "add", "10.231.91.2/24", "dev", "source-nva")
            command("ip", "-n", appliance, "link", "set", "nva-source", "up")
            command("ip", "-n", source, "link", "set", "source-nva", "up")
            command("ip", "-n", source, "route", "add", "default", "via", "10.231.91.1")
            command("ip", "-n", appliance, "link", "add", "nva-target", "type", "veth",
                    "peer", "name", "target-nva", "netns", destination)
            command("ip", "-n", appliance, "addr", "add", "10.231.92.2/24", "dev", "nva-target")
            command("ip", "-n", destination, "addr", "add", "10.231.92.1/24", "dev", "target-nva")
            command("ip", "-n", destination, "addr", "add", "10.231.92.3/24", "dev", "target-nva")
            command("ip", "-n", appliance, "link", "set", "nva-target", "up")
            command("ip", "-n", destination, "link", "set", "target-nva", "up")
            command("ip", "-n", destination, "route", "add", "10.231.91.0/24", "via", "10.231.92.2")
            command("ip", "netns", "exec", appliance, "sysctl", "-w", "net.ipv4.ip_forward=1")
            (folder / "rules.nft").write_text(installer.firewall_config("10.231.0.0/16", 65534))
            command("ip", "netns", "exec", appliance, "nft", "--check", "-f", str(folder / "rules.nft"))
            command("ip", "netns", "exec", appliance, "nft", "-f", str(folder / "rules.nft"))
            processes.append(subprocess.Popen(["ip", "netns", "exec", destination,
                                               sys.executable, __file__, "--server", str(folder)],
                                              stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL))
            proxy = subprocess.Popen(["ip", "netns", "exec", appliance, "setpriv", "--reuid=65534",
                                      "--regid=65534", "--clear-groups", sys.executable, "-B",
                                      str(proxy_script), "--policy", str(folder / "policy.json"),
                                      "--health-file", str(health / "health.json")],
                                     stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
            processes.append(proxy)

            def probe(host, target="10.231.92.1", udp=False):
                args = ["ip", "netns", "exec", source, sys.executable, __file__, "--client", str(folder),
                        "--host", host, "--target", target]
                if udp:
                    args += ["--udp"]
                return subprocess.run(args, capture_output=True, timeout=10, check=False).returncode

            ready = False
            for _ in range(20):
                if proxy.poll() is not None:
                    raise RuntimeError("The maintained transparent proxy exited before readiness.")
                if probe("allowed.cohors.test") == 0:
                    ready = True
                    break
                time.sleep(1)
            if not ready:
                raise RuntimeError("Forwarded TLS with end-to-end certificate validation never succeeded.")
            command("ip", "netns", "exec", destination, sys.executable, __file__, "--client", str(folder),
                    "--host", "allowed.cohors.test", "--target", "10.231.92.1", "--udp")
            if (folder / "proxy-state").exists():
                raise RuntimeError("A passthrough-only proxy must not create certificate authority state.")
            for name, host, target, udp in (
                ("blocked-sni", "blocked.cohors.test", "10.231.92.1", False),
                ("direct-ip-no-sni", "10.231.92.1", "10.231.92.1", False),
                ("sni-ip-mismatch", "allowed.cohors.test", "10.231.92.3", False),
                ("udp-quic-bypass", "", "10.231.92.1", True),
            ):
                if probe(host, target, udp) == 0:
                    raise RuntimeError("Unexpected permitted path: " + name)
            proxy.terminate()
            proxy.wait(timeout=10)
            if probe("allowed.cohors.test") == 0:
                raise RuntimeError("Appliance outage has an unfiltered fallback.")
            counters = json.loads((health / "health.json").read_text())["decisions"]
            if not all(counters.get(reason, 0) > 0 for reason in
                       ("allowed-encrypted-passthrough", "sni", "sni-destination-mismatch")):
                raise RuntimeError("Negative outcomes were not produced by the actual proxy policy.")
        finally:
            for process in reversed(processes):
                if process.poll() is None:
                    process.terminate()
                    process.wait(timeout=10)
            for name, inode in reversed(list(created_namespaces.items())):
                if (Path("/run/netns") / name).stat().st_ino != inode:
                    raise RuntimeError("Fixture ownership changed; refusing namespace deletion.")
                command("ip", "netns", "delete", name)
            for config in reversed(created_configs):
                (config / "hosts").unlink(missing_ok=True)
                config.rmdir()
    if namespace_names(command("ip", "-j", "netns", "list")) != existing:
        raise RuntimeError("Terminal namespace inventory differs from the prospective foreign set.")
    if any(process.poll() is None for process in processes):
        raise RuntimeError("An owned native-fixture process remains alive.")
    print(json.dumps({"dnsConfigurationParsed": True, "nativeKernelCasesPassed": 6,
                      "endToEndCertificateValidation": True, "ownedResourcesRemoved": True,
                      "foreignNamespaceInventoryUnchanged": True, "azureAcaFlexAcceptance": False}))


if __name__ == "__main__":
    parser = argparse.ArgumentParser()
    parser.add_argument("--server", type=Path)
    parser.add_argument("--client", type=Path)
    parser.add_argument("--host")
    parser.add_argument("--target")
    parser.add_argument("--udp", action="store_true")
    arguments = parser.parse_args()
    if arguments.server:
        server(arguments.server)
    elif arguments.client:
        raise SystemExit(client(arguments.client, arguments.host, arguments.target, arguments.udp))
    else:
        main()
