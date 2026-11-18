import asyncio
import datetime as dt
import importlib.util
from pathlib import Path
import ssl
import tempfile
from types import SimpleNamespace
import unittest
import json

from nva_native import namespace_names

from mitmproxy import options
from mitmproxy.tools.dump import DumpMaster
from mitmproxy.proxy.layers.tls import parse_client_hello


SPEC = importlib.util.spec_from_file_location("nva_proxy", Path(__file__).parents[1] / "bootstrap" / "nva_proxy.py")
nva = importlib.util.module_from_spec(SPEC)
SPEC.loader.exec_module(nva)


class PrimaryNvaTests(unittest.IsolatedAsyncioTestCase):
    def test_native_empty_inventory_is_distinct_from_malformed_inventory(self):
        self.assertEqual(namespace_names(b""), set())
        self.assertEqual(namespace_names(b"[]\n"), set())
        self.assertEqual(namespace_names(b'[{"name":"foreign-namespace"}]'), {"foreign-namespace"})
        for value in (b"not-json", b"{}", b"[{}]", b'[{"name":null}]'):
            with self.subTest(value=value), self.assertRaises((RuntimeError, json.JSONDecodeError)):
                namespace_names(value)

    def setUp(self):
        self.now = dt.datetime(2026, 11, 18, tzinfo=dt.timezone.utc)
        self.policy = {"contract": "cohors-nva-policy-1", "expiresAtUtc": "2026-11-19T00:00:00Z",
                       "exactHosts": ["allowed.example.test"], "reviewedSuffixes": ["data.mcr.microsoft.com"]}
        self.hello = SimpleNamespace(sni="allowed.example.test", extensions=[], alpn_protocols=[b"h2"])

    def test_ech_missing_sni_ip_ipv6_and_expiry_fail_closed(self):
        self.assertIsNone(nva.hello_policy(self.hello, ("203.0.113.8", 443), self.policy, self.now))
        variants = [
            (SimpleNamespace(sni=None, extensions=[], alpn_protocols=[]), ("203.0.113.8", 443), self.now),
            (SimpleNamespace(sni="203.0.113.8", extensions=[], alpn_protocols=[]), ("203.0.113.8", 443), self.now),
            (SimpleNamespace(sni="allowed.example.test", extensions=[(0xFE0D, b"opaque")], alpn_protocols=[]),
             ("203.0.113.8", 443), self.now),
            (self.hello, ("2001:db8::8", 443), self.now),
            (self.hello, ("203.0.113.8", 8443), self.now),
            (self.hello, ("203.0.113.8", 443), self.now + dt.timedelta(days=1)),
        ]
        for hello, target, when in variants:
            with self.subTest(target=target, when=when):
                self.assertIsNotNone(nva.hello_policy(hello, target, self.policy, when))
        self.assertFalse(nva.allowed_name("allowed.example.test.evil.test", self.policy["exactHosts"], []))
        self.assertFalse(nva.allowed_name("evil-data.mcr.microsoft.com", [], ["data.mcr.microsoft.com"]))

    def test_real_openssl_clienthello_is_parsed_by_pinned_library_without_tls_termination(self):
        context = ssl.SSLContext(ssl.PROTOCOL_TLS_CLIENT)
        context.set_alpn_protocols(["h2", "http/1.1"])
        incoming, outgoing = ssl.MemoryBIO(), ssl.MemoryBIO()
        client = context.wrap_bio(incoming, outgoing, server_side=False, server_hostname="allowed.example.test")
        with self.assertRaises(ssl.SSLWantReadError):
            client.do_handshake()
        wire = outgoing.read()
        hello = parse_client_hello(wire)
        self.assertEqual(hello.sni, "allowed.example.test")
        self.assertIsNone(nva.hello_policy(hello, ("203.0.113.8", 443), self.policy, self.now))
        self.assertIsNone(parse_client_hello(wire[:8]))

    async def test_pinned_proxy_addons_can_be_removed_before_a_listener_starts(self):
        with tempfile.TemporaryDirectory() as folder:
            opts = options.Options(mode=["transparent"], confdir=folder, rawtcp=False)
            master = DumpMaster(opts, with_termlog=False, with_dumper=False)
            opts.update(connection_strategy="lazy")
            for name in ("nextlayer", "tlsconfig"):
                addon = master.addons.get(name)
                self.assertIsNotNone(addon)
                master.addons.remove(addon)
            self.assertEqual(list(Path(folder).rglob("*")), [])
            master.shutdown()


if __name__ == "__main__":
    unittest.main()
