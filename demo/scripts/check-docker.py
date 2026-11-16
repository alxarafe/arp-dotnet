"""Docker lifecycle regressions against the real demo runner; no Docker mocks.

Run after demo/run build. Only this test's exact container IDs are removed by its finalizer.
"""
import json
import os
import socket
import subprocess
import time
import uuid
from pathlib import Path
from urllib.request import urlopen

ROOT = Path(__file__).resolve().parents[2]
RUNNER = ROOT / "demo/run"
IMAGE = "mcr.microsoft.com/dotnet/sdk:10.0@sha256:fe0fd4e93bd2fbb47c883b7a6369e5c8d3db8bf01cdc5981f6ee35fbdf3ff4fb"
PREFIX = "arp-hardening-" + uuid.uuid4().hex[:12]
created = []


def docker(*args, check=True):
    return subprocess.run(["docker", *args], text=True, capture_output=True, check=check)


def inspect(identity):
    result = docker("inspect", identity, check=False)
    return json.loads(result.stdout)[0] if result.returncode == 0 else None


def signature(identity):
    item = inspect(identity)
    return (item["State"]["Status"], item["State"]["StartedAt"], item["RestartCount"])


def run_demo(command, name, port, success):
    previous = inspect(name)
    result = subprocess.run([str(RUNNER), command], text=True, capture_output=True, env={
        **os.environ, "ARP_DEMO_CONTAINER": name, "ARP_DEMO_PORT": str(port),
        "ARP_DEMO_ORIGIN": "http://127.0.0.1:4198"})
    current = inspect(name)
    if command in ("start", "check") and previous is None and current is not None:
        created.append(current["Id"])
    assert (result.returncode == 0) is success, (command, result.stdout, result.stderr)
    return result


def foreign(suffix, labels=(), publish=False):
    args = ["run", "--detach", "--name", PREFIX + suffix]
    for label in labels:
        args.extend(["--label", label])
    if publish:
        args.extend(["--publish", "127.0.0.1::8080"])
    identity = docker(*args, IMAGE, "sleep", "600").stdout.strip()
    created.append(identity)
    return identity


baseline = {identity: signature(identity) for identity in docker("ps", "-q").stdout.split()}
try:
    blocker = foreign("-port", publish=True)
    mapping = docker("port", blocker, "8080/tcp").stdout.strip()
    assert mapping.startswith("127.0.0.1:")
    occupied_port = int(mapping.rsplit(":", 1)[1])
    before = signature(blocker)
    for command in ("start", "check"):
        name = PREFIX + "-occupied-" + command
        run_demo(command, name, occupied_port, success=False)
        assert inspect(name) is None, (command, "failed start left a container")
        assert signature(blocker) == before

    # Even an existing container with BOTH demo labels is not owned by this start invocation.
    label_sets = [(), ("arp.demo.owner=" + str(ROOT),),
                  ("arp.demo=dotnet-dictionary",),
                  ("arp.demo.owner=" + str(ROOT), "arp.demo=dotnet-dictionary")]
    for index, labels in enumerate(label_sets):
        identity = foreign("-collision-" + str(index), labels)
        name = inspect(identity)["Name"].lstrip("/")
        before = signature(identity)
        run_demo("start", name, 0, success=False)
        assert signature(identity) == before
        if index < 3:
            run_demo("stop", name, 0, success=False)
            assert signature(identity) == before

    # A configured non-default port/origin, successful start/stop and idempotent stop.
    with socket.socket() as reserved:
        reserved.bind(("127.0.0.1", 0))
        port = reserved.getsockname()[1]
    name = PREFIX + "-success"
    run_demo("start", name, port, success=True)
    identity = inspect(name)["Id"]
    created.append(identity)
    assert docker("port", identity, "8080/tcp").stdout.strip() == "127.0.0.1:" + str(port)
    for attempt in range(50):
        try:
            with urlopen("http://127.0.0.1:" + str(port) + "/demo/info", timeout=2) as response:
                assert response.status == 200
                break
        except OSError:
            time.sleep(0.2)
    else:
        raise AssertionError("Host did not become ready on configured port")
    run_demo("stop", name, port, success=True)
    assert inspect(identity) is None
    run_demo("stop", name, port, success=True)
    assert all(signature(identity) == state for identity, state in baseline.items())
    print("PASS: occupied-port start/check cleanup, name collisions, both ownership labels, "
          "configured loopback port, start/stop, existing containers unchanged")
finally:
    for identity in reversed(created):
        docker("rm", "--force", identity, check=False)