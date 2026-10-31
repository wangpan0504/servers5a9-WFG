#!/usr/bin/env python3
"""轻量合成进程回归：不启动服务器、不构建、不接触外部进程。"""
import json
import importlib.util
from pathlib import Path
import subprocess
import sys
import tempfile
import time
import uuid

helper = Path(__file__).with_name('Run-DevelopmentChild.py')
checks = 0

def check(condition, message):
    global checks
    assert condition, message
    checks += 1

spec = importlib.util.spec_from_file_location('supervisor', helper)
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)
check(module.namespace_pid('NSpid:\t100\t7', 2) == 7, '同层 PID 映射')
for depth in (1, 3):
    rejected = False
    try:
        module.namespace_pid('NSpid:\t100\t1', depth)
    except RuntimeError:
        rejected = True
    check(rejected, '异层 PID namespace 拒绝，不能误用内层 PID 1')

def statuses(root_rss, child_rss):
    return {2: (1, f'VmRSS: {root_rss} kB\n'), 3: (2, f'VmRSS: {child_rss} kB\n'),
            9: (8, 'VmRSS: 999999 kB\n')}
observation = module.RssObservation()
observation.observe(statuses(900, 100), 1, 1, 1.01)
observation.observe(statuses(100, 900), 1, 1.2, 1.21)
check(observation.result()['sampledPeakRssBytes'] == 1000 * 1024, '峰值为同一轮树总和，不能相加各进程不同时间峰值')
check(observation.result()['maximumObservedProcesses'] == 2, '排除无关进程与 supervisor')
observation.observe({2: (1, 'State: Z\n')}, 1, 1.4, 1.41)
check(observation.result()['missingRssReadings'] == 1 and observation.samples == 2, '缺 VmRSS 跳过不完整样本，不补零')
check(observation.result()['coverage'] == 'partial-sampled' and observation.result()['maxObservedIntervalMs'] >= 190, '报告有限覆盖和实际采样间隔')
observation.observe(statuses(100, 100), 1, 1.6, 1.61, scan_failures=1)
check(observation.samples == 2 and observation.result()['procScanReadRaces'] == 1, '整份进程状态失读不能遗漏分支后仍计有效样本')
unknown = module.RssObservation()
unknown.observe({2: (1, 'State: Z\n')}, 1, 1, 1.01)
check(unknown.result()['sampledPeakRssBytes'] is None and unknown.result()['coverage'] == 'unknown', '没有完整读数明确 unknown')
unknown.observe({2: (1, object())}, 1, 2, 2.01)
check(unknown.errors == 1, '采样解析异常吞入观测错误，不抛给进程控制')

with tempfile.TemporaryDirectory(prefix='s5a9-supervisor-') as directory:
    root = Path(directory)
    def execute(code, timeout=5, cap=65536):
        run = root / uuid.uuid4().hex
        run.mkdir()
        log = run / 'log'
        receipt = run / 'receipt.json'
        check(not receipt.exists(), '每轮 Linux 合成调用使用唯一收据路径')
        result = subprocess.run([sys.executable, str(helper), '--working-directory', directory,
                                 '--log', str(log), '--receipt', str(receipt), '--timeout-seconds', str(timeout),
                                 '--max-log-bytes', str(cap), '--', sys.executable, '-c', code], timeout=15)
        check(receipt.is_file(), '必须生成本轮新收据，不得沿用旧终态')
        data = json.loads(receipt.read_text())
        live = json.loads(sorted(Path(str(receipt) + '.live').glob('*.json'))[-1].read_text())
        check(live['state'] == 'unknown' and live['terminalProof'] is False and not live['descendantsTerminated'], 'live 永不作为终态证明')
        check(live['executionId'] == data['executionId'] and live['invocationDigest'] == data['invocationDigest'], 'live 与终态绑定同轮身份及规格摘要')
        check(live['ownership']['supervisor']['startTicks'] > 0 and live['ownership']['parent']['namespacePids'], '持久化 supervisor 及父进程 namespace 身份')
        check(data['descendantsTerminated'], '全部自有后代终态确认')
        check('ECHILD' in data['ownership']['terminalProof'], '原subreaper必须给出内核ECHILD证明')
        return result.returncode, data, log.read_bytes()
    code, data, _ = execute('import time; data=bytearray(2*1024*1024); time.sleep(.3)')
    check(code == 0 and data['memory']['samples'] > 0 and data['memory']['sampledPeakRssBytes'] > 0, '实际自有轻量进程采样，不把读取缺失伪造为零')
    code, data, _ = execute('import subprocess,sys; data=bytearray(2*1024*1024); p=subprocess.Popen([sys.executable,"-c","import time; data=bytearray(2*1024*1024); time.sleep(.3)"]); p.wait()')
    check(code == 0 and data['memory']['maximumObservedProcesses'] >= 2, '真实父子进程同轮树采样，不限于直接根进程')
    code, data, log = execute('print("synthetic stdout"); import sys; print("stderr",file=sys.stderr)')
    check(code == 0 and data['state'] == 'completed' and b'stderr' in log, '成功及双输出流')
    code, data, _ = execute('raise SystemExit(7)')
    check(code == 1 and data['exitCode'] == 7, '非零退出保留')
    code, data, log = execute('print("x"*1000000)', cap=4096)
    check(code == 1 and data['state'] == 'output-limit' and len(log) == 4096, '日志硬上限')
    # 旁观进程由测试自己创建，不属于 helper；超时清理不得触碰它。
    bystander = subprocess.Popen([sys.executable, '-c', 'import time; time.sleep(30)'])
    try:
        code, data, _ = execute('import time; time.sleep(30)', timeout=.2)
        check(code == 1 and data['state'] == 'timeout', '超时停止自有任务')
        check(bystander.poll() is None, '无关进程保持存活')
    finally:
        bystander.terminate()
        bystander.wait(timeout=5)
    # 根退出，后代脱离 session 且关闭输出；仍由 subreaper 识别并回收。
    child = 'import os,time; os.setsid(); time.sleep(30)'
    code, data, _ = execute('import subprocess,sys; subprocess.Popen([sys.executable,"-c",' + repr(child) + '],stdout=subprocess.DEVNULL,stderr=subprocess.DEVNULL)')
    check(code == 1 and data['state'] == 'failed', '转 session 后代不漏记、不误报成功')
    # 后代保留 pipe 时不能无限等待 EOF；超时后确定终态。
    code, data, _ = execute('import subprocess,sys; subprocess.Popen([sys.executable,"-c","import time; time.sleep(30)"])', timeout=.3)
    check(code == 1 and data['state'] == 'timeout', '后代继承 pipe 仍有界')
    # 短期孤儿在宽限内自然完成，不把不同时间峰值当实测内存。
    code, data, _ = execute('import subprocess,sys; subprocess.Popen([sys.executable,"-c","import time; time.sleep(.1)"])')
    check(code == 0 and data['state'] == 'completed', '等待短期后代自然完成')
    # 仅测试进程注入一次状态读取竞态/意外错误，不在正式工具中加入故障开关。
    injector=root/'inject.py'
    injector.write_text("""import importlib.util,pathlib,subprocess,sys,errno
mode=sys.argv.pop(1)
spec=importlib.util.spec_from_file_location('bounded',sys.argv.pop(1))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
original=m.subprocess.Popen;read=pathlib.Path.read_text;state={'started':False,'injected':False}
def start(*a,**kw):
 p=original(*a,**kw);state['started']=True;return p
def race(self,*a,**kw):
 if state['started'] and not state['injected'] and self.name=='status' and self.parent.name.isdigit():
  state['injected']=True
  if mode=='race':raise ProcessLookupError(errno.ESRCH,'synthetic disappeared process')
  raise OSError(errno.EIO,'synthetic I/O fault')
 return read(self,*a,**kw)
m.subprocess.Popen=start;pathlib.Path.read_text=race
sys.exit(m.main())
""")
    for mode in ('race','unexpected'):
        target=root/mode;target.mkdir()
        result=subprocess.run([sys.executable,str(injector),mode,str(helper),'--working-directory',directory,'--log',str(target/'log'),'--receipt',str(target/'receipt.json'),'--timeout-seconds','4','--',sys.executable,'-c','import time;time.sleep(.3)'],timeout=15)
        data=json.loads((target/'receipt.json').read_text())
        check(data['descendantsTerminated'] and 'ECHILD' in data['ownership']['terminalProof'], '注入故障仍由原监督器证明后代回收')
        if mode=='race':
            check(result.returncode==0 and data['state']=='completed', '仅ESRCH扫描竞态可忽略')
        else:
            check(result.returncode!=0 and data['state']=='failed' and 'synthetic I/O fault' in data['error'] and data['traceback'], '意外错误保留原失败/堆栈，不假通过')
    # 原子发布、路径新鲜性与限频属于纯文件测试。
    sample = dict(ownership={"supervisor": None, "terminalProof": "不能泄露为 live 证明"},
                  invocationDigest="synthetic", startedUtc="synthetic")
    cp = module.LiveCheckpoint(root / 'pure.json')
    cp.write(sample, 'entry', force=True)
    initial_file = cp.live / '000001.json'
    initial = initial_file.read_bytes()
    cp.write(sample, 'running')
    check(initial_file.read_bytes() == initial and len(list(cp.live.glob('*.json'))) == 1, '一秒内普通采样不重复写盘')
    check('terminalProof' not in json.loads(initial)['ownership'], 'live 不复制终态证明')
    for path in (cp.target,):
        rejected = False
        try: module.LiveCheckpoint(path)
        except FileExistsError: rejected = True
        check(rejected, '已有 live 不可被新轮覆盖')
    link = root / 'linked-directory'; link.symlink_to(root, target_is_directory=True)
    rejected = False
    try: module.LiveCheckpoint(link / 'symlink.json')
    except OSError: rejected = True
    check(rejected, '父目录符号链接拒绝')
    # 不可变序号冲突必须拒绝，不覆盖别的内容。
    foreign = cp.live / '000002.json'; foreign.write_text('foreign')
    rejected = False
    try: cp.write(sample, 'running', force=True)
    except FileExistsError: rejected = True
    check(rejected and foreign.read_text() == 'foreign', '序号占用后拒绝覆盖并失败关闭')
    # 同一收据两个调用者的 claim 只有一个成功，失败者无终态发布资格。
    winner = module.LiveCheckpoint(root / 'race.json')
    rejected = False
    try: module.LiveCheckpoint(root / 'race.json')
    except FileExistsError: rejected = True
    check(rejected and not winner.target.exists(), '并发 claim 失败者不能抢写另一轮终态')
    winner.write(sample, 'entry', force=True)
    winner.publish(winner.target, {'owner': winner.execution_id}, initial=True)
    check(json.loads(winner.target.read_text())['owner'] == winner.execution_id, '只有 claim owner 发布终态')
    bounded = module.LiveCheckpoint(root / 'bounded.json')
    bounded.sequence = 256
    rejected = False
    try: bounded.write(sample, 'running', force=True)
    except RuntimeError: rejected = True
    check(rejected and bounded.disabled, '达到数量上限显式失败关闭，不静默停止检查点')
    oversized = module.LiveCheckpoint(root / 'oversized.json')
    rejected = False
    try: oversized.publish(oversized.live/'000001.json', {'value':'x'*65536}, initial=True)
    except RuntimeError: rejected = True
    check(rejected and not list(oversized.live.iterdir()), '单份live硬上限64KiB，超限不发布残缺JSON')
    # 虚拟时间覆盖默认及最长执行，不等待真实长测，也不制造数百次磁盘写入。
    real_monotonic = module.time.monotonic
    try:
        for timeout in (1800, 86400):
            clock = [0.0]
            module.time.monotonic = lambda: clock[0]
            long_run = module.LiveCheckpoint(root / f'long-{timeout}.json', timeout)
            snapshots = []
            long_run.publish = lambda target, payload, initial: snapshots.append(payload)
            long_run.write(sample, 'entry', force=True)
            long_run.write(sample, 'running', force=True)
            for second in range(1, timeout + 1):
                clock[0] = float(second)
                long_run.write(sample, 'running')
            long_run.write(sample, 'cleanup', force=True)
            clock[0] += 5
            long_run.write(sample, 'cleanup')
            long_run.write(sample, 'supervisor-error', force=True)
            check(len(snapshots) <= 245 and not long_run.disabled, '默认及24小时虚拟运行保留清理检查点余量')
            check(snapshots[-1]['phase'] == 'supervisor-error' and all(not item['terminalProof'] for item in snapshots), '长期运行后清理错误仍可记录且永不充当终态')
    finally:
        module.time.monotonic = real_monotonic
    # 已锚定目录被重命名并替换为链接，也不能把发布导向外来目录。
    anchored = module.LiveCheckpoint(root / 'anchored.json')
    saved = root / 'saved-live'; anchored.live.rename(saved)
    unrelated = root / 'unrelated'; unrelated.mkdir()
    anchored.live.symlink_to(unrelated, target_is_directory=True)
    anchored.write(sample, 'entry', force=True)
    check((saved/'000001.json').exists() and not list(unrelated.iterdir()), '目录替换不会改变 dirfd 发布目标')

    # 写故障只在测试注入：entry 失败不能启动；周期写失败必须由原 owner 清理。
    fault = root / 'checkpoint-fault.py'
    fault.write_text("""import importlib.util,sys,errno
mode=sys.argv.pop(1)
spec=importlib.util.spec_from_file_location('supervisor',sys.argv.pop(1))
m=importlib.util.module_from_spec(spec);spec.loader.exec_module(m)
original=m.LiveCheckpoint.publish
count=0
def publish(self,target,payload,initial):
 global count
 if target!=self.target:
  count+=1
  if mode!='fsync' and count >= (1 if mode=='entry' else 3):
   raise OSError(errno.EIO,'synthetic checkpoint I/O failure')
 return original(self,target,payload,initial)
m.LiveCheckpoint.publish=publish
sync=m.os.fsync;sync_count=0
def fsync(fd):
 global sync_count
 sync_count+=1
 if mode=='fsync' and sync_count==6:raise OSError(errno.EIO,'synthetic checkpoint I/O failure')
 return sync(fd)
m.os.fsync=fsync
sys.exit(m.main())
""")
    for mode in ('entry', 'periodic', 'fsync'):
        target = root / ('checkpoint-' + mode); target.mkdir()
        receipt = target / 'receipt.json'
        marker = target / 'started'
        command = 'from pathlib import Path;import time;Path(' + repr(str(marker)) + ').touch();time.sleep(10)'
        result = subprocess.run([sys.executable,str(fault),mode,str(helper),'--working-directory',directory,
                                 '--log',str(target/'log'),'--receipt',str(receipt),'--timeout-seconds','4','--',sys.executable,'-c',command], timeout=15)
        data = json.loads(receipt.read_text())
        check(result.returncode != 0 and 'synthetic checkpoint I/O failure' in data['error'], '检查点 I/O 失败不可吞成成功')
        check(data['descendantsTerminated'], '写失败仍完成原 owner 收尾')
        if mode == 'entry':
            check(not marker.exists() and data['ownership']['root'] is None, 'entry 发布失败绝不启动子进程')
        else:
            check(marker.exists() and 'ECHILD' in data['ownership']['terminalProof'], '周期写失败后由原 subreaper 取得 ECHILD')
            live = json.loads(sorted(Path(str(receipt) + '.live').glob('*.json'))[-1].read_text())
            check(live['state'] == 'unknown' and live['sequence'] == 2, '最后成功 live 不被失败写破坏或伪造终态')

    # 外层合成测试拥有自己的 subreaper；杀死监督器后只回收该测试子树。
    # 不用新进程的观测给被杀 supervisor 补发终态收据。
    killed = root / 'killed'; killed.mkdir()
    kill_test = root / 'kill-test.py'
    kill_test.write_text("""import ctypes,json,os,pathlib,signal,subprocess,sys,time
root=pathlib.Path(sys.argv[2]);receipt=root/'receipt.json';live=pathlib.Path(str(receipt)+'.live')
assert ctypes.CDLL(None,use_errno=True).prctl(36,1,0,0,0)==0
p=subprocess.Popen([sys.executable,sys.argv[1],'--working-directory',str(root),'--log',str(root/'log'),'--receipt',str(receipt),'--',sys.executable,'-c','import time;time.sleep(2)'])
try:
 deadline=time.monotonic()+5
 while True:
  files=sorted(live.glob('*.json'))
  if files:
   data=json.loads(files[-1].read_text())
   if data['phase']=='running' and data['ownership']['root']['observed'] is not None:break
  assert time.monotonic()<deadline
  time.sleep(.02)
 p.kill();p.wait(timeout=5)
 assert not receipt.exists()
 data=json.loads(sorted(live.glob('*.json'))[-1].read_text())
 assert data['state']=='unknown' and data['terminalProof'] is False and not data['descendantsTerminated']
 assert data['ownership']['supervisor']['startTicks']>0 and data['ownership']['root']['observed']['pidNamespace']
finally:
 if p.poll() is None:p.kill();p.wait(timeout=5)
 # 有界自然退出的合成后代被本测试 subreaper 接收；等待其回收，不据此改 live。
 deadline=time.monotonic()+5
 while True:
  try:pid,status=os.waitpid(-1,os.WNOHANG)
  except ChildProcessError:break
  assert time.monotonic()<deadline
  if pid==0:time.sleep(.02)
""")
    result = subprocess.run([sys.executable,str(kill_test),str(helper),str(killed)], timeout=15)
    check(result.returncode == 0, 'SIGKILL 后原子 live 保留身份但终态收据缺失、状态仍 unknown')
print(f'PASS: {checks} synthetic Linux supervisor checks')
