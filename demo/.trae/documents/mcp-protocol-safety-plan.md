# MCP 协议优化计划

## Context
其他 agent 实测发现：全局 INI 可指向另一 Unity 工程，分组信息冗长，连接字段重复。代码另显示 tools/call 等待超时并不取消执行；已有 AsyncCall 存储仅覆盖显式异步调用，不能代表所有写入。目标是先确保目标正确、结果可查，再降低发现成本。不自动重放失败操作。

## 实施范围与顺序

### 1. 工程与实例绑定
- Node CLI 和 Client 增加 expected_project；显式值优先，否则从 cwd 向上寻找 Unity 工程根。不得把发现返回的路径当作期望路径。
- 对路径做平台适配与规范化。无可确定目标时，允许发现、概览及 schema 查询；工具执行须先明确绑定。
- discovery 发布随机 instance_id，服务重启或域重载后更换。客户端持有已确认绑定；实例变化时报错，须重新发现并确认，不自动重放。
- 执行请求携带工程与实例；服务在接收及入队执行前校验，防发现后发生端口复用。错连时目录查询明确提示；工具调用与未知副作用路由拒绝。暂不假定 GET 或工具名可证明只读。
- 校验覆盖自定义路由入口。外部绝对 URL 不携带 Unity 绑定信息；跨源操作须明确区分，不能宣称已受工程保护。
- 变更执行契约后同步仓库内技能说明；旧客户端执行请求缺绑定时应获明确迁移错误，不静默放行。

### 2. 请求状态查证
- 为工具执行加入 request_id，客户端发送前生成；超时错误保留 ID、实例及查询方式。
- 独立建立有界台账，复用现有队列及完成回调：accepted、running、completed、failed、unknown。记录先于入队；查询不依赖 Unity 主线程空闲。
- 重复 ID 不重新执行；参数不同报冲突。同 ID 查询已有状态，不宣称全局幂等。
- 10 秒等待超时不改写执行终态；迟到完成仍更新记录。
- 重启、记录淘汰或查询不到均为 unknown，不等于未执行。failed 不代表无副作用；partial 仅在工具明确报告时提供，不推断。
- 明确容量、保留期、结果大小与实例有效期。借鉴 AsyncCallResultStore 的有界存储，不复用其完成后可覆盖 ID 的语义。

### 3. 轻量概览与日志
- 基于既有目录快照新增轻量概览：去重工具数、业务分类与短述；分类与细分标签分离，重叠计数明确标注。
- 仅发布可核实的依赖状态；未检测的依赖标 unknown，不以已注册当作可用。不为获取概览加载全量 schema。
- CLI 默认 stdout 只输出结果；连接变化或错连用精简 stderr 提示，完整连接诊断由 --verbose 控制。库默认静默保持不变。
- 概览不替代执行前的 schema 获取。

## 关键文件与复用点
- unity-package/Skills/unity-skilled-mcp/scripts/unity_mcp.js：Client、configure、连接发现、requestHttp、rpcCall、CLI 日志。
- unity-package/Editor/Connect/McpDiscovery.cs：McpConnectionInfo.ToJson、目录快照、groups、连接配置发布。
- unity-package/Editor/Connect/McpService.cs：HandleMcpRequestAsync、ProcessMcpRequest、ProcessMessageQueue 与完成回调。
- unity-package/Editor/Model/AsyncCallResultStore.cs：借鉴有界记录与重载处理；不改变现有显式异步语义。
- 仅在职责必要时新增请求状态存储类；同步修改仓库技能说明与现有测试。

## 验证
- Node：node --test unity-package/Skills/unity-skilled-mcp/tests/test_unity_mcp.js；先确认现有测试入口。
- Unity：运行现有 McpDiscoveryTests 与新增绑定、状态台账测试；若 Unity 不可用，明确标注未验证，不宣称编译通过。
- 覆盖两个工程、大小写与路径别名、全局 INI 切换、发现后重启、排队时实例失效、未知及自定义路由。
- 覆盖超时后完成、同 ID 冲突、异常后状态、记录淘汰、重载后 unknown；证明不会自动重放。
- 覆盖去重总数、多标签重叠、未知依赖、stdout 可解析、stderr 不重复刷屏。
- 实测先做只读调用；写入验证仅用明确隔离的临时测试对象，不动用户场景或资产。

## 本轮不做
schema 哈希强制匹配、全面副作用元数据迁移、统一取消和撤销协议另批处理。取消需要执行器协作，不能以 HTTP 超时或删除记录冒充取消。不自动提交或推送。
