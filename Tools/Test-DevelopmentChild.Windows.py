#!/usr/bin/env python3
"""Windows 监督合同测试；Linux 仅执行纯逻辑，原生 Win32 用例不会伪报通过。"""
import ctypes
import argparse
import importlib.util
import io
import json
import os
import platform
from pathlib import Path
import subprocess
import sys
import tempfile
import uuid

parser = argparse.ArgumentParser(description=__doc__)
parser.add_argument('--require-native', action='store_true', help='原生 CI 门槛：不支持的平台退出 2，不能只跑纯合同后报成功')
options = parser.parse_args()
if options.require_native and (sys.platform != 'win32' or sys.version_info < (3, 8) or ctypes.sizeof(ctypes.c_void_p) != 8 or sys.getwindowsversion().major < 10):
    print('NOT RUN: native validation requires Windows 10/Server 2016+ and 64-bit Python', file=sys.stderr)
    sys.exit(2)

helper = Path(__file__).with_name('Run-DevelopmentChild.Windows.py')
spec = importlib.util.spec_from_file_location('windows_supervisor', helper)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
checks = 0


def check(condition, message):
    global checks
    if not condition:
        raise AssertionError(message)
    checks += 1


class FakeApi:
    """只模拟归属/终态合同，不模拟真实 Windows 内核行为。"""
    def __init__(self, fail=None):
        self.events, self.fail = [], fail
        self.root_done, self.count = False, 0

    def create_job(self):
        self.events.append('create-job')
        return 'private-job'

    def create_suspended(self, command, directory, handles, job):
        self.events.append('create-job-owned-suspended')
        if self.fail == 'create':
            raise OSError('synthetic create failure')
        self.count = 1
        return 'owned-process-handle', 'owned-thread-handle'

    def verify(self, job, process):
        self.events.append('verify-job')
        if self.fail == 'verify':
            raise OSError('synthetic membership verification failure')
        self.count = 1

    def resume(self, thread):
        self.events.append('resume')
        if self.fail == 'resume':
            raise OSError('synthetic resume failure')

    def close(self, handle):
        self.events.append('close:' + handle)

    def exited(self, process):
        return self.root_done

    def active(self, job):
        if self.fail == 'query':
            raise OSError('synthetic unknown job state')
        return self.count

    def terminate_job(self, job):
        self.events.append('terminate-job')
        self.root_done, self.count = True, 0

    def terminate_process(self, process):
        self.events.append('terminate-owned-handle')
        if self.fail == 'terminate':
            raise OSError('synthetic termination failure')
        self.root_done = True


# Win64 ABI 静态布局检查，不代表 Win32 API 已实际运行。
if ctypes.sizeof(ctypes.c_void_p) == 8:
    for structure, expected in ((module.STARTUPINFO, 104), (module.STARTUPINFOEX, 112),
                                (module.PROCESS_INFORMATION, 24), (module.BASIC_LIMIT, 64),
                                (module.EXTENDED_LIMIT, 144), (module.ACCOUNTING, 48)):
        check(ctypes.sizeof(structure) == expected, 'Win64 固定宽度结构布局：' + structure.__name__)
class FakeKernel:
    def __init__(self):
        self.attributes, self.deleted, self.flags = {}, False, 0

    def InitializeProcThreadAttributeList(self, pointer, count, flags, size):
        check(count == 2, '句柄白名单与 job 列表均需属性槽')
        size._obj.value = 128
        return 1

    def UpdateProcThreadAttribute(self, pointer, flags, kind, value, size, previous, returned):
        length = size // ctypes.sizeof(module.HANDLE)
        self.attributes[kind] = list(ctypes.cast(value, ctypes.POINTER(module.HANDLE * length)).contents)
        return 1

    def CreateProcessW(self, executable, text, process_security, thread_security, inherit, flags, environment, directory, startup, result):
        self.flags = flags
        check(inherit is True, '显式白名单继承需要 bInheritHandles')
        result._obj.hProcess, result._obj.hThread = 88, 89
        return 1

    def DeleteProcThreadAttributeList(self, pointer):
        self.deleted = True


native_api = module.WinApi.__new__(module.WinApi)
native_api.k = FakeKernel()
check(native_api.create_suspended(['synthetic.exe', 'a b'], '.', (11, 12, 13), 77) == (88, 89), '原生调用构造返回自有 HANDLE')
check(native_api.k.attributes == {0x20002: [11, 12, 13], 0x2000D: [77]}, '只继承标准流，私有 job 仅用于创建归属不加入继承白名单')
check(native_api.k.flags & 4 and native_api.k.flags & 0x80000 and not native_api.k.flags & 0x1000000, 'suspended + extended，禁止 breakaway')
check(native_api.k.deleted, '属性列表成功路径释放')

api = FakeApi()
owner = module.OwnedJob(api)
owner.start(['synthetic.exe'], '.', (1, 2, 3))
check(api.events[:4] == ['create-job', 'create-job-owned-suspended', 'verify-job', 'resume'], '创建时归属并核验之前绝不 resume')
api.root_done, api.count = True, 1
check(not owner.quiescent(), '根退出但 job 后代未结束，不能归还预算')
api.count = 0
check(owner.quiescent(), '根及 job 全部结束才确认为终态')
owner.close()
check(api.events[-2:] == ['close:owned-process-handle', 'close:private-job'], '关闭本次句柄且 job 最后兜底关闭')
for failure in ('create', 'verify', 'resume'):
    api = FakeApi(failure)
    owner = module.OwnedJob(api)
    rejected = False
    try:
        owner.start(['synthetic.exe'], '.', (1, 2, 3))
    except OSError:
        rejected = True
    check(rejected, failure + ' 失败不能继续执行')
    if failure == 'create':
        check(owner.quiescent() and 'resume' not in api.events, '创建失败从未产生可运行 child')
    elif failure == 'verify':
        check('resume' not in api.events and not owner.quiescent(), '归属核验失败保持 suspended 且未假定已终止')
        owner.terminate()
        check('terminate-job' in api.events and owner.quiescent(), '只终止本次私有 job 并确认终态')
    else:
        owner.terminate()
        check('terminate-job' in api.events and owner.quiescent(), '已归属后发生恢复失败，按私有 job 回收')
    owner.close()
api = FakeApi('query')
owner = module.OwnedJob(api)
owner.start(['synthetic.exe'], '.', (1, 2, 3))
api.root_done = True
failed = False
try:
    owner.quiescent()
except OSError:
    failed = True
check(failed, '缺 job 状态不能推断全部退出')
owner.close()
class FakeCommitApi:
    def __init__(self, value):
        self.value = value
    def peak_job_commit(self, job):
        if isinstance(self.value, Exception):
            raise self.value
        return self.value

commit = module.JobCommitObservation()
commit.observe(FakeCommitApi(1024), 'owned', now=1)
commit.observe(FakeCommitApi(2048), 'owned', now=1.2)
commit.observe(FakeCommitApi(512), 'owned', now=1.4)
check(commit.result()['peakJobCommitBytes'] == 2048 and commit.result()['sampledPeakRssBytes'] is None, 'Job commit 高水位不冒充 RSS，也不相加历史读数')
commit.observe(FakeCommitApi(OSError('synthetic unavailable')), 'owned', now=1.6)
check(commit.errors == 1 and commit.samples == 3, 'Windows 观测异常仅记错误，不改变生命周期')
missing = module.JobCommitObservation()
missing.observe(FakeCommitApi(None), 'owned', now=1)
check(missing.result()['peakJobCommitBytes'] is None and missing.result()['coverage'] == 'unknown', '无有效 commit 读数明确 unknown')

stream = io.BytesIO()
log = module.BoundedLog(stream, 4)
log.write(b'abc')
log.write(b'def')
check(log.exceeded and log.written == 4 and stream.getvalue() == b'abcd', '流式日志固定上限，超限不是成功')
print(f'PASS: {checks} Windows pure-contract checks; native APIs not covered by these checks')

# 同一文件可在 Windows 原生运行实际进程合同；其他平台明确 NOT RUN。
if sys.platform != 'win32':
    print('NOT RUN: Windows native Job Object / handle inheritance / cancellation integration')
else:
    print(json.dumps(dict(nativeValidationHost=dict(reportedMachine=platform.machine(), pythonPointerBits=ctypes.sizeof(ctypes.c_void_p)*8, pythonVersion=platform.python_version(), processorArchitecture=os.environ.get('PROCESSOR_ARCHITECTURE'), processorArchitectureW6432=os.environ.get('PROCESSOR_ARCHITEW6432'), nativeHardwareArchitecture='unknown-not-probed', architectureCoverage='current host only; other native/emulated combinations unknown'))))
    with tempfile.TemporaryDirectory(prefix='s5a9-win-job-') as directory:
        root = Path(directory)
        def native(code, timeout=5, maximum=65536):
            run = root / uuid.uuid4().hex
            run.mkdir()
            log, receipt = run / 'log', run / 'receipt.json'
            check(not receipt.exists(), '每次原生调用使用新的收据路径')
            result = subprocess.run([sys.executable, str(helper), '--working-directory', directory,
                                     '--log', str(log), '--receipt', str(receipt), '--timeout-seconds', str(timeout),
                                     '--max-log-bytes', str(maximum), '--', sys.executable, '-c', code], timeout=15)
            check(receipt.is_file(), '本轮原生监督器必须写出新收据，不能沿用前轮终态')
            data = json.loads(receipt.read_text(encoding='utf-8'))
            check(data['descendantsTerminated'] is True, '原生 job 自有后代终态')
            return result.returncode, data, log.read_bytes()
        result, data, output = native('print("synthetic stdout"); import sys; print("stderr",file=sys.stderr)')
        check(result == 0 and data['state'] == 'completed' and b'synthetic stdout' in output and b'stderr' in output, '原生成功与双输出流内容')
        result, data, output = native('raise SystemExit(7)')
        check(result == 1 and data['exitCode'] == 7, '原生非零退出')
        result, data, output = native('print("x"*1000000)', maximum=4096)
        check(result == 1 and data['state'] == 'output-limit' and len(output) == 4096 and data['logBytes'] == 4096, '原生日志文件与收据均满足硬上限')
        bystander = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])
        try:
            result, data, output = native('import time; time.sleep(30)', timeout=.2)
            check(result == 1 and data['state'] == 'timeout' and bystander.poll() is None, '原生超时只停止私有 job')
        finally:
            bystander.terminate()
            bystander.wait(timeout=5)
        result, data, output = native('import subprocess,sys; subprocess.Popen([sys.executable,"-c","import time; time.sleep(30)"])')
        check(result == 1 and data['state'] == 'timeout', '根退出后 job 后代仍受控直至阶段时限')
        result, data, output = native('import subprocess,sys; subprocess.Popen([sys.executable,"-c","import time; time.sleep(2.2)"])')
        check(result == 0, '合法短活后代不因固定两秒阈值误失败')
        for index in range(3):
            marker = f'synthetic-run-{index}'
            result, data, output = native('print(' + repr(marker) + ')')
            check(result == 0 and marker.encode() in output, '连续运行具有本轮独立内容与新收据')
        # 只尝试启动立即退出的合成子进程；若异常允许 breakaway，也不会留下长活任务。
        code = ('import subprocess,sys\n'
                'try:\n subprocess.run([sys.executable,"-c","pass"],creationflags=subprocess.CREATE_BREAKAWAY_FROM_JOB,check=True)\n'
                'except OSError as error:\n if error.winerror != 5: raise\n raise SystemExit(0)\n'
                'raise SystemExit(23)')
        result, data, output = native(code)
        check(result == 0 and data['exitCode'] == 0, '私有 job 拒绝显式 breakaway')

    print(f'PASS: {checks} combined contract/native assertions on Windows')
    print('NOT COVERED: supervisor hard-kill at every creation phase; incompatible host/nested Job configurations; production Run remains Linux-only')
