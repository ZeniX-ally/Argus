using System.Text.Json;
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

    public void Save(string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(this, Opts));

    public string ResolveOutputDir(string exeDir)
        => string.IsNullOrWhiteSpace(OutputDir) ? Path.Combine(exeDir, "out") : OutputDir;
}
