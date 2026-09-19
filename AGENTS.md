# AGENTS.md

给在本仓库工作的 AI agent（以及偶尔上手的维护者）看的约定。
**改代码前先扫一眼 `docs/pitfalls.md`** —— 那里记着踩过的坑，很多看起来"显然"的写法其实是错的。

## 这个项目是什么

WinUI 的 M3U8 下载器。三个项目，**严格单向依赖**：

```
M3U8Downloader.Core              内核 + 存储（唯一第三方依赖：Microsoft.Data.Sqlite）
  ├── M3U8Downloader.App         WinUI 界面
  └── tests/M3U8Downloader.SelfTest   自检（无界面、不依赖外网）
```

**Core 的第三方依赖只允许一个方向：存储。** 加了新包就等于把负担摊给另外两个项目，
动手前先看下面「存储」一节里那笔账。

## 平台事实与依赖边界（2026-09 核实过，别再重复踩）

**术语**：微软已把 "WinUI 3" 正式改称 **WinUI**（UWP 那套改称 "WinUI for UWP"），
并在 Build 2026 明确 WinUI 是 Windows 11 的长期 UI 框架 —— **不会再有 "WinUI 4" 来替换它**。
本项目用 **Windows App SDK 2.4.0**；包名仍是 `Microsoft.WindowsAppSDK`（它是 WinUI 的
分发包，不是另一个 UI 框架）。文档与注释请跟随新叫法，别写 "WinUI 3"。

**别把元包换成组件包**：2.x 的 SDK 拆成了 `Microsoft.WindowsAppSDK.WinUI` / `.Base` /
`.Foundation` / `.AI` / `.ML` / `.Search` / `.Widgets` 等组件，官方说可以按需引用来减小包体。
但 `H.NotifyIcon.WinUI` 声明依赖的是**元包** `Microsoft.WindowsAppSDK >= 1.6` —— 一旦不显式引
元包，它会把 1.6 元包拖回来和 2.x 组件混用（还附带 NU1603 警告）。实测过，收益不确定，已回退。

**所以那堆用不到的东西是发布之后清掉的**：元包会把 AI / ML / Search / Widgets / Workloads
全带上 —— 实测 **61 个文件、51.5 MB**（`onnxruntime.dll` 21 MB、`DirectML.dll` 18 MB、
`Microsoft.Windows.Search.dll` 3.2 MB），而本项目的代码对这些 API **零引用**。
`scripts/publish.ps1` 在 `dotnet publish` 之后按文件名模式删掉它们，另外把 WinUI 自带的
~85 个语言资源目录砍到 `zh-CN` / `zh-TW` / `en-us`（**82 个目录、3.2 MB**）。
这一步是实测过的：清理后启动，窗口正常出现 —— 顺带证明了 SQLite 原生库与 XAML 资源索引
没被误删。**但哪天真要用到这些 API，必须把名字从脚本里的 `$unusedPattern` 去掉**，
否则是运行时才炸，编译期完全看不出来。
（顺带一提：发布体积 225 MB → 174 MB，而**我们自己的代码只占 3.2 MB**。）

**托盘图标（通知区域）没有官方 API**，Windows App SDK 2.4 仍然没有：

- `AppWindow.SetTaskbarIcon` 是**任务栏上的窗口图标**（与 `SetTitleBarIcon` 并列）
  —— 不是通知区域图标，别拿它当替代品；
- 官方《[Windows notifications overview](https://learn.microsoft.com/windows/apps/develop/notifications/)》
  只列了应用通知（`AppNotificationManager`）、推送、徽章三类，没有托盘图标；
- 官方仓库里从 [#519](https://github.com/microsoft/WindowsAppSDK/discussions/519)（2021）
  问到 [#3394](https://github.com/microsoft/WindowsAppSDK/discussions/3394)（2023）
  **都没有"已计划"的答复**；结论是托盘只能走 `Shell_NotifyIcon`。

**所以 `H.NotifyIcon` 的哪些部分能替、哪些不能**：

| 现在用 H.NotifyIcon 做的 | 官方替代 | 结论 |
|---|---|---|
| 气泡通知 `ShowNotification` | WinAppSDK 的 `AppNotificationManager`（官方推荐，打包/非打包都能用） | **可替**，且体验更好：正规 toast、能进通知中心、可点击 |
| `WindowExtensions.Hide/Show` | Win32 `ShowWindow` —— 几行 P/Invoke | **可替** |
| 托盘图标 + 右键菜单 | **没有** | 替不掉。要么留 H.NotifyIcon，要么自己封 `Shell_NotifyIcon`（得处理 `TaskbarCreated` 重新注册、图标句柄、DPI），要么改用 WinForms 的 `NotifyIcon`（.NET 官方组件、自动重注册，但菜单是 `ContextMenuStrip`、不跟随 WinUI 主题） |

结论是**保留 `H.NotifyIcon`** —— 它承担的那部分（托盘图标）恰恰是官方不提供的；
把通知换成 `AppNotificationManager` 属于可选的体验改进，代价是要额外处理
`Register()/Unregister()` 与"点通知激活应用"跟单实例逻辑的配合。

## 存储（统一库）

**设置、任务列表、下载历史全在一个 SQLite 库里：`data\m3u8.db`**（路径见 `AppPaths.DatabaseFile`）。
实现在 `Core/Storage/`：`SqliteDatabase`（连接 + 建表）、`SqliteSettingsStore`、
`SqliteTaskStore`、`SqliteDownloadHistory`。

```
settings            一行一列一项（CHECK (id = 1) 保证只有一行）
downloads           下载历史，主键 (page_url, episode_number)
tasks               任务本体，一个任务一行
task_episodes       任务里每一集一行
task_snapshots      站点快照，一个任务一行
snapshot_episodes   快照里每一集一行（站点上全部集）
meta                schema 版本
```

**为什么允许 Core 引 SQLite（这条约定是改过的，别照旧文档写）**：
早先的规矩是"Core 零第三方包，存储实现挂界面层"。代价是 Core 里留一套接口、App 里写一份实现 ——
而自检只依赖 Core，**测不到真实现**，SQL 语句得另起一个 `_diag` 项目单独兜。
引进来之后的总账：两个项目各多一个 `e_sqlite3.dll`（1.9 MB，清理后的自包含发布 174 MB 里占 1.1%），
换来存储实现只此一份、自检直接测真货。
**但方向仅限存储** —— 别的功能想引包，先回来把这笔账重算一遍。

**连接约定**（`SqliteDatabase.Open()`）：连接串必须带 `Pooling=false`（理由见
`docs/pitfalls.md` 第 29 条，那是真被坑过的地方）；每次开连接都设 `busy_timeout=3000`；
**所有读写都自己吞异常** —— 库坏了不该让程序起不来（设置退回默认值、任务读成空表、
历史少一条账，程序照常跑）。

**表设计约定**：

- **设置是"一行一列一项"，不是 key-value。** 那样只是把 JSON 的字段拆成行，
  除了"在数据库里"没有任何好处，还丢掉列类型。
- **读的时候缺列 / 为 NULL / 压根没有那一行，一律退回代码里的默认值** ——
  所以以后加设置项是安全的，不需要为它写迁移。
- 动态 key 的小字典（快照里的请求头）直接存一列 JSON，不单独建表。
- 需要顺序的东西（任务列表）存 `sort_order`，读回来按它排。

**没有旧文件迁移**：早先版本把三个文件合并进这个库时写过迁移逻辑，后来删掉了
（个人项目，没有别的用户要照顾，也省得每次改 schema 都要照顾旧文件格式）。
所以库不存在时就是一个全新的空库；数据目录里如果还留着
`settings.json` / `tasks.json` / `downloads.db`，程序**不会读它们**。

## 常用命令

```powershell
# 编译界面
dotnet build src\M3U8Downloader.App\M3U8Downloader.App.csproj -c Debug -p:Platform=x64 -r win-x64

# 自检：唯一的回归防线，改完 Core 必须跑；退出码 0 = 全过
dotnet run --project tests\M3U8Downloader.SelfTest -c Debug

# 自包含发布
.\scripts\publish.ps1 [-Version x.y.z]

# 按 .editorconfig 重排代码排版（只动空白与换行，不改语义）
dotnet format whitespace <项目或解决方案>
```

- **发布前先杀掉正在运行的 `M3U8Downloader.exe`**，否则 `publish/app-win-x64` 被占用、publish 失败。
- **发布后启动一次产物，确认窗口能出来** —— `publish` 成功不代表能跑：缺 XAML 资源索引（PRI）时
  build 与 publish 都一声不响，只有真正启动才报 `XamlParseException`。
  见 `docs/pitfalls.md` 第 34 条。
- 自检跑的是本地 `HttpListener` 上的假 HLS 服务器，**不访问外网**，CI 里也能跑。

## 版本号

`Directory.Build.props` 里统一维护，三个项目共用；界面标题栏与「关于」都读它。

**发版时 git tag 必须和它一致**，否则「下载到的包」与「源码构建的包」会显示不同的版本号。

## 发版流程

1. 改 `Directory.Build.props` 的 `<Version>`，提交；
2. 打 tag `vX.Y.Z` 并推送 —— CI（`.github/workflows/release.yml`）自动跑自检、
   自包含打包、发 Release；
3. **Release 页面必须包含「本次解决的问题」一节** —— 用户点进 Release 第一眼看的是
   「这版修了什么」，不是安装说明。这一节由 CI 从
   `上一个 tag..本 tag` 的**提交标题**自动生成（见 workflow 的「整理更新说明」步骤）。

因为它是从提交标题生成的，所以：

- **提交标题要写成人能看懂的一句话**，说清"做了什么、为什么"，别写 `fix bug` / `update`；
- 详细内容（踩过的坑、验证方式）写在提交正文里，Release 说明只取标题；
- `版本号 x.y.z` 这类提交会被自动过滤掉，可以放心提交。

## 提交信息约定

- 标题一句话，**中文**，说清做了什么；
- 正文写：为什么改、怎么验证的、有什么已知限制；
- **提交信息里含英文双引号时不要用 `-m` 直接传**：`git.exe` 是原生命令，PowerShell 会
  重组命令行，消息会被引号对拆成多个参数，git 报一堆 `pathspec ... did not match`
  （提交不会执行，但 `git add` 已经生效，文件留在暂存区）。写进临时文件再
  `git commit -F <文件>` 最稳。
- **写这类临时文件要用无 BOM 的 UTF-8**：这台机器上的 PowerShell 是 **5.1**，
  `Set-Content -Encoding utf8NoBOM` 它不认（那是 PowerShell 7 才有的枚举值），
  用 `[System.IO.File]::WriteAllText($path, $msg, (New-Object System.Text.UTF8Encoding($false)))`。
  BOM 混进提交标题的话，会一路显示到 Release 说明里（那一节由提交标题生成）。
- 修 bug 的提交，同时往 `docs/pitfalls.md` 补一条。

## 几条硬约定（都是踩过坑换来的）

- **要用直连就必须显式 `UseProxy = false`**。`SocketsHttpHandler.UseProxy` 默认是 `true`，
  不显式关掉就会退到系统代理（用户的 Clash）—— 白耗流量，还会被站点按代理 IP 拒绝。
- **绑定属性只能在 UI 线程改**。后台线程改 WinUI 绑定属性**不报错但界面不刷新**，
  现象是任务永远停在"下载中"。
- **后台线程不要遍历界面绑定的 `ObservableCollection`**，先在 UI 线程快照一份。
- **站点适配是模块化的**：新增站点 = 在 `Core/Sites/` 加一个 `ISiteAdapter` 实现，
  反射会自动登记（按 `Priority` 排序）。**不要改注册代码**。
- **存储只有一份，就在 `Core/Storage/`**。设置、任务、历史都在统一库里 ——
  别在界面层另起一份存储实现（那正是当初"存储挂界面层"留下的病：自检测不到真实现）。
  新增要落盘的东西，先想清楚它属于哪张表，而不是新开一个文件。
- **「下过没有」以磁盘文件为准**。任务记录会被「清理已完成」清掉、重装会丢，
  视频却还躺在文件夹里。判断某集下载过没有，用 `EpisodeFileScanner` 按文件名模板
  正向算出文件名再查文件；任务记录与下载历史只用来说明"为什么没有"。
- **数据路径一律走 `AppPaths`，不要自己拼 `%APPDATA%`**。数据目录优先程序目录下的
  `data\`（便携：拷走整个文件夹即带走全部数据），不可写时退回 `%APPDATA%\M3U8Downloader\`。
  散着拼路径的后果是数据被劈成两半：便携版在程序目录、回退时又在用户目录，
  用户拷走文件夹却发现任务没跟过去。新增任何需要落盘的东西，都在 `AppPaths` 里加一个属性。
  数据目录里只有 `m3u8.db` 与 `logs\` 是程序在用的；如果还看到
  `settings.json` / `tasks.json` / `downloads.db`，那是旧版本遗留，程序不读。
- **广告识别宁可不跳也不能误删正片**。判断规则的门槛设得保守，
  任何一条不满足就整条放弃；新增规则时同样要留"反例"自检。
- **做了对用户有价值的事就要显式说出来**。比如过滤掉广告后，任务卡片与完成弹窗
  都会报数量 —— 用户察觉不到"少了几十秒"，不声不响地做等于没做。
- **代码排版是 K&R：大括号跟在同一行**（类 / 方法 / 属性 / 控制流 / 初始化器都一样）。
  维护者是 Java 出身，这是他明确要求的风格，由根目录 `.editorconfig` 强制 ——
  **别按 C# 生态惯例"顺手"改回 Allman（大括号另起一行）**。
  只影响排版，编译器不在乎；改完跑一遍自检即可。要批量重排用
  `dotnet format whitespace`（只做空白/换行，不动语义）。
  注意：**命名仍按 .NET 惯例**（方法 PascalCase、接口 `I` 前缀、私有字段 `_camelCase`），
  因为 XAML 绑定和框架 API 都是这套 —— 别把它一起"Java 化"了。

## 文档分工

| 文件 | 面向 | 内容 |
|---|---|---|
| `README.md` | 用户 | 怎么用、支持哪些站点、代理策略 |
| `docs/csharp-for-java-devs.md` | 维护者（Java 出身） | 这份 C# 代码怎么读：语法逐条对照 Java、容易踩的差异 |
| `docs/internal-design.md` | 维护者 | 分层、数据流、一条流水线两种模式 |
| `docs/pitfalls.md` | 改代码的人 | 踩过的坑，**动手前先看** |
| `AGENTS.md` | AI agent | 本文件：约定与发版流程 |

改动仓库结构或存储方式时，`docs/internal-design.md` 的目录树与「存储分工」两处要一起更新 ——
它们是最容易过期的部分。
