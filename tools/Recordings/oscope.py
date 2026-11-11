"""A tiny client for a running onescope shell: starts the tool, speaks MCP over its live socket, captures frames.

Python standard library only. The tool path comes from $ONESCOPE (default: onescope on PATH); the shell renders on the
graphics device named by $ONEGAME_GPU_ADAPTER.
"""
import json
import os
import socket
import subprocess
import tempfile
import time


class Shell:
    def __init__(self, open_args, size="1920x1080", frames_dir="frames", env=None):
        self.frames_dir = os.path.abspath(frames_dir)
        os.makedirs(self.frames_dir, exist_ok=True)
        self.sock_dir = tempfile.mkdtemp(prefix="osrec")
        self.path = os.path.join(self.sock_dir, "s.sock")
        tool = os.environ.get("ONESCOPE", "onescope")
        cmd = [tool, *open_args, "--headless", "--size", size, "--artifacts", self.frames_dir, "--live", self.path]
        self.proc = subprocess.Popen(cmd, stdout=subprocess.PIPE, stderr=subprocess.STDOUT, text=True, env=env)
        for _ in range(300):
            if os.path.exists(self.path):
                break
            if self.proc.poll() is not None:
                raise RuntimeError("onescope exited: " + (self.proc.stdout.read() or "")[:500])
            time.sleep(0.1)
        time.sleep(0.2)
        self.sock = socket.socket(socket.AF_UNIX)
        self.sock.connect(self.path)
        self.f = self.sock.makefile("rw")
        self._id = 0
        self._rpc("initialize", {"protocolVersion": "2024-11-05", "capabilities": {}, "clientInfo": {"name": "rec", "version": "1"}})
        self.f.write(json.dumps({"jsonrpc": "2.0", "method": "notifications/initialized"}) + "\n")
        self.f.flush()

    def _rpc(self, method, params):
        self._id += 1
        self.f.write(json.dumps({"jsonrpc": "2.0", "id": self._id, "method": method, "params": params}) + "\n")
        self.f.flush()
        while True:
            line = self.f.readline()
            if not line:
                raise RuntimeError("shell closed the connection")
            msg = json.loads(line)
            if msg.get("id") == self._id:
                return msg

    def call(self, name, **args):
        """Runs one operation; returns its JSON result, or raises with the error the shell reported."""
        msg = self._rpc("tools/call", {"name": name, "arguments": args})
        if "error" in msg:
            raise RuntimeError(f"{name}: {msg['error']}")
        res = msg["result"]
        text = next((c["text"] for c in res["content"] if c["type"] == "text"), "{}")
        if res.get("isError"):
            raise RuntimeError(f"{name} {args}: {text}")
        return json.loads(text)

    def frame(self, clip, n, **extra):
        """Captures the shell's frame as <clip>/<n>.png under the frames folder."""
        os.makedirs(os.path.join(self.frames_dir, clip), exist_ok=True)
        return self.call("capture_frame", path=f"{clip}/{n:05d}.png", **extra)

    def close(self):
        try:
            self.call("quit")
        except Exception:
            pass
        try:
            self.proc.wait(timeout=20)
        except subprocess.TimeoutExpired:
            self.proc.kill()
        try:
            os.unlink(self.path)
            os.rmdir(self.sock_dir)
        except OSError:
            pass

    def __enter__(self):
        return self

    def __exit__(self, *_):
        self.close()
