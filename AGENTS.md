# AGENTS.md

给在本仓库工作的 AI agent（以及偶尔上手的维护者）看的约定。
**改代码前先扫一眼 `docs/pitfalls.md`** —— 那里记着踩过的坑，很多看起来"显然"的写法其实是错的。

## 这个项目是什么

WinUI 3 的 M3U8 下载器。四个项目，**严格单向依赖**：

```
M3U8Downloader.Core              纯 BCL，不引任何第三方包
  ├── M3U8Downloader.App         WinUI 3 界面
  ├── M3U8Downloader.Cli         命令行
  └── tests/M3U8Downloader.SelfTest   自检（无界面、不依赖外网）
```

**改 Core 时不要引入外部依赖** —— 另外三个都靠它。

## 常用命令

```powershell
# 编译界面
dotnet build src\M3U8Downloader.App\M3U8Downloader.App.csproj -c Debug -p:Platform=x64 -r win-x64

# 自检：唯一的回归防线，改完 Core 必须跑；退出码 0 = 全过
dotnet run --project tests\M3U8Downloader.SelfTest -c Debug

# 自包含发布
.\scripts\publish.ps1 [-Version x.y.z] [-SkipCli]
```

- **发布前先杀掉正在运行的 `M3U8Downloader.exe`**，否则 `publish/app-win-x64` 被占用、publish 失败。
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
- 修 bug 的提交，同时往 `docs/pitfalls.md` 补一条。

## 几条硬约定（都是踩过坑换来的）

- **要用直连就必须显式 `UseProxy = false`**。`SocketsHttpHandler.UseProxy` 默认是 `true`，
  不显式关掉就会退到系统代理（用户的 Clash）—— 白耗流量，还会被站点按代理 IP 拒绝。
- **绑定属性只能在 UI 线程改**。后台线程改 WinUI 绑定属性**不报错但界面不刷新**，
  现象是任务永远停在"下载中"。
- **后台线程不要遍历界面绑定的 `ObservableCollection`**，先在 UI 线程快照一份。
- **站点适配是模块化的**：新增站点 = 在 `Core/Sites/` 加一个 `ISiteAdapter` 实现，
  反射会自动登记（按 `Priority` 排序）。**不要改注册代码**。
- **广告识别宁可不跳也不能误删正片**。判断规则的门槛设得保守，
  任何一条不满足就整条放弃；新增规则时同样要留"反例"自检。
- **做了对用户有价值的事就要显式说出来**。比如过滤掉广告后，任务卡片与完成弹窗
  都会报数量 —— 用户察觉不到"少了几十秒"，不声不响地做等于没做。

## 文档分工

| 文件 | 面向 | 内容 |
|---|---|---|
| `README.md` | 用户 | 怎么用、支持哪些站点、代理策略 |
| `docs/internal-design.md` | 维护者 | 分层、数据流、一条流水线两种模式 |
| `docs/pitfalls.md` | 改代码的人 | 踩过的坑，**动手前先看** |
| `AGENTS.md` | AI agent | 本文件：约定与发版流程 |
