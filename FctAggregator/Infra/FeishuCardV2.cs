namespace FctAggregator;

public static class FeishuCardV2
{
    public static object Root(string headerTitle, string template, List<object> elements, string? subtitle = null, string? bannerImgKey = null)
    {
        var header = new Dictionary<string, object>
        {
            ["template"] = template,
            ["title"] = new { tag = "plain_text", content = headerTitle },
        };
        if (!string.IsNullOrEmpty(subtitle))
            header["subtitle"] = new { tag = "plain_text", content = subtitle };

        var bodyEls = elements;
        var banner = BannerImg(bannerImgKey);
        if (banner != null)
        {
            bodyEls = new List<object>(elements.Count + 1) { banner };
            bodyEls.AddRange(elements);
            header["padding"] = "12px 16px 0 16px";
        }

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
                elements = bodyEls,
            },
        };
    }

    public static object? BannerImg(string? imgKey)
    {
        if (string.IsNullOrWhiteSpace(imgKey)) return null;
        return new
        {
            tag = "img",
            img_key = imgKey.Trim(),
            alt = new { tag = "plain_text", content = "" },
            margin = "-12px -16px 0 -16px",
        };
    }

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

    public static object Md(string content, bool heading = false) => new
    {
        tag = "markdown",
        content,
        text_size = heading ? "heading" : "normal",
    };

    public static object Hr() => new { tag = "hr" };

    public static object Note(string text) => new
    {
        tag = "div",
        text = new { tag = "plain_text", content = text, text_color = "grey", text_size = "normal" },
    };

    public static string Escape(string? s)
    {
        s ??= "";
        return s.Replace("*", "\\*").Replace("|", "\\|")
                .Replace("[", "\\[").Replace("]", "\\]").Replace("`", "\\`");
    }
}