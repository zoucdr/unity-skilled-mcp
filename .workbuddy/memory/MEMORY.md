# 长期项目偏好

- 工具名称保持简短，避免 `_workflow` / `_workflows` 赘词；优先复用已有前缀。合并前核查完整 `prefix.action` 冲突；仅冲突的新动作改名，旧工具不动，不加重复注册或兼容别名。
- 同前缀可有不同实现模块；严格参数校验必须按具体 Owner 的 `IStrictToolModule` 标记，不按前缀扩大到旧模块。
- 代码定位遵循 codebase-graph 图优先；保留工作区已有改动。除非明确要求，不 commit/push；当前文档同步，历史日志不追溯替换。
