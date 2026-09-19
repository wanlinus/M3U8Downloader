# 给 Java 出身的维护者：这份 C# 代码怎么读

本项目的维护者是从 Java 过来的，所以这份文档不按"微软教材"的顺序讲，
而是**按你会撞上的顺序**：先说你一眼就看出来的差异，再说 Java 里根本没有、
因此最容易被误解的那些语法，最后是"拿 Java 习惯套过来会出错"的几条。

代码排版已经调成 Java 习惯（大括号跟在同一行，见根目录 `.editorconfig`），
所以下面只谈**语义**差异 —— 那些不是排版能解决的。

---

## 一、命名：一眼能对上的部分

| C# 写法 | Java 对应 | 备注 |
|---|---|---|
| `namespace M3U8Downloader.Core.Storage;` | `package ...;` | 几乎一样，用 `.` 分层 |
| `class SqliteDatabase` | `class SqliteDatabase` | 类名都是 PascalCase |
| `void Record(...)` / `Task LoadAsync()` | `record(...)` / `loadAsync()` | **方法名 PascalCase**，这是最扎眼的一条 |
| `private readonly SqliteDatabase _database;` | `private final ... database;` | 私有字段约定 `_camelCase` |
| `IDownloadHistoryStore` | `DownloadHistoryStore` | **接口带 `I` 前缀**（.NET 生态统一） |
| `public const int SchemaVersion = 1;` | `static final int SCHEMA_VERSION` | 常量也是 PascalCase |
| `AppPaths.DatabaseFile` | `AppPaths.DATABASE_FILE` | 静态属性当常量用 |

方法名和常量的写法跟 Java 是反的，这不是笔误 —— .NET 框架 API 全这样
（`list.Count`、`File.Exists`、`string.IsNullOrEmpty`），跟着写才不会一半一半。

---

## 二、属性：Java 里没有的东西

你看到 `public string FilePath { get; }` 时，**脑内翻译成 `getFilePath()` 就行**：

```csharp
// 只读属性 ≈ Java 的 getter
public string FilePath => _database.FilePath;

// 读写属性 ≈ getter + setter
public string? FfmpegPath { get; set; }

// 计算属性 ≈ Java 里手写的 isXxx()
public bool IsEmpty => _items.Count == 0;
```

用的时候不带括号：

```csharp
var path = task.FilePath;      // Java: task.getFilePath()
task.Message = "已暂停";        // Java: task.setMessage("已暂停")
```

**为什么这里非用属性不可**（不是图省事）：

1. XAML 界面绑定的是属性名 —— `{Binding StatusText}` 是字符串，改属性名就得改 `.xaml`；
2. `INotifyPropertyChanged` 靠 setter 触发界面刷新，写成 `setXxx()` 反而接不上。

更"Java 味"的写法是这样（项目里到处都是）：

```csharp
public sealed class TaskEpisodeItem : INotifyPropertyChanged {
    private double _percent;
    public double Percent {
        get => _percent;
        set { if (SetProperty(ref _percent, value)) OnPropertyChanged(); }
    }
}
```

---

## 三、`record` + `required` + `init`：最接近 Java 的一处

```csharp
public sealed record DownloadHistoryEntry {
    public required string PageUrl { get; init; }
    public required int EpisodeNumber { get; init; }
    public string? FilePath { get; init; }
    public long FileBytes { get; init; }
}
```

对照 Java 16 的 `record`：

- `record` —— 一样，值语义、自带相等性；
- `required` —— **Java 没有**：不写这个属性就编译不过（编译期强制，不是运行时校验）；
- `init` —— 只能在初始化时赋值，构造完就不可变（类似 `final` 字段 + 构造器赋值）。

构造出来长这样（注意用的是**属性名**，不是位置参数）：

```csharp
var entry = new DownloadHistoryEntry {
    PageUrl = "https://...",
    EpisodeNumber = 7,
    FileBytes = 12345,
};
```

---

## 四、表达式体成员：读的时候自己补个 `return`

```csharp
public string FilePath => _database.FilePath;                  // { return _database.FilePath; }
public bool IsEmpty => _items.Count == 0;                      // { return ...; }
private void Log(string s) => Console.WriteLine(s);            // { Console.WriteLine(s); }
```

`=>` 后面是表达式就自动 return，是语句就当方法体。项目里 `=>` 出现得非常频繁，
看到它不用紧张。

---

## 五、空值处理：`string?`、`?.`、`??`

Java 靠 `@Nullable` 注解（工具检查），C# 是**编译器检查**的：

```csharp
public string? Key { get; set; }            // 允许 null（Java: @Nullable String key）
public string Title { get; set; } = "";     // 不允许 null，默认给空串
```

三个运算符：

```csharp
entry.FilePath?.Length                      // ?. 空传播：null 就直接得 null，不抛 NPE（Java 没有）
(entry.FilePath ?? "（未知）")               // ?? 空合并：左边是 null 就用右边
cmd.Parameters.AddWithValue("$path", (object?)entry.FilePath ?? DBNull.Value);
```

编译器的可空检查会**在编译期**告诉你"这里可能是 null"，
比运行期撞 NPE 强得多 —— 这是 C# 比 Java 舒服的地方之一。

---

## 六、`async` / `await`：像同步代码的 `CompletableFuture`

```csharp
public async Task<SeriesTask?> RetryFailedAsync(SeriesTask task) {
    var preview = await _store.LoadAsync();     // 等它，但不阻塞线程
    return Build(preview);
}
```

| C# | Java |
|---|---|
| `Task<T>` | `CompletableFuture<T>` |
| `Task` | `CompletableFuture<Void>` |
| `await x` | `x.thenApply(...)`，但写起来像同步 |
| `async` 方法返回 `Task` | 方法返回 `CompletableFuture` |

两个坑：

- **`async void` 很危险**（异常没人接），本项目只在 WinUI 的事件处理器里用；
- `await` 之后的代码**不保证在原来的线程上**（跟 WinUI 打交道时要留意，
  本项目因此有 `RunOnUi(...)` 这套封送）。

---

## 七、`using` = try-with-resources

```csharp
using var connection = _database.Open();     // 方法结束时自动 Dispose
using (var connection = Open()) { ... }      // 块结束时
```

一样的东西，只是不用写括号里的变量声明形式。

---

## 八、LINQ ≈ Stream API

```csharp
// Java: list.stream().filter(e -> ...).sorted(...).collect(toList())
records.Where(e => e.PageUrl == pageUrl).OrderBy(e => e.EpisodeNumber).ToList()

// Java: list.stream().map(this::toRecord).collect(toList())
Tasks.Select(ToRecord).ToList()

// Java: list.stream().anyMatch(...)
histEntries.All(e => e.FileBytes > 0)

// Java: list.stream().count()
task.Episodes.Count(e => e.Status == "Completed")
```

对照：`Where`=filter、`Select`=map、`OrderBy`=sorted、`Any`=anyMatch、`All`=allMatch、
`FirstOrDefault`=findFirst、`Single`=取唯一元素（不是唯一就抛异常）。

**惰性求值和 Java 一样**：`Where/Select` 不立刻执行，`ToList()` / `foreach` 才求值。

---

## 九、字符串

```csharp
$"共 {count} 条（{percent:F1}%）"        // Java: String.format("共 %d 条", count)
"""                                       // Java 15 的 text block，几乎一模一样
SELECT * FROM t WHERE id = $id;
"""
@"D:\视频"                                // 逐字字符串：反斜杠不用转义（Java 没有）
$@"{root}\logs"                          // 插值 + 逐字
```

**最要注意的一条**（下面第十五节还会再说）：C# 里 `==` 比字符串是**值比较**：

```csharp
if (status == "Completed") { }        // 正确，就是内容比较
if (status == "Completed") { }        // Java 里这是引用比较，必须用 equals
```

方向是反的 —— Java 出身的人容易以为"得写 equals"，结果写 `string.Equals(...)` 反而啰嗦。

---

## 十、模式匹配：Java 21 的 record pattern，但按属性名配

```csharp
if (previewDone is { DownloadedCount: 4, PendingCount: 0 }) { ... }
if (qT1.Episodes[1] is { Number: 2, Percent: 42.5, Error: "连接超时" }) { ... }
if (degraded is { Refreshed: false }) { ... }
```

读法：**"它非 null，而且这些属性的值分别是……"**。等价于 Java 里

```java
if (previewDone != null && previewDone.downloadedCount() == 4 && ...) { }
```

`is not null` = `!= null`。`is { }` = 非 null 并顺手取出来：

```csharp
if (TaskOf(sender) is { } task) _taskManager.Cancel(task);   // 变量 task 直接可用
```

---

## 十一、委托、lambda、事件

| C# | Java |
|---|---|
| `Action<string>` | `Consumer<String>` |
| `Func<Task<bool>>` | `Supplier<CompletableFuture<Boolean>>` |
| `Func<A, B>` | `Function<A, B>` |
| `Predicate<T>` | `Predicate<T>` |
| `event EventHandler? Changed` | listener 列表，但用 `+=` 注册、`-=` 注销 |

```csharp
task.PropertyChanged += (_, e) => { ... };        // 注册
_runOnUi(() => RaiseChanged());                   // 传一个方法当参数
```

`_` 是"这个参数我不关心"的占位名。

---

## 十二、泛型：C# 不擦除

```csharp
List<string>      // 运行时真的知道元素是 string（Java 是类型擦除）
List<int>         // 不装箱（Java 的 List<Integer> 会）
```

实际影响：C# 里不用为 `int` 写 `IntStream` 之类的东西，`List<int>` 直接就是高效的。
其余用法（约束、通配符）比 Java 简单：C# 用 `where T : class`，Java 用 `<? extends T>`。

---

## 十三、集合与初始化器

```csharp
new List<string>()                  // ArrayList
new Dictionary<string, int>()       // HashMap
new HashSet<string>()               // HashSet
new[] { 1, 2, 3 }                   // 数组
Array.Empty<string>()               // 空数组（复用同一个实例，别 new string[0]）
```

集合/对象初始化器（Java 没有这种语法）：

```csharp
new Dictionary<string, string> {
    ["Referer"] = "https://example.com/",     // 索引器初始化
    ["User-Agent"] = "UA/1.0",
}

new SeriesTaskRecord {
    Id = "t1",
    Episodes = {
        new TaskEpisodeRecord { Number = 1 },  // 直接往已有集合里 Add
    },
}
```

---

## 十四、把这套代码的骨架串一遍

```
src/M3U8Downloader.Core/    业务全部在这里（纯逻辑 + 存储），三个项目共用
  ├── Sites/                站点适配（加新站点 = 加一个 ISiteAdapter 实现，反射自动登记）
  ├── Core/                 下载引擎（m3u8 解析、分片下载、广告识别）
  ├── Downloads/            一集/一部剧的流水线
  ├── Tasks/                任务队列、断点续传、磁盘优先判定
  ├── Storage/              ★统一数据库（设置 / 任务 / 历史）
  └── Settings/ Ffmpeg/ Net/ Staging/ Update/   支撑
src/M3U8Downloader.App/     只有界面，不写业务
tests/M3U8Downloader.SelfTest/  自检：唯一的回归防线，改完 Core 必须跑
```

一条数据流（站点下载）大致是：

```
页面 URL → SiteResolver 认出站点 → 适配器解析出剧集列表
        → DownloadTaskManager 入队 → SeriesDownloader 逐集
        → EpisodePipeline（解析清单 → 下载分片 → 转 MP4 → 校验）
        → 写入统一库（进度 / 下载历史）
```

---

## 十五、拿 Java 习惯套过来会出错的几条

1. **字符串 `==` 是值比较**（Java 要 `equals`）—— 反过来的，别写多余的方法调用；
2. **集合大小是属性**：`list.Count`（不是 `size()`）、`array.Length`（不是 `length`）；
3. **异常没有 checked**：方法签名里不用 `throws`，也没有 `throws` 关键字；
4. **`foreach` 里改集合会抛 `InvalidOperationException`**（相当于 Java 的
   `ConcurrentModificationException`），要改先 `ToList()` 快照一份；
5. **`struct` 是值类型** —— 本项目基本没用，但看到别当类；
6. **属性/方法可以"扩展"**：`WindowExtensions.Hide(...)` 这种扩展方法，
   调用时看起来像实例方法，实际是静态类 —— 相当于 Java 里没法做、只能写工具类的东西；
7. **`??=`、`?.`、`??`** 这些运算符 Java 完全没有，看到不用猜，就是判空简写；
8. **`nameof(X)`** 取标识符的字符串（重构友好），Java 没有；
9. **局部函数**：方法体里可以再定义方法（`static void Helper(...)` 在 top-level 语句里
   很常见，比如自检 `Program.cs`），不是 lambda；
10. **`partial class`**：一个类拆在多个文件里（XAML 界面就是 `.xaml` + `.xaml.cs`），
    Java 没有对应概念。

---

## 十六、想更深入时看哪儿

- `AGENTS.md` —— 项目约定与发版流程（改代码前必看）；
- `docs/pitfalls.md` —— 踩过的坑，很多"看起来显然"的写法其实是错的；
- `docs/internal-design.md` —— 分层、数据流、扩展点；
- 自检 `tests/M3U8Downloader.SelfTest/Program.cs` —— 想知道某个功能到底怎么用，
  照着自检里的调用抄最快（它本身就是一份可执行的用法说明）。
