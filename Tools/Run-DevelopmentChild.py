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
import traceback
import uuid
import stat
import hashlib


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


class LiveCheckpoint:
    """只保存身份线索；不提供终态或租约释放证明。"""
    def __init__(self, receipt, timeout_seconds=1800):
        self.target = Path(os.path.abspath(receipt))
        self.live = self.target.with_name(self.target.name + ".live")
        # 从根目录逐层 O_NOFOLLOW 打开，后续发布全部相对此目录 fd。
        self.directory_fd = os.open("/", os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW)
        try:
            for part in self.target.parent.parts[1:]:
                next_fd = os.open(part, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=self.directory_fd)
                os.close(self.directory_fd)
                self.directory_fd = next_fd
            try:
                os.stat(self.target.name, dir_fd=self.directory_fd, follow_symlinks=False)
            except FileNotFoundError:
                pass
            else:
                raise FileExistsError("终态收据已存在，拒绝覆盖")
            # 独占目录也是运行 claim；失败者不能写 live 或终态收据。
            os.mkdir(self.live.name, mode=0o700, dir_fd=self.directory_fd)
            self.live_fd = os.open(self.live.name, os.O_RDONLY | os.O_DIRECTORY | os.O_NOFOLLOW, dir_fd=self.directory_fd)
            os.fsync(self.directory_fd)
        except BaseException:
            if hasattr(self, "live_fd"):
                os.close(self.live_fd)
            os.close(self.directory_fd)
            raise
        self.execution_id = uuid.uuid4().hex
        self.last = None
        self.interval = max(1, timeout_seconds / 240)
        self.disabled = False
        self.sequence = 0

    def publish(self, target, payload, initial):
        encoded = json.dumps(payload, ensure_ascii=False)
        if target != self.target and len(encoded.encode("utf-8")) > 65536:
            raise RuntimeError("live 检查点超过64KiB上限，停止任务而不截断JSON")
        directory_fd = self.directory_fd if target == self.target else self.live_fd
        temporary = target.name + "." + self.execution_id + ".tmp"
        fd = os.open(temporary, os.O_WRONLY | os.O_CREAT | os.O_EXCL | os.O_NOFOLLOW, 0o600, dir_fd=directory_fd)
        try:
            with os.fdopen(fd, "w", encoding="utf-8") as stream:
                stream.write(encoded)
                stream.flush()
                os.fsync(stream.fileno())
            # 所有版本只发布一次；不使用 lstat→replace 冒充条件原子替换。
            os.link(temporary, target.name, src_dir_fd=directory_fd, dst_dir_fd=directory_fd, follow_symlinks=False)
            os.fsync(directory_fd)
        finally:
            try:
                os.unlink(temporary, dir_fd=directory_fd)
            except FileNotFoundError:
                pass

    def write(self, receipt, phase, force=False):
        if self.disabled or (not force and self.last is not None and time.monotonic() - self.last < self.interval):
            return
        try:
            if self.sequence >= 256:
                raise RuntimeError("live 检查点数量达到上限，停止任务而不丢弃旧证据")
            source = receipt["ownership"]
            ownership = {key: source.get(key) for key in ("supervisor", "parent", "root")}
            ownership["observedChildren"] = source.get("observedChildren", [])[:128]
            ownership["truncated"] = source.get("truncated", False) or len(source.get("observedChildren", [])) > 128
            payload = dict(schema=1, executionId=self.execution_id, invocationDigest=receipt["invocationDigest"],
                           startedUtc=receipt["startedUtc"], checkpointUtc=utc(),
                           sequence=self.sequence + 1, maximumCheckpoints=256, intervalSeconds=self.interval, phase=phase, state="unknown",
                           terminalProof=False, descendantsTerminated=False,
                           ownership=ownership, error=receipt.get("liveError"),
                           limitations="身份线索可能过期或不完整；缺失、存活或退出均不能由本文件证明；仅本轮终态收据可用于释放租约")
            self.publish(self.live / f"{self.sequence + 1:06d}.json", payload, initial=True)
            self.sequence += 1
            self.last = time.monotonic()
        except BaseException:
            self.disabled = True
            raise  # 原 supervisor 进入异常回收，不继续无检查点运行。


def run(args):
    receipt = dict(schema=1, state="failed", exitCode=None, descendantsTerminated=False,
                   startedUtc=utc(), completedUtc=None, logBytes=0, error=None)
    process = None
    checkpoint = None
    memory = RssObservation()
    receipt["ownership"] = {"supervisor": None, "root": None, "observedChildren": [], "reaped": [], "signals": [], "truncated": False}
    observed_identities = set()

    def identity(pid):
        try:
            proc = Path(f"/proc/{pid}")
            stat = (proc / "stat").read_text()
            # comm 可以含空格或括号；字段从最后一个右括号后开始。
            fields = stat[stat.rfind(")") + 2:].split()
            return dict(procPid=pid, startTicks=int(fields[19]), pidNamespace=os.readlink(proc / "ns/pid"))
        except (FileNotFoundError, ProcessLookupError):
            return dict(procPid=pid, state="disappeared-before-identity")

    def record_owned(pid, status):
        item = identity(pid)
        item["localPid"] = namespace_pid(status, namespace_depth)
        # /proc 可挂在外层 namespace；只能由已核父子关系与 NSpid 匹配根进程。
        if process is not None and item["localPid"] == process.pid:
            receipt["ownership"]["root"]["observed"] = item
        key = (item["procPid"], item.get("startTicks"), item.get("pidNamespace"))
        if key in observed_identities:
            return
        if len(observed_identities) >= 1024:
            receipt["ownership"]["truncated"] = True
            return
        observed_identities.add(key)
        receipt["ownership"]["observedChildren"].append(item)
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
            except (FileNotFoundError, ProcessLookupError):
                # /proc 条目可在枚举后退出；仅该已消失竞态不视为监督器损坏。
                scan_failures += 1
                continue
            ppid = next(int(line.split()[1]) for line in status.splitlines() if line.startswith("PPid:"))
            if ppid == parent:
                result.append(int(entry.name))
                record_owned(int(entry.name), status)
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
                return True
            if pid == 0:
                return False
            if len(receipt["ownership"]["reaped"]) < 1024:
                receipt["ownership"]["reaped"].append(dict(localPid=pid, waitStatus=status, exitCode=os.waitstatus_to_exitcode(status)))
            else:
                receipt["ownership"]["truncated"] = True
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
                if len(receipt["ownership"]["signals"]) < 1024:
                    receipt["ownership"]["signals"].append(dict(procPid=pid, localPid=local_pid, signal=int(sig), identity=identity(pid)))
                else:
                    receipt["ownership"]["truncated"] = True
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
        checkpoint = LiveCheckpoint(args.receipt, args.timeout_seconds)
        receipt["executionId"] = checkpoint.execution_id
        # 仅持久化调用规格摘要，不写出命令、环境或凭据。
        receipt["invocationDigest"] = hashlib.sha256(json.dumps(vars(args), sort_keys=True).encode()).hexdigest()
        if sys.platform != "linux" or not hasattr(os, "pidfd_open") or not hasattr(signal, "pidfd_send_signal"):
            raise RuntimeError("执行仅支持带 pidfd 的 Linux/Python；其他平台未验证")
        # PR_SET_CHILD_SUBREAPER 仅改变本 supervisor 的孤儿接收行为，不更改系统安全策略。
        libc = ctypes.CDLL(None, use_errno=True)
        if libc.prctl(36, 1, 0, 0, 0) != 0:
            raise OSError(ctypes.get_errno(), "无法设置本进程 subreaper")
        receipt["ownership"]["supervisor"] = identity(int(os.readlink("/proc/self")))
        receipt["ownership"]["supervisor"]["localPid"] = os.getpid()
        self_status = Path("/proc/self/status").read_text()
        parent_pid = next(int(line.split()[1]) for line in self_status.splitlines() if line.startswith("PPid:"))
        receipt["ownership"]["parent"] = identity(parent_pid)
        parent_status = Path(f"/proc/{parent_pid}/status").read_text()
        receipt["ownership"]["parent"]["namespacePids"] = [int(value) for value in next(line.split()[1:] for line in parent_status.splitlines() if line.startswith("NSpid:"))]
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
        checkpoint.write(receipt, "entry", force=True)
        with open(args.log, "wb", buffering=0) as log:
            process = subprocess.Popen(args.command, cwd=args.working_directory, stdin=subprocess.DEVNULL,
                                       stdout=subprocess.PIPE, stderr=subprocess.PIPE, start_new_session=True)
            receipt["ownership"]["root"] = dict(localPid=process.pid, observed=None)
            for pipe in (process.stdout, process.stderr):
                os.set_blocking(pipe.fileno(), False)
                selector.register(pipe, selectors.EVENT_READ)
            receipt["state"] = "running"
            children()
            checkpoint.write(receipt, "running", force=True)
            deadline = time.monotonic() + args.timeout_seconds
            root_done_at = None
            while receipt["state"] == "running":
                kernel_empty = reap()
                direct = children()
                checkpoint.write(receipt, "running")
                if not direct and kernel_empty:
                    receipt["ownership"]["terminalProof"] = "same-subreaper waitpid ECHILD"
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
                checkpoint.write(receipt, "cleanup", force=True)
                # 先停止直接自有子进程，随后孤儿由 subreaper 接管；每次都核验身份。
                cleanup_deadline = time.monotonic() + 5
                term_until = time.monotonic() + 0.5
                while True:
                    kernel_empty = reap()
                    if not children() and kernel_empty:
                        receipt["ownership"]["terminalProof"] = "same-subreaper waitpid ECHILD after cleanup"
                        receipt["descendantsTerminated"] = True
                        break
                    if time.monotonic() >= cleanup_deadline:
                        break
                    checkpoint.write(receipt, "cleanup")
                    signal_owned(signal.SIGTERM if time.monotonic() < term_until else signal.SIGKILL)
                    drain(log, 0.05)
                if not receipt["descendantsTerminated"]:
                    receipt.update(state="termination-unknown", error="无法确认全部自有后代终止；必须保留预算租约")
    except BaseException as error:
        receipt.update(state="termination-unknown", error=str(error), traceback=traceback.format_exc())
        receipt["liveError"] = dict(type=type(error).__name__, errno=getattr(error, "errno", None))
        if checkpoint is not None:
            try:
                checkpoint.write(receipt, "supervisor-error", force=True)
            except BaseException as checkpoint_error:
                receipt["checkpointError"] = type(checkpoint_error).__name__
        # 只由仍存活的原 subreaper 收尾；保留原错误，不以新进程的缺少读数推断终态。
        if process is None:
            receipt["descendantsTerminated"] = True
        else:
            try:
                cleanup_deadline = time.monotonic() + 5
                term_until = time.monotonic() + 0.5
                with open(args.log, "ab", buffering=0) as log:
                    while True:
                        kernel_empty = reap()
                        if not children() and kernel_empty:
                            receipt.update(state="failed", descendantsTerminated=True)
                            receipt["ownership"]["terminalProof"] = "same-subreaper waitpid ECHILD after supervisor error"
                            break
                        if time.monotonic() >= cleanup_deadline:
                            break
                        signal_owned(signal.SIGTERM if time.monotonic() < term_until else signal.SIGKILL)
                        drain(log, 0.05)
            except BaseException as cleanup_error:
                receipt["cleanupError"] = str(cleanup_error)
                receipt["cleanupTraceback"] = traceback.format_exc()
    finally:
        selector.close()
        receipt["memory"] = memory.result()
        receipt["completedUtc"] = utc()
        if checkpoint is not None:
            # 终态另行发布，不改 live 的 unknown 语义；既有收据不可覆盖。
            try:
                checkpoint.publish(checkpoint.target, receipt, initial=True)
            finally:
                os.close(checkpoint.live_fd)
                os.close(checkpoint.directory_fd)
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
