#!/usr/bin/env python3
"""只读收集 macOS 生命周期研究前提；不启动任务、不发信号、不授予执行资格。"""
import ctypes
import json
import platform
import select
import sys


def inspect():
    result = {
        'SchemaVersion': 1,
        'Scope': 'macos-lifecycle-inventory-only',
        'Platform': sys.platform,
        'Architecture': platform.machine(),
        'MacOSVersion': platform.mac_ver()[0] if sys.platform == 'darwin' else None,
        'SupervisorSupported': False,
        'DescendantTrackingProven': False,
        'AtomicSignalIdentityProven': False,
        'NativeTestsRun': False,
        'StrictFallback': 'Tools/Check-Development.ps1',
        'Reason': 'macos-native-lifecycle-not-proven',
        'ObservedSymbols': {},
    }
    if sys.platform != 'darwin':
        result['Reason'] = 'not-macos-no-native-inspection'
        return result
    # 常量存在不能证明内核实现：Apple 已废弃 NOTE_TRACK/NOTE_CHILD。
    result['KqueueAvailable'] = hasattr(select, 'kqueue')
    result['NoteTrackConstantPresent'] = hasattr(select, 'KQ_NOTE_TRACK')
    try:
        library = ctypes.CDLL('/usr/lib/libproc.dylib')
        for name in ('proc_listchildpids', 'proc_pidinfo', 'proc_signal_with_audittoken'):
            # 只查符号，不调用查询其它进程或发送信号的 API。
            result['ObservedSymbols'][name] = hasattr(library, name)
    except OSError:
        result['Reason'] = 'libproc-symbol-inspection-unavailable'
    return result


if __name__ == '__main__':
    print(json.dumps(inspect(), ensure_ascii=False, indent=2))
    # 0 仅表示清单读取结束，不是执行器合格；非 Mac 不伪装原生检查。
    sys.exit(0 if sys.platform == 'darwin' else 2)
