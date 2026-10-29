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
        check(data['descendantsTerminated'], '全部自有后代终态确认')
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
print(f'PASS: {checks} synthetic Linux supervisor checks')
