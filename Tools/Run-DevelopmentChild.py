#!/usr/bin/env python3
"""Linux 自有进程监督：流式有界日志、subreaper 后代回收；无系统配置修改。"""
import argparse
import ctypes
import datetime
import json
import os
import re
from pathlib import Path
import selectors
import signal
import subprocess
import sys
import time


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def namespace_pid(status, expected_depth):
    ids = next(line.split()[1:] for line in status.splitlines() if line.startswith("NSpid:"))
    # 仅支持与 supervisor 同层的 PID namespace；不能把内层 PID 1 当成本层 PID 1。
    if len(ids) != expected_depth:
        raise RuntimeError("自有后代 PID namespace 层级变化，无法安全停止，必须保留租约")
    return int(ids[expected_depth - 1])


class RssObservation:
    """只复用监督器状态快照做观测，缺测/解析失败不能改变任务控制结果。"""
    def __init__(self):
        self.peak = None
        self.samples = self.missing = self.errors = self.skipped = self.maximum_processes = self.scan_races = 0
        self.last = None
        self.max_interval = self.max_span = 0.0

    def observe(self, rows, supervisor, started, completed, scan_failures=0):
        try:
            if self.last is not None and completed - self.last < .1:
                return
            if scan_failures:
                # 整份状态读取失效时无法确认是不是自有分支，保守跳过整轮而非补零。
                self.scan_races += scan_failures
                self.skipped += 1
                if self.last is not None:
                    self.max_interval = max(self.max_interval, (completed - self.last) * 1000)
                self.last = completed
                self.max_span = max(self.max_span, (completed - started) * 1000)
                return
            children = {}
            for pid, (parent, status) in rows.items():
                children.setdefault(parent, []).append(pid)
            pending = list(children.get(supervisor, []))
            owned = set()
            while pending:
                pid = pending.pop()
                if pid == supervisor or pid in owned:
                    continue
                owned.add(pid)
                pending.extend(children.get(pid, []))
            if not owned:
                return  # 没有任务读数不代表实测峰值为零。
            if self.last is not None:
                self.max_interval = max(self.max_interval, (completed - self.last) * 1000)
            self.last = completed
            self.max_span = max(self.max_span, (completed - started) * 1000)
            self.maximum_processes = max(self.maximum_processes, len(owned))
            total, missing = 0, 0
            for pid in owned:
                match = re.search(r"^VmRSS:\s+(\d+)\s+kB\s*$", rows[pid][1], re.MULTILINE)
                if match is None:
                    missing += 1
                else:
                    total += int(match.group(1)) * 1024
            self.missing += missing
            if missing:
                self.skipped += 1  # 不把部分和伪装成完整树读数，也不将缺失替换为零。
                return
            self.samples += 1
            self.peak = total if self.peak is None else max(self.peak, total)
        except Exception:
            self.errors += 1

    def result(self):
        return dict(sampledPeakRssBytes=self.peak, samples=self.samples, targetIntervalMs=100,
                    maxObservedIntervalMs=round(self.max_interval, 3), maxSnapshotSpanMs=round(self.max_span, 3),
                    missingRssReadings=self.missing, skippedIncompleteSamples=self.skipped,
                    samplingErrors=self.errors, procScanReadRaces=self.scan_races, maximumObservedProcesses=self.maximum_processes,
                    coverage='partial-sampled' if self.samples else 'unknown',
                    source='linux-proc-status-VmRSS', scope='owned descendants; excludes supervisor',
                    limitations='approximate kernel RSS accounting and sequential snapshots; short-lived processes and between-sample peaks may be missed; '
                                'shared pages counted per process; not private memory, instantaneous peak, allocation total, or a hard limit')


def run(args):
    receipt = dict(schema=1, state="failed", exitCode=None, descendantsTerminated=False,
                   startedUtc=utc(), completedUtc=None, logBytes=0, error=None)
    process = None
    memory = RssObservation()
    namespace_depth = None
    selector = selectors.DefaultSelector()
    interrupted = []
    for sig in (signal.SIGTERM, signal.SIGINT):
        signal.signal(sig, lambda number, frame: interrupted.append(number))

    def children():
        # 本 supervisor 单线程；subreaper 确保脱离进程组的孤儿同样归本进程。
        result = []
        snapshot_started = time.monotonic()
        rows = {}
        scan_failures = 0
        parent = int(os.readlink("/proc/self"))
        for entry in Path("/proc").iterdir():
            if not entry.name.isdigit():
                continue
            try:
                status = (entry / "status").read_text()
            except FileNotFoundError:
                scan_failures += 1
                continue
            ppid = next(int(line.split()[1]) for line in status.splitlines() if line.startswith("PPid:"))
            if ppid == parent:
                result.append(int(entry.name))
            try:
                rows[int(entry.name)] = (ppid, status)
            except Exception:
                memory.errors += 1
                scan_failures += 1
        memory.observe(rows, parent, snapshot_started, time.monotonic(), scan_failures)
        return result

    def reap():
        while True:
            try:
                pid, status = os.waitpid(-1, os.WNOHANG)
            except ChildProcessError:
                break
            if pid == 0:
                break
            if process is not None and pid == process.pid:
                process.returncode = os.waitstatus_to_exitcode(status)
                receipt["exitCode"] = process.returncode

    def signal_owned(sig):
        for pid in children():
            try:
                status = Path(f"/proc/{pid}/status").read_text()
                # /proc 可能挂载于外层 PID namespace；pidfd 使用本 namespace 的 PID。
                local_pid = namespace_pid(status, namespace_depth)
                fd = os.pidfd_open(local_pid)
            except (ProcessLookupError, FileNotFoundError):
                continue
            try:
                # pidfd 固定身份；验证直接父子关系后才发信号，不按历史 PID/名字推断。
                status = Path(f"/proc/{pid}/status").read_text()
                ppid = next(int(line.split()[1]) for line in status.splitlines() if line.startswith("PPid:"))
                if ppid != int(os.readlink("/proc/self")):
                    raise RuntimeError("自有进程身份已变化，拒绝发信号")
                signal.pidfd_send_signal(fd, sig)
            except (ProcessLookupError, FileNotFoundError):
                pass
            finally:
                os.close(fd)

    def drain(log, timeout):
        for key, _ in selector.select(timeout):
            try:
                chunk = os.read(key.fd, 65536)
            except BlockingIOError:
                continue
            if not chunk:
                selector.unregister(key.fileobj)
                key.fileobj.close()
                continue
            remaining = args.max_log_bytes - receipt["logBytes"]
            if remaining > 0:
                log.write(chunk[:remaining])
                receipt["logBytes"] += min(len(chunk), remaining)
            if len(chunk) > remaining and receipt["state"] == "running":
                receipt.update(state="output-limit", error="子进程输出超过日志上限")

    try:
        if sys.platform != "linux" or not hasattr(os, "pidfd_open") or not hasattr(signal, "pidfd_send_signal"):
            raise RuntimeError("执行仅支持带 pidfd 的 Linux/Python；其他平台未验证")
        # PR_SET_CHILD_SUBREAPER 仅改变本 supervisor 的孤儿接收行为，不更改系统安全策略。
        libc = ctypes.CDLL(None, use_errno=True)
        if libc.prctl(36, 1, 0, 0, 0) != 0:
            raise OSError(ctypes.get_errno(), "无法设置本进程 subreaper")
        self_status = Path("/proc/self/status").read_text()
        namespace_depth = len(next(line.split()[1:] for line in self_status.splitlines() if line.startswith("NSpid:")))
        if namespace_pid(self_status, namespace_depth) != os.getpid():
            raise RuntimeError("无法匹配 supervisor 的 PID namespace，拒绝启动")
        # 启动前验证 pidfd 能力；不支持时不能启动一个随后无法安全停止的任务。
        fd = os.pidfd_open(os.getpid())
        try:
            signal.pidfd_send_signal(fd, 0)
        finally:
            os.close(fd)
        if children():
            raise RuntimeError("监督器启动前已有子进程，拒绝混入既有任务")
        with open(args.log, "wb", buffering=0) as log:
            process = subprocess.Popen(args.command, cwd=args.working_directory, stdin=subprocess.DEVNULL,
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
            for pipe in (process.stdout, process.stderr):
                os.set_blocking(pipe.fileno(), False)
                selector.register(pipe, selectors.EVENT_READ)
            receipt["state"] = "running"
            deadline = time.monotonic() + args.timeout_seconds
            root_done_at = None
            while receipt["state"] == "running":
                reap()
                direct = children()
                if not direct:
                    # 所有已启动的后代都已退出并回收，才能归还预算；不能只看根进程。
                    drain_deadline = time.monotonic() + 1
                    while selector.get_map() and time.monotonic() < drain_deadline:
                        drain(log, 0.05)
                    if selector.get_map():
                        receipt.update(state="termination-unknown", error="全部后代退出后输出管道仍未关闭")
                        break
                    if receipt["state"] == "running":
                        receipt.update(state="completed" if receipt["exitCode"] == 0 else "failed",
                                       descendantsTerminated=True)
                    break
                if interrupted:
                    receipt.update(state="cancelled", error="监督进程收到取消信号")
                    break
                if time.monotonic() >= deadline:
                    receipt.update(state="timeout", error="子进程及其后代超过执行时限")
                    break
                if process.returncode is not None:
                    root_done_at = root_done_at or time.monotonic()
                    if time.monotonic() - root_done_at > 2:
                        receipt.update(state="failed", error="根进程已退出，但仍有自有后代存活")
                        break
                drain(log, 0.05)
            if not receipt["descendantsTerminated"]:
                # 先停止直接自有子进程，随后孤儿由 subreaper 接管；每次都核验身份。
                cleanup_deadline = time.monotonic() + 5
                term_until = time.monotonic() + 0.5
                while True:
                    reap()
                    if not children():
                        receipt["descendantsTerminated"] = True
                        break
                    if time.monotonic() >= cleanup_deadline:
                        break
                    signal_owned(signal.SIGTERM if time.monotonic() < term_until else signal.SIGKILL)
                    drain(log, 0.05)
                if not receipt["descendantsTerminated"]:
                    receipt.update(state="termination-unknown", error="无法确认全部自有后代终止；必须保留预算租约")
    except BaseException as error:
        receipt.update(state="termination-unknown", error=str(error))
        # 发生监督错误时，不以缺少读数推断无人运行，不尝试按 PID 名称兜底杀进程。
        if process is None:
            receipt["descendantsTerminated"] = True
    finally:
        selector.close()
        receipt["memory"] = memory.result()
        receipt["completedUtc"] = utc()
        target = Path(args.receipt)
        temporary = target.with_name(target.name + f".{os.getpid()}.tmp")
        temporary.write_text(json.dumps(receipt, ensure_ascii=False), encoding="utf-8")
        os.replace(temporary, target)
    return 0 if receipt["state"] == "completed" and receipt["descendantsTerminated"] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--working-directory", required=True)
    parser.add_argument("--log", required=True)
    parser.add_argument("--receipt", required=True)
    parser.add_argument("--timeout-seconds", type=float, default=1800)
    parser.add_argument("--max-log-bytes", type=int, default=16 * 1024 * 1024)
    parser.add_argument("command", nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command[:1] == ["--"]:
        args.command.pop(0)
    if not args.command or not 0 < args.timeout_seconds <= 86400 or not 0 < args.max_log_bytes <= 1024 * 1024 * 1024:
        parser.error("命令不能为空，timeout 必须在 (0,86400]，日志上限必须在 (0,1GiB]")
    return run(args)


if __name__ == "__main__":
    sys.exit(main())
