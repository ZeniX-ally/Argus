namespace FctAggregator;

/// <summary>一条失败测试步骤的明细。</summary>
public class FailedTest
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Hilim { get; set; } = "";
    public string Lolim { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Rule { get; set; } = "";
}

/// <summary>一条测试记录。</summary>
public class TestRecord
{
    public string StationId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Category { get; set; } = "";      // Online / Offline
    public string TestDate { get; set; } = "";      // 目录日期 yyyyMMdd
    public string? Sn { get; set; }
    public string Result { get; set; } = "";        // PASS/FAIL/INTERRUPTED/INVALID
    public string XmlPath { get; set; } = "";
    public string? FailReason { get; set; }
    public string? Tester { get; set; }
    public string? PanelStatus { get; set; }
    /// <summary>治具/机台位 ID（v3.22.0 规格01 fixture 归因：BATCH XML FACTORY@FIXTURE_ID；老格式缺省为 null，走 fail_reason 前缀回退）。</summary>
    public string? FixtureId { get; set; }
    public string? BatchTimestamp { get; set; }
    public bool HasFailItems { get; set; }
    public List<FailedTest> FailedTests { get; set; } = new();
    public long? FileSize { get; set; }
}
