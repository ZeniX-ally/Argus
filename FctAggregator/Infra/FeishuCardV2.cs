namespace FctAggregator;

/// <summary>
/// 飞书交互卡片 JSON 2.0 构建原语（schema:"2.0"）。
///
/// 与旧 1.0 差异（见开放平台「卡片 JSON 2.0 版本更新说明」）：
///   1. elements 移入 body 层级；根必须显式声明 schema="2.0"，否则当 1.0 处理。
///   2. 两列/多列不再用 div.fields，改用 column_set（旧 fields 属性在 2.0 报「不支持属性」错误）。
///   3. 分割线用 tag=hr；富文本用 tag=markdown（text_size 支持 normal/heading）。
///   4. header.template 是白名单枚举，非法值（如 amber）在 2.0 直接报错，必须换成合法色（yellow/red…）。
/// </summary>
public static class FeishuCardV2
{
    /// <summary>卡片根：schema 2.0 + 填满聊天窗宽度 + body 纵向布局。</summary>
    public static object Root(string headerTitle, string template, List<object> elements, string? subtitle = null)
    {
        var header = new Dictionary<string, object>
        {
            ["template"] = template,
            ["title"] = new { tag = "plain_text", content = headerTitle },
        };
        if (!string.IsNullOrEmpty(subtitle))
            header["subtitle"] = new { tag = "plain_text", content = subtitle };

        return new
        {
            schema = "2.0",
            config = new { width_mode = "fill" },
            header,
            body = new
            {
                direction = "vertical",
                padding = "12px 16px",
                vertical_spacing = "small",
                elements,
            },
        };
    }

    /// <summary>对称多列字段（column_set），每列一个「粗体标签 + 值」markdown，等宽均分。</summary>
    public static object FieldRow(params (string label, string value)[] fields)
    {
        var columns = fields.Select(f => (object)new
        {
            tag = "column",
            width = "weighted",
            weight = 1,
            vertical_align = "center",
            elements = new object[] { Md($"**{f.label}**\n{Escape(f.value)}") },
        }).ToArray();

        return new { tag = "column_set", horizontal_spacing = "medium", columns };
    }

    /// <summary>富文本 markdown 块；heading=true 放大字号当作段落标题。</summary>
    public static object Md(string content, bool heading = false) => new
    {
        tag = "markdown",
        content,
        text_size = heading ? "heading" : "normal",
    };

    /// <summary>分割线（区分信息分区）。</summary>
    public static object Hr() => new { tag = "hr" };

    /// <summary>
    /// 页脚备注。2.0 已废弃 tag=note（实测 schema 2.0 校验报 unsupported tag note），
    /// 改用 div + 灰色小号 plain_text 实现同等「底部弱化信息」效果。
    /// </summary>
    public static object Note(string text) => new
    {
        tag = "div",
        text = new { tag = "plain_text", content = text, text_color = "grey", text_size = "normal" },
    };

    /// <summary>lark_md 转义：粗体/竖线/链接方括号与反引号（防恶意测试项名渲染成可点击链接）。</summary>
    public static string Escape(string? s)
    {
        s ??= "";
        return s.Replace("*", "\\*").Replace("|", "\\|")
                .Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
    }
}