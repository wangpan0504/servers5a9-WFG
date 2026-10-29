# macOS 重型任务生命周期边界

当前没有可安全归还共享重型许可的 macOS 新执行器。继续使用[原严格入口](../../CONTRIBUTING.md#检查范围)；不把下列只读清单或 Linux/Windows 结果当作 Mac 原生验收。原严格入口本身也不因此新增全后代预算保证。

## 已核实的系统接口限制

- Apple XNU 的 [`bsd/sys/event.h`](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/sys/event.h) 明确 `NOTE_TRACK`、`NOTE_TRACKERR`、`NOTE_CHILD` 自 macOS 10.5 起不再支持。因此 Python 暴露常量、存在 `kqueue`，都不能证明自动跟踪 `fork` 后代。FreeBSD 或旧 macOS 手册不能替代当前 Darwin 合同。
- Apple [`launchd.plist.5` 的 `AbandonProcessGroup`](https://github.com/apple-oss-distributions/launchd/blob/main/man/launchd.plist.5) 描述的是同进程组清理；不证明 `setsid` 脱组后代也被终止。不能以提交 launchd job 或 `killpg` 成功作为整树完成。
- Apple [`libproc.c`](https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/wrappers/libproc/libproc.c) 有 `proc_signal_with_audittoken`，内核 [`proc_info.c`](https://github.com/apple-oss-distributions/xnu/blob/main/bsd/kern/proc_info.c) 转交 `psignal_by_audit_token`。这提供进一步核验身份定向信号的入口，**不证明**工具已取得可信 token、无遗漏的后代集合、目标系统支持或权限。相关 [`libproc.h`](https://github.com/apple-oss-distributions/xnu/blob/main/libsyscall/wrappers/libproc/libproc.h) 亦注明接口可能变化。

最小未闭合项是：从任务开始前建立无遗漏的后代归属，覆盖迅速双 fork/脱组/父退出；持不可混淆的进程身份安全停止；确认最后后代终态后才归还许可。轮询父 PID 或启动时间不是原子信号句柄，不能消除“校验后 PID 被复用”的间隙。仅增加日志或延长等待不会补足这些条件。

## 原生核验准备

在目标 Mac 的仓库根可运行：

```sh
python3 Tools/Inspect-MacLifecycle.py > mac-lifecycle-inventory.json
```

脚本只记录 OS/架构和系统库符号可见性，不创建子任务、不安装 daemon、不发信号、不操作其它进程。输出固定 `SupervisorSupported=false`；Mac 上退出 0 仅表示完成清单，不是合格执行器。其它平台退出 2 且不加载 Mac 库。清单保存在本地，不提交 Git。

下一原生实验必须先选择有受支持合同的归属/身份方案，再在专用测试域只运行自建短命进程，至少验证：

| 场景 | 必须建立的结果 |
|---|---|
| 根正常/非零退出；后代稍后退出 | 不在根退出时提前报整树终态 |
| 双 fork、`setsid`、关闭全部输出 | 后代仍被准确归属，不能借管道 EOF 猜退出 |
| fork 与关闭并发；事件丢失/溢出 | 未知即隔离许可，不能补成功 |
| 身份过期及同 PID 新实例 | 旧身份不得停止新实例；不以真实高频 PID 复用压力冒险代替受控验证 |
| 旁观进程；取消/超时/日志超限 | 仅处置本任务所有者，日志有界，保留原失败 |
| 监督器异常/被终止；回执缺失 | 预算残留保持隔离，不按 TTL/PID 不存在自动接管 |

若方案要求特权、系统扩展、持久 launchd 服务或新的签名权限，应先单独审查和授权；当前工具不安装这些组件。即使原生生命周期闭合，跨工作树缓存仍需独立核对 Mac 工具链、native 库、环境、输入和实际完整成功收据，不复用 Linux profile 的资格。
