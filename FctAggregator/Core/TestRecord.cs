namespace FctAggregator;

public class FailedTest
{
    public string Name { get; set; } = "";
    public string Value { get; set; } = "";
    public string Hilim { get; set; } = "";
    public string Lolim { get; set; } = "";
    public string Unit { get; set; } = "";
    public string Rule { get; set; } = "";
}

public class MeasurementRow
{
    public string TestName { get; set; } = "";
    public double? Value { get; set; }
    public string? ValueText { get; set; }
    public double? Lolim { get; set; }
    public double? Hilim { get; set; }
    public string? Unit { get; set; }
    public string? Rule { get; set; }
}

/// <summary>test_records 入库结果：新插 / 覆盖更新 / 是否需补推 FAIL 告警。</summary>
public readonly struct IngestResult
{
    public long RecordId { get; init; }
    public bool IsNew { get; init; }
    public bool WasUpdated { get; init; }
    public string? PreviousResult { get; init; }
    public bool NeedsFailAlert { get; init; }
}

public class TestRecord
{
    public string StationId { get; set; } = "";
    public string Model { get; set; } = "";
    public string Category { get; set; } = "";
    public string TestDate { get; set; } = "";
    public string? Sn { get; set; }
    public string Result { get; set; } = "";
    public string XmlPath { get; set; } = "";
    public string? FailReason { get; set; }
    public string? Tester { get; set; }
    public string? PanelStatus { get; set; }
    public string? FixtureId { get; set; }
    public string? BatchTimestamp { get; set; }
    public bool HasFailItems { get; set; }
    public List<FailedTest> FailedTests { get; set; } = new();
    public List<MeasurementRow> Measurements { get; set; } = new();
    public long? FileSize { get; set; }
    /// <summary>本次解析是否看过总时长。没有 TOTALTIME 时 CycleSeconds 为 null，仍然记一笔，避免反复补读。</summary>
    public bool CycleChecked { get; set; }
    public double? CycleSeconds { get; set; }
}
