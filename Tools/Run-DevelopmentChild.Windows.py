#!/usr/bin/env python3
"""Windows 私有 Job Object 监督器；原生平台验证状态由调用者另行记录。"""
import argparse
import ctypes as C
import datetime
import json
import os
from pathlib import Path
import signal
import subprocess
import sys
import time

DWORD, BOOL, WORD = C.c_uint32, C.c_int32, C.c_uint16
HANDLE, SIZE_T, LPVOID = C.c_void_p, C.c_size_t, C.c_void_p


class STARTUPINFO(C.Structure):
    _fields_ = [('cb', DWORD), ('lpReserved', C.c_wchar_p), ('lpDesktop', C.c_wchar_p),
                ('lpTitle', C.c_wchar_p)] + [(name, DWORD) for name in
                ('dwX', 'dwY', 'dwXSize', 'dwYSize', 'dwXCountChars', 'dwYCountChars', 'dwFillAttribute', 'dwFlags')] + [
                ('wShowWindow', WORD), ('cbReserved2', WORD), ('lpReserved2', LPVOID),
                ('hStdInput', HANDLE), ('hStdOutput', HANDLE), ('hStdError', HANDLE)]


class STARTUPINFOEX(C.Structure):
    _fields_ = [('StartupInfo', STARTUPINFO), ('lpAttributeList', LPVOID)]


class PROCESS_INFORMATION(C.Structure):
    _fields_ = [('hProcess', HANDLE), ('hThread', HANDLE), ('dwProcessId', DWORD), ('dwThreadId', DWORD)]


class BASIC_LIMIT(C.Structure):
    _fields_ = [('PerProcessUserTimeLimit', C.c_int64), ('PerJobUserTimeLimit', C.c_int64),
                ('LimitFlags', DWORD), ('MinimumWorkingSetSize', SIZE_T), ('MaximumWorkingSetSize', SIZE_T),
                ('ActiveProcessLimit', DWORD), ('Affinity', SIZE_T), ('PriorityClass', DWORD), ('SchedulingClass', DWORD)]


class EXTENDED_LIMIT(C.Structure):
    _fields_ = [('BasicLimitInformation', BASIC_LIMIT), ('IoInfo', C.c_uint64 * 6),
                ('ProcessMemoryLimit', SIZE_T), ('JobMemoryLimit', SIZE_T),
                ('PeakProcessMemoryUsed', SIZE_T), ('PeakJobMemoryUsed', SIZE_T)]


class ACCOUNTING(C.Structure):
    _fields_ = [(name, C.c_int64) for name in ('TotalUserTime', 'TotalKernelTime', 'ThisPeriodTotalUserTime', 'ThisPeriodTotalKernelTime')] + [
                (name, DWORD) for name in ('TotalPageFaultCount', 'TotalProcesses', 'ActiveProcesses', 'TotalTerminatedProcesses')]


class WinApi:
    """只持有本次创建的 HANDLE；不枚举或打开系统 PID。"""
    def __init__(self):
        if sys.platform != 'win32':
            raise RuntimeError('此监督器仅可在 Windows 原生 Python 中执行')
        if C.sizeof(HANDLE) != 8:
            raise RuntimeError('首版 Windows 监督器只接受 64 位 Python；32 位 ABI 尚未验证')
        self.k = C.WinDLL('kernel32', use_last_error=True)
        signatures = {
            'CreateJobObjectW': ([LPVOID, C.c_wchar_p], HANDLE),
            'SetInformationJobObject': ([HANDLE, C.c_int32, LPVOID, DWORD], BOOL),
            'QueryInformationJobObject': ([HANDLE, C.c_int32, LPVOID, DWORD, LPVOID], BOOL),
            'InitializeProcThreadAttributeList': ([LPVOID, DWORD, DWORD, C.POINTER(SIZE_T)], BOOL),
            'UpdateProcThreadAttribute': ([LPVOID, DWORD, SIZE_T, LPVOID, SIZE_T, LPVOID, LPVOID], BOOL),
            'DeleteProcThreadAttributeList': ([LPVOID], None),
            'CreateProcessW': ([C.c_wchar_p, C.c_wchar_p, LPVOID, LPVOID, BOOL, DWORD, LPVOID, C.c_wchar_p, LPVOID, C.POINTER(PROCESS_INFORMATION)], BOOL),
            'IsProcessInJob': ([HANDLE, HANDLE, C.POINTER(BOOL)], BOOL),
            'ResumeThread': ([HANDLE], DWORD),
            'TerminateProcess': ([HANDLE, DWORD], BOOL),
            'TerminateJobObject': ([HANDLE, DWORD], BOOL),
            'WaitForSingleObject': ([HANDLE, DWORD], DWORD),
            'GetExitCodeProcess': ([HANDLE, C.POINTER(DWORD)], BOOL),
            'CloseHandle': ([HANDLE], BOOL),
            'PeekNamedPipe': ([HANDLE, LPVOID, DWORD, LPVOID, C.POINTER(DWORD), LPVOID], BOOL),
        }
        for name, (args, result) in signatures.items():
            function = getattr(self.k, name)
            function.argtypes, function.restype = args, result

    @staticmethod
    def check(result):
        if not result:
            raise C.WinError(C.get_last_error())
        return result

    def create_job(self):
        job = self.check(self.k.CreateJobObjectW(None, None))
        try:
            limits = EXTENDED_LIMIT()
            # 不设置 BREAKAWAY_OK 或 SILENT_BREAKAWAY_OK；job 句柄不允许子进程继承。
            limits.BasicLimitInformation.LimitFlags = 0x2000  # KILL_ON_JOB_CLOSE
            self.check(self.k.SetInformationJobObject(job, 9, C.byref(limits), C.sizeof(limits)))
            return job
        except BaseException:
            self.close(job)
            raise

    def create_suspended(self, command, directory, handles, job):
        size = SIZE_T()
        self.k.InitializeProcThreadAttributeList(None, 2, 0, C.byref(size))
        if not size.value:
            raise C.WinError(C.get_last_error())
        buffer = C.create_string_buffer(size.value)
        self.check(self.k.InitializeProcThreadAttributeList(buffer, 2, 0, C.byref(size)))
        try:
            allowed = (HANDLE * 3)(*handles)
            self.check(self.k.UpdateProcThreadAttribute(buffer, 0, 0x20002, C.byref(allowed), C.sizeof(allowed), None, None))
            jobs = (HANDLE * 1)(job)
            # JOB_LIST 在创建时建立归属，避免 suspended 创建与后续 Assign 之间的崩溃窗口。
            # Windows 10 / Server 2016+；不支持时失败，不降级到有窗口的分步归属。
            self.check(self.k.UpdateProcThreadAttribute(buffer, 0, 0x2000D, C.byref(jobs), C.sizeof(jobs), None, None))
            startup = STARTUPINFOEX()
            startup.StartupInfo.cb = C.sizeof(startup)
            startup.StartupInfo.dwFlags = 0x100  # STARTF_USESTDHANDLES
            startup.StartupInfo.hStdInput, startup.StartupInfo.hStdOutput, startup.StartupInfo.hStdError = handles
            startup.lpAttributeList = C.cast(buffer, LPVOID)
            info = PROCESS_INFORMATION()
            text = C.create_unicode_buffer(subprocess.list2cmdline(command))
            # EXTENDED_STARTUPINFO_PRESENT | CREATE_SUSPENDED | CREATE_NO_WINDOW。
            # 不启用 BREAKAWAY；父宿主已在不兼容 job 时创建失败，不启动用户指令。
            self.check(self.k.CreateProcessW(command[0], text, None, None, True, 0x00080000 | 4 | 0x08000000,
                                            None, directory, C.byref(startup), C.byref(info)))
            return info.hProcess, info.hThread
        finally:
            self.k.DeleteProcThreadAttributeList(buffer)

    def verify(self, job, process):
        belongs = BOOL()
        self.check(self.k.IsProcessInJob(process, job, C.byref(belongs)))
        if not belongs.value:
            raise RuntimeError('创建进程未归属私有 job，禁止恢复执行')

    def resume(self, thread):
        if self.k.ResumeThread(thread) == 0xFFFFFFFF:
            raise C.WinError(C.get_last_error())

    def active(self, job):
        info = ACCOUNTING()
        self.check(self.k.QueryInformationJobObject(job, 1, C.byref(info), C.sizeof(info), None))
        return info.ActiveProcesses

    def peak_job_commit(self, job):
        info = EXTENDED_LIMIT()
        self.check(self.k.QueryInformationJobObject(job, 9, C.byref(info), C.sizeof(info), None))
        return info.PeakJobMemoryUsed

    def exited(self, process):
        result = self.k.WaitForSingleObject(process, 0)
        if result == 0:
            return True
        if result == 258:
            return False
        raise C.WinError(C.get_last_error())

    def exit_code(self, process):
        code = DWORD()
        self.check(self.k.GetExitCodeProcess(process, C.byref(code)))
        return code.value

    def terminate_job(self, job):
        self.check(self.k.TerminateJobObject(job, 1))

    def terminate_process(self, process):
        self.check(self.k.TerminateProcess(process, 1))

    def close(self, handle):
        if handle:
            self.check(self.k.CloseHandle(handle))

    def available(self, fd):
        import msvcrt
        amount = DWORD()
        if not self.k.PeekNamedPipe(msvcrt.get_osfhandle(fd), None, 0, None, C.byref(amount), None):
            error = C.get_last_error()
            if error in (109, 232):  # BROKEN_PIPE / NO_DATA
                return None
            raise C.WinError(error)
        return amount.value


class OwnedJob:
    """创建、归属、恢复顺序可用合成 API 验证；原生句柄行为仍须 Windows 验证。"""
    def __init__(self, api):
        self.api = api
        self.job = api.create_job()
        self.process = None
        self.verified = False

    def start(self, command, directory, handles):
        self.process, thread = self.api.create_suspended(command, directory, handles, self.job)
        try:
            self.api.verify(self.job, self.process)
            self.verified = True
            self.api.resume(thread)
        finally:
            self.api.close(thread)

    def quiescent(self):
        if self.process is None:
            return True
        root_exited = self.api.exited(self.process)
        return root_exited and self.api.active(self.job) == 0

    def terminate(self):
        if self.process is not None:
            self.api.terminate_job(self.job)
            if not self.verified and not self.api.exited(self.process):
                # 归属核验失败时从未 Resume；仅凭本次创建 HANDLE 终止悬挂进程兜底。
                self.api.terminate_process(self.process)

    def close(self):
        try:
            if self.process is not None:
                self.api.close(self.process)
        finally:
            self.api.close(self.job)


class JobCommitObservation:
    """Job 内核高水位是 commit，不是 RSS；观测异常不能影响清理/许可归还。"""
    def __init__(self):
        self.peak = None
        self.samples = self.errors = 0
        self.last = None
        self.max_interval = 0.0

    def observe(self, api, job, force=False, now=None):
        try:
            now = time.monotonic() if now is None else now
            if not force and self.last is not None and now - self.last < .1:
                return
            if self.last is not None:
                self.max_interval = max(self.max_interval, (now - self.last) * 1000)
            self.last = now
            value = api.peak_job_commit(job)
            if type(value) is not int or value < 0:
                raise ValueError('未知的 Job commit 计数')
            self.peak = value if self.peak is None else max(self.peak, value)
            self.samples += 1
        except Exception:
            self.errors += 1

    def result(self):
        return dict(peakJobCommitBytes=self.peak, sampledPeakRssBytes=None, samples=self.samples,
                    targetIntervalMs=100, maxObservedIntervalMs=round(self.max_interval, 3),
                    samplingErrors=self.errors, coverage='job-counter-observed' if self.samples else 'unknown',
                    source='windows-job-PeakJobMemoryUsed', scope='private job; excludes supervisor',
                    limitations='commit charge, not RSS, private working set, allocation total, or a configured hard limit; '
                                'native-platform validation is separate from contract tests')


class BoundedLog:
    def __init__(self, stream, maximum):
        self.stream, self.maximum, self.written = stream, maximum, 0
        self.exceeded = False

    def write(self, data):
        remaining = self.maximum - self.written
        part = data[:remaining]
        self.stream.write(part)
        self.written += len(part)
        self.exceeded |= len(data) > remaining


def utc():
    return datetime.datetime.now(datetime.timezone.utc).isoformat()


def execute(args):
    receipt = dict(schema=1, state='failed', exitCode=None, descendantsTerminated=False,
                   startedUtc=utc(), completedUtc=None, logBytes=0, error=None,
                   platform='windows', supervision='private-job-object')
    owner, log = None, None
    memory = JobCommitObservation()
    descriptors, readers = [], []
    interrupted = []
    for number in (signal.SIGTERM, signal.SIGINT):
        signal.signal(number, lambda sig, frame: interrupted.append(sig))
    try:
        api = WinApi()
        import msvcrt
        owner = OwnedJob(api)
        stdin = os.open(os.devnull, os.O_RDONLY)
        descriptors.append(stdin)
        out_r, out_w = os.pipe()
        descriptors.extend((out_r, out_w))
        err_r, err_w = os.pipe()
        descriptors.extend((err_r, err_w))
        readers = [out_r, err_r]
        inherited = [stdin, out_w, err_w]
        for fd in inherited:
            os.set_inheritable(fd, True)
        with open(args.log, 'wb', buffering=0) as stream:
            log = BoundedLog(stream, args.max_log_bytes)
            try:
                owner.start(args.command, args.working_directory, [msvcrt.get_osfhandle(fd) for fd in inherited])
            finally:
                for fd in inherited:
                    os.close(fd)
                    descriptors.remove(fd)
            receipt['state'] = 'running'
            deadline = time.monotonic() + args.timeout_seconds
            while receipt['state'] == 'running':
                memory.observe(api, owner.job)
                for fd in list(readers):
                    available = api.available(fd)
                    if available is None:
                        readers.remove(fd)
                    elif available:
                        log.write(os.read(fd, min(available, 65536)))
                if log.exceeded:
                    receipt.update(state='output-limit', error='子进程输出超过日志上限')
                elif interrupted:
                    receipt.update(state='cancelled', error='监督器收到取消信号')
                elif time.monotonic() >= deadline:
                    receipt.update(state='timeout', error='子进程及 job 后代超过执行时限')
                elif owner.quiescent() and not readers:
                    receipt['exitCode'] = api.exit_code(owner.process)
                    receipt.update(state='completed' if receipt['exitCode'] == 0 else 'failed', descendantsTerminated=True)
                time.sleep(.02)
    except BaseException as error:
        receipt.update(state='failed', error=str(error))
    finally:
        try:
            if owner is None:
                receipt['descendantsTerminated'] = True  # 尚未有机会创建任务进程。
            elif not receipt['descendantsTerminated']:
                if not owner.quiescent():
                    owner.terminate()
                deadline = time.monotonic() + 5
                while not owner.quiescent() and time.monotonic() < deadline:
                    time.sleep(.05)
                receipt['descendantsTerminated'] = owner.quiescent()
            if owner is not None and owner.process is not None and owner.api.exited(owner.process):
                receipt['exitCode'] = owner.api.exit_code(owner.process)
        except BaseException as error:
            receipt.update(descendantsTerminated=False, error=str(error))
        finally:
            if not receipt['descendantsTerminated']:
                receipt['state'] = 'termination-unknown'
            for fd in descriptors:
                try:
                    os.close(fd)
                except OSError:
                    pass
            if owner is not None:
                if owner.process is not None:
                    memory.observe(owner.api, owner.job, force=True)
                try:
                    owner.close()  # 私有 job 最后一个句柄关闭也触发兜底终止；不冒充已观测终态。
                except BaseException as error:
                    receipt.update(state='termination-unknown', descendantsTerminated=False, error=str(error))
            receipt['memory'] = memory.result()
            receipt['logBytes'] = log.written if log else 0
            receipt['completedUtc'] = utc()
            target = Path(args.receipt)
            temporary = target.with_name(target.name + f'.{os.getpid()}.tmp')
            temporary.write_text(json.dumps(receipt, ensure_ascii=False), encoding='utf-8')
            os.replace(temporary, target)
    return 0 if receipt['state'] == 'completed' and receipt['descendantsTerminated'] else 1


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument('--working-directory', required=True)
    parser.add_argument('--log', required=True)
    parser.add_argument('--receipt', required=True)
    parser.add_argument('--timeout-seconds', type=float, default=1800)
    parser.add_argument('--max-log-bytes', type=int, default=16 * 1024 * 1024)
    parser.add_argument('command', nargs=argparse.REMAINDER)
    args = parser.parse_args()
    if args.command[:1] == ['--']:
        args.command.pop(0)
    if not args.command or not 0 < args.timeout_seconds <= 86400 or not 0 < args.max_log_bytes <= 1024 * 1024 * 1024:
        parser.error('命令不能为空，timeout 必须在 (0,86400]，日志上限必须在 (0,1GiB]')
    return execute(args)


if __name__ == '__main__':
    sys.exit(main())
