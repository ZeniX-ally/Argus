using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FctFetcher;

/// <summary>
/// 捞取器配置。与 fct-aggregator / fct-distributor 的 config.json 风格保持一致。
/// </summary>
public sealed class Config
{
    [JsonPropertyName("results_root")]
    public string ResultsRoot { get; set; } = @"D:\Results";

    /// <summary>TDMS 根目录。其下层级与 Results 相同: {Online|Offline}\{型号}\{yyyyMMdd}\</summary>
    [JsonPropertyName("tdms_root")]
    public string TdmsRoot { get; set; } = @"D:\TDMS Log";

    [JsonPropertyName("output_dir")]
    public string OutputDir { get; set; } = "";

    /// <summary>把命中文件按类型分入 xml/csv/tdms 三个文件夹并打包成 zip</summary>
    [JsonPropertyName("pack_files")]
    public bool PackFiles { get; set; } = true;

    /// <summary>打包后是否保留未压缩的中间目录</summary>
    [JsonPropertyName("keep_stage_dir")]
    public bool KeepStageDir { get; set; }

    /// <summary>镜像目录找不到 TDMS 时，是否回退到整个 TdmsRoot 按 SN 搜索</summary>
    [JsonPropertyName("tdms_fallback_global")]
    public bool TdmsFallbackGlobal { get; set; } = true;

    /// <summary>
    /// 要扇描的分类。生产环境 Online 全是 pass，所以默认只扇 Offline。
    /// </summary>
    [JsonPropertyName("categories")]
    public string[] Categories { get; set; } = new[] { "Offline" };

    /// <summary>
    /// 排除非产品真实不良的步骤（Get Unit Information / UUT Status Err）。
    /// 关掉后这类步骤也会算作 fail。
    /// </summary>
    [JsonPropertyName("exclude_ignored_steps")]
    public bool ExcludeIgnoredSteps { get; set; } = true;

    [JsonPropertyName("skip_debug")]
    public bool SkipDebug { get; set; } = true;

    private static readonly JsonSerializerOptions Opts = new()
    {
        WriteIndented = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public static Config Load(string path)
    {
        if (!File.Exists(path)) return new Config();
        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<Config>(json, Opts) ?? new Config();
        }
        catch (Exception e)
        {
            Console.Error.WriteLine($"[警告] 配置读取失败, 使用默认值: {e.Message}");
            return new Config();
        }
    }

    /// <summary>
    /// 保存配置。
    ///
    /// ⚠⚠ 必须**合并**写入，不能整体覆盖 ⚠⚠
    /// v2.9.0 把五个程序合并成单 exe 后，取数打包与主程序**同在一个 exe 目录、共用
    /// 同一个 config.json**。此前这里是 <c>File.WriteAllText(path, Serialize(this))</c>，
    /// 于是【取数打包】页每点一次「运行」，就把主程序的字段整体抹掉：
    ///   · webhook_url 丢 -> 飞书推送**静默失效**（无兜底）
    ///   · fct_ini_path / desktop_notify / todo_scan_days 等回默认值
    ///   · station_id 丢 -> 库名由 {station_id}.db 退回 fct.db（见 Engine.cs 第 34 行），
    ///     IP 识别机台号又恰好失败时，现场表现为「历史数据全部消失」
    /// 现在改为：读出原 JSON，只覆盖本类拥有的键，其余键原样保留。
    ///
    /// 读取用与 Load 相同的容错（跳注释、容忍尾逗号）；若原文件连容错解析都失败
    /// （或顶层不是 JSON 对象），**直接抛异常中止，绝不写盘、绝不全量覆盖**，
    /// 由调用方向用户报告。写入采用原子替换：先写同目录 .tmp 再 File.Move 覆盖，
    /// 防止写一半断电损坏配置。
    /// </summary>
    public void Save(string path)
    {
        JsonObject root;
        if (File.Exists(path))
        {
            var docOpts = new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            };
            root = JsonNode.Parse(File.ReadAllText(path), null, docOpts) as JsonObject
                ?? throw new InvalidDataException(
                    "config.json 不是有效的 JSON 对象, 已中止保存(未写入磁盘)");
        }
        else
        {
            root = new();
        }

        if (JsonSerializer.SerializeToNode(this, Opts) is JsonObject mine)
        {
            // DeepClone 必需：JsonNode 不允许一个节点同时挂在两个父对象下
            foreach (var kv in mine) root[kv.Key] = kv.Value?.DeepClone();
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Opts));
        File.Move(tmp, path, overwrite: true);
    }

    public string ResolveOutputDir(string exeDir)
        => string.IsNullOrWhiteSpace(OutputDir) ? Path.Combine(exeDir, "out") : OutputDir;
}
