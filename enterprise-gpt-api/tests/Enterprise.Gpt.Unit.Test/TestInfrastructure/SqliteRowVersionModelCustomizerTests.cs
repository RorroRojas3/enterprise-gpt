using Enterprise.Gpt.Common.Enums;
using Enterprise.Gpt.Entity;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Enterprise.Gpt.Unit.Test.TestInfrastructure;

/// <summary>
/// Pins the SQL Server-specific model adaptations the unit-test fixture depends on, so a new
/// provider-specific column type is proven to load on SQLite rather than assumed to.
/// </summary>
public sealed class SqliteRowVersionModelCustomizerTests : IDisposable
{
    private readonly SqliteDbContextFixture _fixture = new();

    [Fact]
    public void Model_JsonColumn_LoadsOnSqliteAsAnOrdinaryTextColumn()
    {
        var cells = _fixture.Context.Model
            .FindEntityType(typeof(ConversationDocumentSheetRow))!
            .FindProperty(nameof(ConversationDocumentSheetRow.Cells))!;

        Assert.Equal("TEXT", cells.GetColumnType());
        Assert.Equal(typeof(string), cells.ClrType);
    }

    [Fact]
    public async Task SheetRow_InsertedThroughTheFixture_RoundTripsItsCellsJsonVerbatim()
    {
        const string cells = """{"SKU":"WID-1","Région":"Est","Revenue":"1200"}""";

        var sheetId = await AddSheetWithRowAsync(cells);

        using var ctx = _fixture.CreateContext();
        var row = await ctx.ConversationDocumentSheetRows
            .AsNoTracking()
            .SingleAsync(r => r.ConversationDocumentSheetId == sheetId, TestContext.Current.CancellationToken);

        Assert.Equal(cells, row.Cells);
    }

    public void Dispose()
    {
        _fixture.Dispose();
    }

    private async Task<Guid> AddSheetWithRowAsync(string cells)
    {
        var date = DateTimeOffset.UtcNow;

        var conversation = new Conversation
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            Name = "Test Conversation",
            DateCreated = date,
            DateModified = date
        };

        var document = new ConversationDocument
        {
            Id = Guid.NewGuid(),
            UserId = KnownIds.SeedUserId,
            ConversationId = conversation.Id,
            Name = "budget.xlsx",
            Extension = ".xlsx",
            MimeType = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            Size = 1024,
            Path = "path/to/budget.xlsx",
            DateCreated = date,
            DateModified = date,
            Sheets =
            [
                new ConversationDocumentSheet
                {
                    Id = Guid.NewGuid(),
                    SheetIndex = 1,
                    SheetName = "Regional Revenue",
                    RowCount = 1,
                    ColumnCount = 3,
                    DateCreated = date,
                    DateModified = date,
                    Columns =
                    [
                        new ConversationDocumentSheetColumn
                        {
                            ColumnIndex = 0,
                            ColumnName = "SKU",
                            InferredType = SheetColumnType.Text,
                            DateCreated = date,
                            DateModified = date
                        }
                    ],
                    Rows =
                    [
                        new ConversationDocumentSheetRow
                        {
                            RowIndex = 0,
                            Cells = cells,
                            DateCreated = date,
                            DateModified = date
                        }
                    ]
                }
            ]
        };

        using var ctx = _fixture.CreateContext();
        ctx.Conversations.Add(conversation);
        ctx.ConversationDocuments.Add(document);
        await ctx.SaveChangesAsync(TestContext.Current.CancellationToken);

        return document.Sheets[0].Id;
    }
}
