using System.Diagnostics;
using System.IO;
using Reepax.Models;
using Reepax.Services.Storage;
using Xunit;

namespace Reepax.Tests;

public partial class ArchivePasswordTests
{
    [Fact]
    public void PasswordCounters_DetectInPlaceListEditsAndKeepSuccessWithTheOriginalEntry()
    {
        var service = new SettingsService(Path.Combine(_tempDirectory, "counter-edits.json"));
        try
        {
            service.ReplaceArchivePasswords(new[] { " Key ", "key", "KEY", "Other" });
            var original = service.RecordArchivePasswordAttempt(" Key ")!;
            long revision = service.ArchivePasswordListRevision;
            service.Settings.ExtractionPasswords[2] = "Changed";
            var changed = service.RecordArchivePasswordAttempt("Changed")!;
            Assert.Null(service.RecordArchivePasswordAttempt("KEY"));
            Assert.Same(original, service.RecordArchivePasswordAttempt(" Key "));
            service.Settings.ExtractionPasswords.Reverse();
            Assert.Same(changed, service.RecordArchivePasswordAttempt("Changed"));
            service.RecordArchivePasswordSuccess(original);
            Assert.Equal(1, original.SuccessCount);
            int index = service.Settings.ExtractionPasswords.IndexOf(" Key ");
            var replacement = new ArchivePasswordEntry { Password = " Key ", UsageCount = 3, SuccessCount = 1 };
            service.Settings.ArchivePasswordStatistics[index] = replacement;
            service.RecordArchivePasswordSuccess(original);
            Assert.Equal(1, replacement.SuccessCount);
            Assert.Same(replacement, service.RecordArchivePasswordAttempt(" Key "));
            Assert.Equal(4, replacement.UsageCount);
            service.RecordArchivePasswordSuccess(replacement);
            Assert.Equal(2, replacement.SuccessCount);
            Assert.Equal(revision, service.ArchivePasswordListRevision);
            Assert.Equal(new[] { "Other", "Changed", "key", " Key " }, service.GetArchivePasswordEntries().Select(entry => entry.Password));
            service.SaveSettings();
            var loaded = new SettingsService(Path.Combine(_tempDirectory, "counter-edits.json"));
            var persisted = loaded.GetArchivePasswordEntries().Single(entry => entry.Password == " Key ");
            Assert.Equal(4, persisted.UsageCount);
            Assert.Equal(2, persisted.SuccessCount);
        }
        finally { service.SaveSettings(); }
    }

    [Fact]
    public void PasswordCounters_NormalizeExternalChangesAndDoNotOverflow()
    {
        var service = new SettingsService(Path.Combine(_tempDirectory, "counter-limits.json"));
        try
        {
            service.RememberArchivePassword("KnownKey");
            var original = service.RecordArchivePasswordAttempt("KnownKey")!;
            original.UsageCount = -3;
            original.SuccessCount = 8;
            Assert.Same(original, service.RecordArchivePasswordAttempt("KnownKey"));
            Assert.Equal(1, original.UsageCount);
            Assert.Equal(0, original.SuccessCount);
            original.UsageCount = long.MaxValue;
            original.SuccessCount = long.MaxValue - 1;
            Assert.Same(original, service.RecordArchivePasswordAttempt("KnownKey"));
            service.RecordArchivePasswordSuccess(original);
            service.RecordArchivePasswordSuccess(original);
            Assert.Equal(long.MaxValue, original.UsageCount);
            Assert.Equal(long.MaxValue, original.SuccessCount);
            service.Settings.ExtractionPasswords = new List<string> { " KnownKey ", "knownkey", " KnownKey ", "", null! };
            var entries = service.GetArchivePasswordEntries();
            Assert.Equal(new[] { " KnownKey ", "knownkey" }, entries.Select(entry => entry.Password));
            service.RecordArchivePasswordSuccess(original);
            Assert.All(service.GetArchivePasswordEntries(), entry => Assert.Equal(0, entry.SuccessCount));
            service.Settings.ExtractionPasswords = null!;
            service.Settings.ArchivePasswordStatistics = null!;
            Assert.Null(service.RecordArchivePasswordAttempt("KnownKey"));
            Assert.Empty(service.GetArchivePasswordEntries());
            Assert.Null(service.RecordArchivePasswordAttempt(null!));
        }
        finally { service.SaveSettings(); }
    }

    [Theory]
    [InlineData(128)]
    [InlineData(1024)]
    public void PasswordAttempts_UnchangedListsKeepTheirIdentityAndBoundAllocations(int count)
    {
        var service = new SettingsService(Path.Combine(_tempDirectory, "counter-performance.json"));
        service.ReplaceArchivePasswords(Enumerable.Range(0, count).Select(index => $"Key{index:D4}"));
        var passwords = service.Settings.ExtractionPasswords;
        var statistics = service.Settings.ArchivePasswordStatistics;
        var tokens = new ArchivePasswordEntry?[count];
        try
        {
            service.GetArchivePasswordSnapshot();
            passwords = service.Settings.ExtractionPasswords;
            statistics = service.Settings.ArchivePasswordStatistics;
            long before = GC.GetAllocatedBytesForCurrentThread();
            var watch = Stopwatch.StartNew();
            for (int index = 0; index < count; index++)
                tokens[index] = service.RecordArchivePasswordAttempt(passwords[index]);
            double milliseconds = watch.Elapsed.TotalMilliseconds;
            long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
            Console.WriteLine($"Password counters: entries={count}; attempts={count}; elapsed={milliseconds:F2} ms; allocated={allocated} bytes.");
            Assert.Same(passwords, service.Settings.ExtractionPasswords);
            Assert.Same(statistics, service.Settings.ArchivePasswordStatistics);
            Assert.True(allocated < count * 1024L, "Counter updates must not allocate complete password lists per attempt.");
            for (int index = 0; index < count; index++)
            {
                Assert.Same(statistics[index], tokens[index]);
                Assert.Equal(1, statistics[index].UsageCount);
                Assert.Equal(0, statistics[index].SuccessCount);
            }
        }
        finally { service.SaveSettings(); }
    }
}
