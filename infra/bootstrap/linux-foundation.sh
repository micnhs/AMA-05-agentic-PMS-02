#!/bin/bash
set -euo pipefail
umask 077

# No forwarding while the reviewed proxy and DNS policy are not installed.
install -d -m 0700 /var/lib/cohors
cat >/etc/sysctl.d/90-cohors-private.conf <<'SYSCTL'
net.ipv4.ip_forward=0
net.ipv4.conf.all.accept_redirects=0
net.ipv4.conf.default.accept_redirects=0
net.ipv4.conf.all.send_redirects=0
net.ipv6.conf.all.disable_ipv6=1
net.ipv6.conf.default.disable_ipv6=1
SYSCTL
sysctl --system >/dev/null
printf '{"contract":"cohors-host-foundation-1","forwarding":false,"configured":false}\n' \
  >/var/lib/cohors/foundation.json
