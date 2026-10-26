# AuthWorker 协议 v3

主程序启动参数仍只有随机 pipe 名与 64 位十六进制 nonce；Named Pipe 限当前用户，并核对实际连接子进程 PID。所有 JSON 帧使用 4 字节小端长度、UTF-8、5 MiB 上限、严格未知字段拒绝和有界深度。

WorkerRequest 包含 protocolVersion=3、requestId、accountGeneration、nonce、taskKind、allowedHosts、deadlineUtc，以及可空 profileScopeId。profileScopeId 只能为 64 个十六进制字符，不是可执行路径、任意文件名、URL 或凭据。

默认运行目录对应 null scope，继续使用既有 `%LOCALAPPDATA%/DLUTDesktopTimetable/auth-profile`。显式独立数据目录以规范绝对路径大写字符串的 SHA-256 生成 scope，worker 在固定应用目录下解析为 `profiles/<scope>/auth-profile`。工作目录原文不进入 worker 参数或管道，不复制默认会话；不同数据目录不共用登录资料。

scope 字段在 v2 引入。本次 v3 在 WorkerDiagnostics 增加可空 retryAfterUtc：取消/关闭同样返回学校限流截止时间，主程序持久化设置并限制下一次手动刷新。不能因关闭窗口而绕过该期限；期满只恢复准入，不自动抓取。主程序与随包 worker 必须匹配，不静默兼容旧进程。数据库 user_version=2、标准课表 schemaVersion=1.0、脱敏 ObservationReport version=1 是独立版本，不随 IPC 一同变更。

取消控制仍使用相同 requestId、generation、nonce。完成结果在主程序核对当前 generation；清会话先增加 generation，取消并等待自有 worker/browser 退出，再获取相同 profile mutex 清理目录。迟到旧结果不能覆盖当前操作或课表。

当前 ReadTimetable 仍返回 UnsupportedSchema；本期只从用户触发的 InteractiveLogin 进入学校页面并读取本人课表。成功在内存标准化后返回 Completed/datasetJson，主程序校验并事务提交；其他状态不得携带 dataset。原始学校响应与凭据不进入业务数据库；纯读取/映射文件由 worker 编译链接复用，worker 不引用 SQLite 或写业务数据库。实际联调见 [本人导入](../validation/real-school-import-stage.md)，v3 的限流及恢复验证见 [刷新反馈](../validation/school-refresh-feedback-stage.md)。旧 v1/v2 生命周期报告保留历史版本语义。
