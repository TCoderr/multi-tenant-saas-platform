using Kulvar.Modules.Operations;

namespace Kulvar.UnitTests;

public sealed class CsvWriterTests
{
    [Theory]
    [InlineData("=1+1", "'=1+1")]
    [InlineData("+90 555", "'+90 555")]
    [InlineData("-2+3", "'-2+3")]
    [InlineData("@SUM(A1)", "'@SUM(A1)")]
    [InlineData("\tcmd", "'\tcmd")]
    [InlineData("\rcmd", "\"'\rcmd\"")]
    [InlineData("=HYPERLINK(\"http://x\",\"y\")", "\"'=HYPERLINK(\"\"http://x\"\",\"\"y\"\")\"")]
    public void FormulaCells_AreNeutralised(string input, string expected) => Assert.Equal(expected, CsvWriter.Escape(input));

    [Theory]
    [InlineData("düz metin", "düz metin")]
    [InlineData("", "")]
    [InlineData("a,b", "\"a,b\"")]
    [InlineData("o \"dedi\"", "\"o \"\"dedi\"\"\"")]
    [InlineData("satır\nsonu", "\"satır\nsonu\"")]
    [InlineData(" baştaki boşluk", "\" baştaki boşluk\"")]
    [InlineData("ortada = işareti", "ortada = işareti")]   // yalnız hücrenin ilk karakteri önemlidir
    [InlineData("e-posta@ornek.test", "e-posta@ornek.test")]
    public void OrdinaryCells_FollowRfc4180(string input, string expected) => Assert.Equal(expected, CsvWriter.Escape(input));

    [Fact]
    public async Task Rows_UseCrlf_InvariantNumbers_AndUtcTimestamps()
    {
        await using var writer = new StringWriter();
        var csv = new CsvWriter(writer);
        await csv.WriteRowAsync("id", "amount", "at", "empty");
        await csv.WriteRowAsync(Guid.Parse("018f0000-0000-7000-8000-000000000001"), 1234.5m, new DateTimeOffset(2026, 10, 10, 12, 30, 0, TimeSpan.FromHours(3)), null);

        Assert.Equal("id,amount,at,empty\r\n018f0000-0000-7000-8000-000000000001,1234.5,2026-10-10T09:30:00Z,\r\n", writer.ToString());
        Assert.Equal(2, csv.RowCount);
        Assert.Equal([0xEF, 0xBB, 0xBF], CsvWriter.Encoding.GetPreamble());
    }

    [Fact]
    public void ArtifactPath_IsBuiltOnlyFromServerGeneratedIds_AndStaysUnderRoot()
    {
        var root = Path.Combine(Path.GetTempPath(), "kulvar-unit-exports");
        var options = new ExportOptions { Root = root };
        var tenant = Guid.Parse("018f0000-0000-7000-8000-00000000000a");
        var job = Guid.Parse("018f0000-0000-7000-8000-00000000000b");

        var path = options.ArtifactPath(tenant, job);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), tenant.ToString("D"), job.ToString("D") + ".zip"), path);
        Assert.Equal(Path.Combine(Path.GetFullPath(root), tenant.ToString("D")), options.TenantDirectory(tenant));
        // Farklı şirket, farklı dizin: bir şirketin kimliğiyle başkasının dosyasına yol kurulamaz.
        Assert.NotEqual(options.TenantDirectory(tenant), options.TenantDirectory(job));
        Assert.Throws<InvalidOperationException>(() => new ExportOptions().ArtifactPath(tenant, job));
    }
}
