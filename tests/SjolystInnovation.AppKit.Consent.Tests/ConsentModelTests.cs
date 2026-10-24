using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace SjolystInnovation.AppKit.Consent.Tests;

public sealed class ConsentModelTests : IDisposable
{
    private readonly SqliteConnection _connection = new("DataSource=:memory:");
    private readonly TestDbContext _db;

    public ConsentModelTests()
    {
        _connection.Open();
        _db = new TestDbContext(new DbContextOptionsBuilder<TestDbContext>().UseSqlite(_connection).Options);
    }

    public void Dispose()
    {
        _db.Dispose();
        _connection.Dispose();
    }

    private IEntityType Entity => _db.Model.FindEntityType(typeof(ConsentRecord))!;

    [Fact]
    public void The_table_has_exactly_the_documented_columns()
    {
        Assert.Equal("ConsentRecords", Entity.GetTableName());
        Assert.Equal(
            ["ConsentId", "DecidedAt", "Id", "Marketing", "NoticeVersion", "SchemaVersion", "Source", "UserId"],
            Entity.GetProperties().Select(p => p.GetColumnName()).Order());
    }

    [Fact]
    public void There_is_no_column_for_an_ip_address_or_a_user_agent()
    {
        var columns = Entity.GetProperties().Select(p => p.GetColumnName().ToLowerInvariant()).ToList();

        Assert.DoesNotContain(columns, c => c.Contains("ip", StringComparison.Ordinal) || c.Contains("agent", StringComparison.Ordinal)
            || c.Contains("address", StringComparison.Ordinal) || c.Contains("host", StringComparison.Ordinal));
        Assert.DoesNotContain(typeof(ConsentRecord).GetProperties(), p => p.Name.Contains("Ip", StringComparison.Ordinal) || p.Name.Contains("Agent", StringComparison.Ordinal));
    }

    [Fact]
    public void The_columns_have_their_lengths_and_nullability()
    {
        Assert.Equal(64, Entity.FindProperty(nameof(ConsentRecord.NoticeVersion))!.GetMaxLength());
        Assert.Equal(32, Entity.FindProperty(nameof(ConsentRecord.Source))!.GetMaxLength());
        Assert.Equal(450, Entity.FindProperty(nameof(ConsentRecord.UserId))!.GetMaxLength());
        Assert.False(Entity.FindProperty(nameof(ConsentRecord.NoticeVersion))!.IsNullable);
        Assert.False(Entity.FindProperty(nameof(ConsentRecord.Source))!.IsNullable);
        Assert.True(Entity.FindProperty(nameof(ConsentRecord.UserId))!.IsNullable);
        Assert.Equal(typeof(long), Entity.FindPrimaryKey()!.Properties.Single().ClrType);
    }

    [Fact]
    public void The_consent_id_and_the_decision_time_are_indexed()
    {
        var indexes = Entity.GetIndexes().Select(i => string.Join(",", i.Properties.Select(p => p.Name))).Order().ToList();

        Assert.Equal(["ConsentId,DecidedAt", "DecidedAt"], indexes);
        Assert.All(Entity.GetIndexes(), i => Assert.False(i.IsUnique));
    }

    [Fact]
    public void The_sources_are_banner_and_settings()
    {
        Assert.Equal(["banner", "settings"], ConsentSources.All);
    }
}
