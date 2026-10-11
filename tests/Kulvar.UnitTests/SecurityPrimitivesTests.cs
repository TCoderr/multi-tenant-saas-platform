using System.Globalization;
using Kulvar.Platform.Security;
using Kulvar.Testing;

namespace Kulvar.UnitTests;

public sealed class EmailNormalizerTests
{
    [Theory]
    [InlineData("  Ayse@Example.TEST ", "ayse@example.test")]
    [InlineData("AYSE@EXAMPLE.TEST", "ayse@example.test")]
    [InlineData("ayse+etiket@example.test", "ayse+etiket@example.test")]
    [InlineData("a.y.s.e@example.test", "a.y.s.e@example.test")]
    public void Normalize_IsCaseInsensitive_WithoutFolding(string input, string expected) =>
        Assert.Equal(expected, EmailNormalizer.Normalize(input));

    [Fact]
    public void Normalize_IsCultureIndependent_TurkishI()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            // tr-TR kültüründe "I".ToLower() == "ı" olur; normalizasyon kültürden bağımsız olmalıdır.
            CultureInfo.CurrentCulture = new CultureInfo("tr-TR");
            Assert.Equal("info@example.test", EmailNormalizer.Normalize("INFO@EXAMPLE.TEST"));
            Assert.Equal(EmailNormalizer.Normalize("info@example.test"), EmailNormalizer.Normalize("INFO@EXAMPLE.TEST"));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void Normalize_AppliesNfkc()
    {
        // Tam genişlikli "Ａ" (U+FF21) NFKC ile "A" olur.
        Assert.Equal("a@example.test", EmailNormalizer.Normalize("Ａ@example.test"));
    }

    [Theory]
    [InlineData("a@b.example", true)]
    [InlineData("ayse@example.test", true)]
    [InlineData("", false)]
    [InlineData("ayse", false)]
    [InlineData("ayse@", false)]
    [InlineData("@example.test", false)]
    [InlineData("ayse@example", false)]
    [InlineData("ay se@example.test", false)]
    [InlineData("a@b@c.test", false)]
    [InlineData("ayse@example.", false)]
    public void IsPlausible_AcceptsOnlyAddressLikeInput(string input, bool expected) =>
        Assert.Equal(expected, EmailNormalizer.IsPlausible(input));

    [Fact]
    public void Mask_HidesLocalPart() => Assert.Equal("a***@example.test", EmailNormalizer.Mask("ayse@example.test"));
}

public sealed class TokenTests
{
    [Fact]
    public void NewToken_IsUrlSafe_Long_AndUnique()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => Tokens.NewToken()).ToList();
        Assert.Equal(200, tokens.Distinct().Count());
        Assert.All(tokens, t =>
        {
            Assert.True(t.Length >= 43); // 32 bayt → 43 base64url karakteri
            Assert.True(Tokens.LooksValid(t));
        });
    }

    [Fact]
    public void Hash_IsSha256_AndDeterministic()
    {
        var hash = Tokens.Hash("abc");
        Assert.Equal(32, hash.Length);
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", Tokens.HashHex("abc"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("kisa")]
    [InlineData("../../etc/passwd-................................")]
    [InlineData("token with spaces token with spaces")]
    public void LooksValid_RejectsMalformed(string? token) => Assert.False(Tokens.LooksValid(token));
}

public sealed class TestDatabaseGuardTests
{
    private sealed class SpyProbe(string host, int port, string? database, string? marker, bool throwOnRead = false) : ITestDatabaseProbe
    {
        public List<string> Reads { get; } = [];
        public List<string> Writes { get; } = [];
        public string Host => host;
        public int Port => port;

        public Task<string?> ReadScalarAsync(string sql, CancellationToken ct)
        {
            Reads.Add(sql);
            if (throwOnRead)
            {
                throw new InvalidOperationException("relation kulvar_test.run_marker does not exist");
            }
            return Task.FromResult(sql.Contains("current_database", StringComparison.Ordinal) ? database : marker);
        }

        public Task WriteAsync(string sql, CancellationToken ct)
        {
            Writes.Add(sql);
            return Task.CompletedTask;
        }
    }

    private static readonly TestDatabaseManifest Manifest = new("run123", "127.0.0.1", 55432, "kulvar_test_run123");
    private const string Destructive = "DROP SCHEMA app CASCADE";

    [Fact]
    public async Task OwnedTarget_AllowsWrites()
    {
        var probe = new SpyProbe("127.0.0.1", 55432, "kulvar_test_run123", "run123");
        var guard = new TestDatabaseGuard(probe, Manifest, TestDatabaseGuard.OptInValue);
        await guard.WriteAsync(Destructive);
        Assert.Equal([Destructive], probe.Writes);
    }

    public static TheoryData<string, string, int, string?, string?, string?> WrongTargets => new()
    {
        // ad, host, port, veritabanı, işaret, onay
        { "onay yok", "127.0.0.1", 55432, "kulvar_test_run123", "run123", null },
        { "yanlış onay", "127.0.0.1", 55432, "kulvar_test_run123", "run123", "yes" },
        { "geliştirme yığını portu", "127.0.0.1", 5432, "kulvar_test_run123", "run123", "owned" },
        { "başka host", "db.internal", 55432, "kulvar_test_run123", "run123", "owned" },
        { "başka veritabanı (önek uyuyor)", "127.0.0.1", 55432, "kulvar_test_other", "run123", "owned" },
        { "geliştirme veritabanı", "127.0.0.1", 55432, "kulvar", "run123", "owned" },
        { "işaret yok", "127.0.0.1", 55432, "kulvar_test_run123", null, "owned" },
        { "başka koşunun işareti", "127.0.0.1", 55432, "kulvar_test_run123", "run999", "owned" },
    };

    [Theory]
    [MemberData(nameof(WrongTargets))]
    public async Task WrongTarget_PerformsZeroWrites(string name, string host, int port, string? database, string? marker, string? optIn)
    {
        var probe = new SpyProbe(host, port, database, marker);
        var guard = new TestDatabaseGuard(probe, Manifest, optIn);

        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => guard.WriteAsync(Destructive));
        // İkinci deneme de yazmaz: başarısız doğrulama "doğrulandı" durumuna geçmez.
        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => guard.WriteAsync(Destructive));

        Assert.True(probe.Writes.Count == 0, $"'{name}' durumunda hedefe yazma gönderildi.");
        Assert.All(probe.Reads, sql => Assert.StartsWith("SELECT", sql, StringComparison.Ordinal));
    }

    [Fact]
    public async Task MarkerTableMissing_IsTreatedAsNotOwned_ZeroWrites()
    {
        var probe = new SpyProbe("127.0.0.1", 55432, "kulvar_test_run123", "run123", throwOnRead: true);
        var guard = new TestDatabaseGuard(probe, Manifest, TestDatabaseGuard.OptInValue);
        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => guard.WriteAsync(Destructive));
        Assert.Empty(probe.Writes);
    }

    [Fact]
    public async Task ManifestWithoutTestPrefix_IsRejected_BeforeAnyQuery()
    {
        var probe = new SpyProbe("127.0.0.1", 55432, "kulvar", "run123");
        var guard = new TestDatabaseGuard(probe, Manifest with { Database = "kulvar" }, TestDatabaseGuard.OptInValue);
        await Assert.ThrowsAsync<TestDatabaseGuardException>(() => guard.WriteAsync(Destructive));
        Assert.Empty(probe.Writes);
        Assert.Empty(probe.Reads);
    }
}
