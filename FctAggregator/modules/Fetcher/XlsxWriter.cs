using System.Text;
using Cell = FctShared.Xlsx.Cell;
using Sheet = FctShared.Xlsx.Sheet;

namespace FctFetcher;

public static class XlsxWriter
{
    public const int S_NORMAL = 0;
    public const int S_HEADER = 1;
    public const int S_TEXT_C = 2;
    public const int S_NUM_C = 3;

    public static Cell T(string? s, int style = S_NORMAL) => FctShared.Xlsx.T(s, style);
    public static Cell N(double n, int style = S_NUM_C) => FctShared.Xlsx.N(n, style);
    public static Cell H(string s) => FctShared.Xlsx.T(s, S_HEADER);

    public static void Write(string path, List<Sheet> sheets)
    {
        foreach (var sh in sheets)
            if (sh.FreezeRows == 0 && sh.Rows.Count > 0) sh.FreezeRows = 1;
        FctShared.Xlsx.Write(path, sheets, Styles());
    }

    internal static string Styles2() => Styles();

    private static string Styles()
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>");
        sb.Append("<styleSheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\">");

        sb.Append("<fonts count=\"2\">");
        sb.Append("<font><sz val=\"11\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("<font><b/><sz val=\"11\"/><color rgb=\"FFFFFFFF\"/><name val=\"微软雅黑\"/></font>");
        sb.Append("</fonts>");

        sb.Append("<fills count=\"3\">");
        sb.Append("<fill><patternFill patternType=\"none\"/></fill>");
        sb.Append("<fill><patternFill patternType=\"gray125\"/></fill>");
        sb.Append("<fill><patternFill patternType=\"solid\"><fgColor rgb=\"FF4472C4\"/></patternFill></fill>");
        sb.Append("</fills>");

        sb.Append("<borders count=\"2\">");
        sb.Append("<border><left/><right/><top/><bottom/><diagonal/></border>");
        sb.Append("<border>");
        sb.Append("<left style=\"thin\"><color rgb=\"FFBFBFBF\"/></left>");
        sb.Append("<right style=\"thin\"><color rgb=\"FFBFBFBF\"/></right>");
        sb.Append("<top style=\"thin\"><color rgb=\"FFBFBFBF\"/></top>");
        sb.Append("<bottom style=\"thin\"><color rgb=\"FFBFBFBF\"/></bottom>");
        sb.Append("<diagonal/></border>");
        sb.Append("</borders>");

        sb.Append("<cellStyleXfs count=\"1\"><xf/></cellStyleXfs>");
        sb.Append("<cellXfs count=\"4\">");
        Xf(sb, 0, 0, 1, "left");
        Xf(sb, 1, 2, 1, "center");
        Xf(sb, 0, 0, 1, "center");
        Xf(sb, 0, 0, 1, "center");
        sb.Append("</cellXfs>");
        sb.Append("<cellStyles count=\"1\">");
        sb.Append("<cellStyle name=\"Normal\" xfId=\"0\" builtinId=\"0\"/>");
        sb.Append("</cellStyles>");
        sb.Append("</styleSheet>");
        return sb.ToString();
    }

    private static void Xf(StringBuilder sb, int font, int fill, int border, string hAlign)
    {
        sb.Append($"<xf fontId=\"{font}\" fillId=\"{fill}\" borderId=\"{border}\" ");
        sb.Append("applyFont=\"1\" applyFill=\"1\" applyBorder=\"1\" applyAlignment=\"1\">");
        sb.Append($"<alignment horizontal=\"{hAlign}\" vertical=\"center\" wrapText=\"0\"/></xf>");
    }
}
