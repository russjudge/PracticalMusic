using PracticalMusic.Core.Packaging;
using Xunit;

namespace PracticalMusic.Core.Tests;

public class MoveFileTests
{
    [Fact]
    public void Moves_a_received_book_to_the_chosen_location_and_removes_the_original()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmmove_" + Guid.NewGuid().ToString("N"));
        try
        {
            var source = Path.Combine(dir, "received", "Book.pmbook");
            Directory.CreateDirectory(Path.GetDirectoryName(source)!);
            var payload = new byte[70_000];
            new Random(7).NextBytes(payload);
            File.WriteAllBytes(source, payload);

            var target = Path.Combine(dir, "Documents", "sub", "My Book.pmbook"); // folder does not exist yet
            BookPackage.MoveFile(source, target);

            Assert.False(File.Exists(source));
            Assert.Equal(payload, File.ReadAllBytes(target));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Overwrites_an_existing_target_and_tolerates_same_path()
    {
        var dir = Path.Combine(Path.GetTempPath(), "pmmove_" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(dir);
            var source = Path.Combine(dir, "a.pmbook");
            var target = Path.Combine(dir, "b.pmbook");
            File.WriteAllBytes(source, new byte[] { 1, 2, 3 });
            File.WriteAllBytes(target, new byte[] { 9, 9, 9, 9, 9 });

            BookPackage.MoveFile(source, target);
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(target));

            BookPackage.MoveFile(target, target); // no-op, must not delete the file
            Assert.True(File.Exists(target));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* ignore */ }
        }
    }
}
