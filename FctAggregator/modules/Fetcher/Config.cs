using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace FctFetcher;

public sealed class Config
{
    [JsonPropertyName("results_root")]
    public string ResultsRoot { get; set; } = @"D:\Results";

    [JsonPropertyName("tdms_root")]
    public string TdmsRoot { get; set; } = @"D:\TDMS Log";

    [JsonPropertyName("output_dir")]
    public string OutputDir { get; set; } = "";

    [JsonPropertyName("pack_files")]
    public bool PackFiles { get; set; } = true;

    [JsonPropertyName("keep_stage_dir")]
    public bool KeepStageDir { get; set; }

    [JsonPropertyName("tdms_fallback_global")]
    public bool TdmsFallbackGlobal { get; set; } = true;

    [JsonPropertyName("categories")]
    public string[] Categories { get; set; } = new[] { "Offline" };

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
            foreach (var kv in mine) root[kv.Key] = kv.Value?.DeepClone();
        }

        var tmp = path + ".tmp";
        File.WriteAllText(tmp, root.ToJsonString(Opts));
        File.Move(tmp, path, overwrite: true);
    }

    public string ResolveOutputDir(string exeDir)
        => string.IsNullOrWhiteSpace(OutputDir) ? Path.Combine(exeDir, "out") : OutputDir;
}
