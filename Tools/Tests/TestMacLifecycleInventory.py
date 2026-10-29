#!/usr/bin/env python3
"""只验证清单不会把 API/常量存在误报为生命周期证明；不是 macOS 原生验收。"""
import importlib.util
from pathlib import Path
from unittest.mock import patch

spec = importlib.util.spec_from_file_location('inventory', Path(__file__).parents[1] / 'Inspect-MacLifecycle.py')
module = importlib.util.module_from_spec(spec)
spec.loader.exec_module(module)

with patch.object(module.sys, 'platform', 'linux'), patch.object(module.ctypes, 'CDLL') as load:
    result = module.inspect()
    assert result['Reason'] == 'not-macos-no-native-inspection'
    load.assert_not_called()

for library in (object(), type('Symbols', (), {'proc_listchildpids': 1, 'proc_pidinfo': 1, 'proc_signal_with_audittoken': 1})()):
    with patch.object(module.sys, 'platform', 'darwin'), patch.object(module.platform, 'mac_ver', return_value=('fixture', '', '')), patch.object(module.ctypes, 'CDLL', return_value=library):
        result = module.inspect()
        assert not any(result[key] for key in ('SupervisorSupported', 'DescendantTrackingProven', 'AtomicSignalIdentityProven', 'NativeTestsRun'))
with patch.object(module.sys, 'platform', 'darwin'), patch.object(module.platform, 'mac_ver', return_value=('fixture', '', '')), patch.object(module.ctypes, 'CDLL', side_effect=OSError):
    result = module.inspect()
    assert result['Reason'] == 'libproc-symbol-inspection-unavailable' and not result['SupervisorSupported']
print('PASS: 4 inventory contract cases; no macOS native execution')
