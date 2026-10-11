using Enterprise.Gpt.Common.Constants;
using Enterprise.Gpt.Service.Observability;
using Microsoft.Extensions.Diagnostics.Metrics.Testing;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.Service.Observability;

/// <summary>
/// Covers the spreadsheet instruments, including the dimensions they must never carry.
/// </summary>
/// <remarks>
/// The instruments are process-global, so every assertion looks for a measurement carrying the values
/// under test rather than for an exact count: another test recording to the same instrument at the same
/// moment is normal.
/// </remarks>
public sealed class ChatMetricsTests
{
    private static readonly string[] IngestionTagKeys = ["document.type", "sheet.outcome"];
    private static readonly string[] QueryTagKeys = ["sheet_query.operation", "sheet_query.outcome"];

    [Fact]
    public void RecordSheetIngestion_ASuccessfulRead_RecordsItsShapeAndItsCost()
    {
        using var duration = Collect<double>("enterprise_gpt.sheet_ingestion.duration");
        using var sheets = Collect<int>("enterprise_gpt.sheet_ingestion.sheets");
        using var rows = Collect<int>("enterprise_gpt.sheet_ingestion.rows");
        using var columns = Collect<int>("enterprise_gpt.sheet_ingestion.columns");

        ChatMetrics.RecordSheetIngestion(
            "xlsx", outcome: "success", sheetCount: 6, rowCount: 4213, columnCount: 41,
            elapsed: TimeSpan.FromSeconds(2.5));

        Assert.Contains(duration.GetMeasurementSnapshot(), m => m.Value == 2.5 && HasTags(m, "xlsx", "success"));
        Assert.Contains(sheets.GetMeasurementSnapshot(), m => m.Value == 6 && HasTags(m, "xlsx", "success"));
        Assert.Contains(rows.GetMeasurementSnapshot(), m => m.Value == 4213 && HasTags(m, "xlsx", "success"));
        Assert.Contains(columns.GetMeasurementSnapshot(), m => m.Value == 41 && HasTags(m, "xlsx", "success"));
    }

    [Fact]
    public void RecordSheetQuery_WhenCalled_TagsTheOperationAndTheOutcome()
    {
        using var duration = Collect<double>("enterprise_gpt.sheet_query.duration");

        ChatMetrics.RecordSheetQuery("sum", "success", TimeSpan.FromSeconds(1.25));

        Assert.Contains(
            duration.GetMeasurementSnapshot(),
            m => m.Value == 1.25
                && m.Tags["sheet_query.operation"] as string == "sum"
                && m.Tags["sheet_query.outcome"] as string == "success");
    }

    /// <summary>
    /// The dimension set is closed, so a later call site cannot add one. What is recorded into those two
    /// keys is the call sites' own business, and their tests assert it.
    /// </summary>
    [Fact]
    public void SheetInstruments_WhateverIsRecorded_CarryNoDimensionBeyondTheTwoDeclared()
    {
        using var ingestion = Collect<double>("enterprise_gpt.sheet_ingestion.duration");
        using var query = Collect<double>("enterprise_gpt.sheet_query.duration");

        ChatMetrics.RecordSheetIngestion(
            "xlsx", outcome: "success", sheetCount: 1, rowCount: 1, columnCount: 1, elapsed: TimeSpan.Zero);
        ChatMetrics.RecordSheetQuery("sum", outcome: "success", TimeSpan.Zero);

        Assert.All(
            ingestion.GetMeasurementSnapshot(),
            m => Assert.Equal(IngestionTagKeys, m.Tags.Keys.Order(StringComparer.Ordinal)));
        Assert.All(
            query.GetMeasurementSnapshot(),
            m => Assert.Equal(QueryTagKeys, m.Tags.Keys.Order(StringComparer.Ordinal)));
    }

    private static MetricCollector<T> Collect<T>(string instrument) where T : struct =>
        new(meterScope: null, TelemetryNames.ChatSource, instrument);

    private static bool HasTags<T>(CollectedMeasurement<T> measurement, string documentType, string outcome)
        where T : struct =>
        measurement.Tags["document.type"] as string == documentType
            && measurement.Tags["sheet.outcome"] as string == outcome;
}
