"""Transparent TLS passthrough policy using mitmproxy's maintained parser and TCP engine.

No certificate authority, TLS termination, HTTP parser, payload logging, or raw fallback.
"""

import asyncio
from collections import Counter
import datetime as dt
import ipaddress
import json
import logging
from pathlib import Path
import socket

from mitmproxy import options
from mitmproxy.net.tls import starts_like_tls_record
from mitmproxy.proxy import commands, events, layer
from mitmproxy.proxy.layers import TCPLayer
from mitmproxy.proxy.layers.tls import parse_client_hello
from mitmproxy.tools.dump import DumpMaster


class Reject(layer.Layer):
    def _handle_event(self, event):
        if isinstance(event, events.Start):
            yield commands.CloseConnection(self.context.client)


def allowed_name(name, exact, suffixes):
    if not name or not isinstance(name, str) or name.endswith(".") or len(name) > 253:
        return False
    try:
        ipaddress.ip_address(name)
        return False
    except ValueError:
        pass
    lowered = name.lower()
    return lowered in exact or any(lowered.endswith("." + suffix) for suffix in suffixes)


def hello_policy(hello, destination, policy, now):
    if now >= dt.datetime.fromisoformat(policy["expiresAtUtc"].replace("Z", "+00:00")):
        return "expired"
    if not destination or destination[1] != 443:
        return "destination"
    try:
        if ipaddress.ip_address(destination[0]).version != 4:
            return "ipv6"
    except ValueError:
        return "not-original-ip"
    if any(kind in (0xFE0D, 0xFFCE) for kind, _ in hello.extensions):
        return "ech-or-esni"
    if hello.sni and hello.sni.lower() in policy.get("privateOnlyHosts", []):
        return "private-endpoint-must-not-use-default-route"
    if not allowed_name(hello.sni, policy["exactHosts"], policy["reviewedSuffixes"]):
        return "sni"
    if not hello.alpn_protocols and hello.sni.lower() not in policy.get("reviewedEmptyAlpnHosts", []):
        return "unreviewed-empty-alpn"
    if any(protocol not in (b"http/1.1", b"h2") for protocol in hello.alpn_protocols):
        return "alpn"
    return None


class EgressPolicy:
    def __init__(self, policy, health_file):
        self.policy = policy
        self.health_file = Path(health_file)
        self.counters = Counter()

    def record(self, decision):
        self.counters[decision] += 1
        value = {"contract": "cohors-nva-health-1", "atUtc": dt.datetime.now(dt.timezone.utc).isoformat(),
                 "decisions": dict(self.counters), "payloadsRecorded": False, "tlsTerminated": False}
        temporary = self.health_file.with_suffix(".tmp")
        temporary.write_text(json.dumps(value), encoding="utf-8")
        temporary.replace(self.health_file)

    async def next_layer(self, nextlayer):
        payload = nextlayer.data_client()
        if not payload:
            return
        if len(payload) < 5:
            return
        reason = None
        hello = None
        if len(payload) > 65536 or not starts_like_tls_record(payload):
            reason = "not-bounded-tls"
        else:
            try:
                hello = parse_client_hello(payload)
            except ValueError:
                reason = "malformed-tls"
            if not reason and hello is None:
                return
        destination = nextlayer.context.server.address
        if not reason:
            reason = hello_policy(hello, destination, self.policy, dt.datetime.now(dt.timezone.utc))
        if not reason:
            try:
                answers = await asyncio.wait_for(asyncio.get_running_loop().getaddrinfo(
                    hello.sni, 443, family=socket.AF_INET, type=socket.SOCK_STREAM), timeout=5)
                if destination[0] not in {answer[4][0] for answer in answers}:
                    reason = "sni-destination-mismatch"
            except (OSError, asyncio.TimeoutError):
                reason = "dns-unavailable"
        self.record(reason or "allowed-encrypted-passthrough")
        nextlayer.layer = (Reject(nextlayer.context) if reason
                           else TCPLayer(nextlayer.context, ignore=True))


async def run(policy_file, health_file):
    policy = json.loads(Path(policy_file).read_text(encoding="utf-8"))
    if policy.get("contract") != "cohors-nva-policy-1" or not policy.get("exactHosts"):
        raise ValueError("A nonempty reviewed NVA policy is required.")
    if not isinstance(policy.get("reviewedSuffixes"), list):
        raise ValueError("Wildcard exceptions must be explicit, including an empty list.")
    # Built-in TLS-error logs may include malformed ClientHello bytes. Emit only our counters.
    logging.disable(logging.CRITICAL)
    opts = options.Options(mode=["transparent"], listen_host="0.0.0.0", listen_port=8443,
                           confdir=str(Path(policy_file).parent / "proxy-state"), rawtcp=False)
    master = DumpMaster(opts, with_termlog=False, with_dumper=False)
    opts.update(connection_strategy="lazy")
    for name in ("nextlayer", "tlsconfig"):
        addon = master.addons.get(name)
        if addon is None:
            raise RuntimeError("Pinned proxy addon contract changed.")
        master.addons.remove(addon)
    master.addons.add(EgressPolicy(policy, health_file))
    await master.run()


if __name__ == "__main__":
    import argparse
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--policy", required=True)
    parser.add_argument("--health-file", required=True)
    arguments = parser.parse_args()
    asyncio.run(run(arguments.policy, arguments.health_file))
