#!/usr/bin/env python3
"""单独的本机 GM 网页。只绑定 127.0.0.1，把表单转成游戏服务已有的一行 GM 命令。"""

import argparse
import json
import socket
import sys
import threading
from http.server import BaseHTTPRequestHandler, ThreadingHTTPServer
from pathlib import Path
from urllib.error import URLError
from urllib.request import Request, urlopen

PAGE = (Path(__file__).resolve().parent / "index.html").read_bytes()
BIND = "127.0.0.1"


def call_gm(port, line):
    payload = (line + "\n").encode("utf-8")
    with socket.create_connection((BIND, port), timeout=5) as conn:
        conn.settimeout(5)
        conn.sendall(payload)
        data = b""
        while b"\n" not in data:
            chunk = conn.recv(65536)
            if not chunk:
                break
            data += chunk
            if len(data) > 1024 * 1024:
                raise ValueError("GM 回复过长")
    text = data.split(b"\n", 1)[0].decode("utf-8")
    if not text:
        raise ValueError("GM 没有返回")
    return text


class GmWeb(ThreadingHTTPServer):
    def __init__(self, port, gm_port, account):
        self.web_port = port
        self.gm_port = gm_port
        self.account = account
        super().__init__((BIND, port), Handler)


class Handler(BaseHTTPRequestHandler):
    server: GmWeb

    def log_message(self, fmt, *args):
        return

    def do_GET(self):
        if not self._local():
            self._send(403, {"ok": False, "error": "网页只接受本机访问"})
            return
        if self.path.split("?", 1)[0] == "/":
            self._send(200, PAGE, "text/html; charset=utf-8")
            return
        if self.path.split("?", 1)[0] == "/api/meta":
            self._send(200, {"ok": True, "gmPort": self.server.gm_port, "account": self.server.account, "webPort": self.server.web_port})
            return
        self._send(404, {"ok": False, "error": "未知页面"})

    def do_POST(self):
        if not self._local():
            self._send(403, {"ok": False, "error": "网页只接受本机访问"})
            return
        if self.path.split("?", 1)[0] != "/api/command":
            self._send(404, {"ok": False, "error": "未知页面"})
            return
        length = int(self.headers.get("Content-Length") or 0)
        if length < 0 or length > 4096:
            self._send(400, {"ok": False, "error": "请求过长"})
            return
        try:
            body = json.loads(self.rfile.read(length).decode("utf-8"))
            line = body.get("line", "")
        except (UnicodeError, json.JSONDecodeError, AttributeError):
            self._send(400, {"ok": False, "error": "请求必须是 JSON"})
            return
        if not isinstance(line, str) or not line.strip() or len(line) > 512 or "\n" in line or "\r" in line:
            self._send(400, {"ok": False, "error": "命令无效"})
            return
        try:
            text = call_gm(self.server.gm_port, line.strip())
        except (ConnectionRefusedError, TimeoutError, socket.timeout, OSError):
            self._send(502, {"ok": False, "error": f"连不上 127.0.0.1:{self.server.gm_port}。确认游戏服务已用 --gm-port {self.server.gm_port} 启动。"})
            return
        except ValueError as ex:
            self._send(502, {"ok": False, "error": str(ex)})
            return
        self._send(200, {"ok": True, "text": text})

    def _local(self):
        host = (self.headers.get("Host") or "").split(":")[0]
        if host not in (BIND, "localhost"):
            return False
        origin = self.headers.get("Origin")
        allowed = {f"http://{BIND}:{self.server.web_port}", f"http://localhost:{self.server.web_port}"}
        return origin is None or origin in allowed

    def _send(self, status, payload, content_type="application/json; charset=utf-8"):
        if isinstance(payload, (dict, list)):
            raw = json.dumps(payload, ensure_ascii=False).encode("utf-8")
        else:
            raw = payload
        self.send_response(status)
        self.send_header("Content-Type", content_type)
        self.send_header("Content-Length", str(len(raw)))
        self.send_header("Cache-Control", "no-store")
        self.end_headers()
        self.wfile.write(raw)


def serve(gm_port, port):
    if not 1 <= gm_port <= 65535 or not 1 <= port <= 65535:
        raise ValueError("GM 和网页端口必须为 1–65535")
    if port == gm_port:
        raise ValueError("网页端口不能与 GM 端口相同")
    account = "10000"
    httpd = GmWeb(port, gm_port, account)
    print(f"GM 网页 http://{BIND}:{port}  游戏命令端口 {gm_port}  默认账号 {account}", flush=True)
    httpd.serve_forever()


def self_check():
    received = []
    listener = socket.socket()
    listener.bind((BIND, 0))
    listener.listen(8)
    gm_port = listener.getsockname()[1]

    def once():
        conn, _ = listener.accept()
        with conn:
            data = b""
            while b"\n" not in data:
                chunk = conn.recv(1024)
                if not chunk:
                    break
                data += chunk
            line = data.split(b"\n", 1)[0].decode("utf-8")
            received.append(line)
            if line == "account 10000 list":
                reply = "OK count=1 | 测角 id=1 slot=0 job=0 grow=0 level=1 story=0 rev=0 pos=38/1/450/234 ret=38/0/622/196 quests=0 skills=0"
            elif line == "account 10000 level 测角 50":
                reply = "OK 测角 id=1 slot=0 job=0 grow=0 level=50 story=0 rev=0 pos=38/1/450/234 ret=38/0/622/196 quests=0 skills=1 已写入。"
            else:
                reply = "ERR 未识别"
            conn.sendall((reply + "\n").encode("utf-8"))

    threading.Thread(target=once, daemon=True).start()
    threading.Thread(target=once, daemon=True).start()
    probe = socket.socket()
    probe.bind((BIND, 0))
    web_port = probe.getsockname()[1]
    probe.close()
    try:
        threading.Thread(target=serve, args=(gm_port, web_port), daemon=True).start()
        meta = None
        for _ in range(30):
            try:
                meta = json.loads(urlopen(f"http://{BIND}:{web_port}/api/meta", timeout=1).read().decode("utf-8"))
                break
            except (URLError, TimeoutError, OSError):
                threading.Event().wait(0.05)
        assert meta is not None and meta["account"] == "10000" and meta["gmPort"] == gm_port, meta
        page = urlopen(f"http://{BIND}:{web_port}/", timeout=3).read().decode("utf-8")
        assert "开发 GM" in page
        for line in ("account 10000 list", "account 10000 level 测角 50"):
            req = Request(
                f"http://{BIND}:{web_port}/api/command",
                data=json.dumps({"line": line}).encode("utf-8"),
                headers={"Content-Type": "application/json"},
                method="POST",
            )
            body = json.loads(urlopen(req, timeout=3).read().decode("utf-8"))
            assert body["ok"] and body["text"].startswith("OK "), body
        assert received == ["account 10000 list", "account 10000 level 测角 50"], received
    finally:
        listener.close()
    print("PASS gm web proxy")


def main():
    parser = argparse.ArgumentParser(description="本机 GM 网页")
    parser.add_argument("--gm-port", type=int, default=23102, help="游戏服务的本机 GM 端口，需与服务端 --gm-port 一致")
    parser.add_argument("--port", type=int, default=23103)
    parser.add_argument("--self-check", action="store_true")
    args = parser.parse_args()
    if args.self_check:
        self_check()
        return
    try:
        serve(args.gm_port, args.port)
    except (OSError, ValueError) as ex:
        print(ex, file=sys.stderr)
        sys.exit(1)


if __name__ == "__main__":
    main()
