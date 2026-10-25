# AppEgress

**Windows 应用网络出口分析与路由管理工具。**

同时开启多个 VPN、系统代理和本机隧道时，应用的连接很容易变成黑盒。AppEgress 将应用连接、对端进程、代理入口和 Windows 路由线索放在一起展示，并提供独立的指定网卡规则。

[English](README.md) · [Русский](README.ru.md)

维护者：[ccut2014](https://github.com/ccut2014)。本项目基于 kulich Software / li4inka 的 [TrafikRuler](https://github.com/li4inka/trafik-ruler) 修改，保留原作者、图标来源与许可；不是上游官方发布。详见 [来源说明](UPSTREAM.md)。

## 功能

| 功能 | 说明 |
|---|---|
| 仅监控 | 无需指定网卡，查看当前 TCP 连接、UDP 本地绑定，不改变流量。 |
| 本机对端 | 根据完整连接匹配进程与 PID，区分已匹配对端和监听候选。 |
| 当前出口线索 | 分开展示进程自身连接的网卡查询、本机对端进程的其他连接及 VPN 网卡名称。 |
| 系统代理入口 | 判断是否匹配配置的代理入口；共享上游、最终出口及代理扣量保留为未知。 |
| 指定网卡 | 为匹配的 exe 选择目标网卡，实际可用性受 Windows 路由和 VPN 策略影响。 |
| 原生界面 | 明暗主题、托盘、简体中文、英文和俄文。 |

## 使用前了解

- 路由查询不等于数据包已通过该 VPN；不能据此确认最终公网出口或代理扣量。
- 对端进程可能服务多个应用，其其他连接不能直接归属到当前应用。
- UDP 目前只显示本地端点；监控模式不采集流量速度。
- “当前快照”不是历史记录、命令次数或数据包计数。
- 规则匹配指定的程序路径或文件名，不会自动包含不同名称的子进程。
- 指定网卡功能仍存在无法识别进程、停止拦截等放行情形，不提供绝对断网隔离保证。

更多信息：[监控边界](docs/monitoring.md)、[多 VPN 与转发边界](docs/multiple-vpns.md)。

## 运行

需要 Windows 10/11 x64、.NET 8 **Desktop Runtime x64**、管理员权限。默认发布轻量版，使用本机已安装的 .NET；WinDivert 两个文件与主程序放在一起。

1. 发布后从 [Releases](https://github.com/ccut2014/app-egress/releases) 下载并完整解压。
2. 从旧程序托盘菜单退出 TrafikRuler/AppEgress，再运行 `AppEgress.exe`。
3. 添加应用，选择“仅监控”或“指定网卡”；若暂停，先点击“恢复”。
4. 展开连接详情，选中连接查看关联信息。语言在 Settings → Language 中选择。

## 旧设置兼容

新设置和日志使用 `%APPDATA%\AppEgress`。新设置文件不存在时，会读取 `%APPDATA%\TrafikRuler\settings.json`；后续保存写入新目录，旧文件保留。

为防止新旧两个流量引擎同时运行，保留了旧版的单实例标识。开机启动也沿用旧任务槽；在 AppEgress 中重新开启开机启动，才会更新为新程序路径。更名本身不会操作本机任务计划程序。

## 开发与打包

安装 .NET 8 SDK，在项目根目录执行：

```powershell
./scripts/get-windivert.ps1
dotnet restore AppEgress.sln
dotnet build AppEgress.sln -c Release
./scripts/package.ps1 -Version 1.1.0
```

默认轻量包输出到 `dist/`，源码单独提供。打包脚本不自动执行测试或启动程序。需验证时可另行运行：

```powershell
dotnet test AppEgress.sln -c Release
```

## 许可与来源

沿用 GPL-3.0-or-later，保留上游版权和第三方声明。详见 [UPSTREAM.md](UPSTREAM.md)、[LICENSE](LICENSE)、[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。
