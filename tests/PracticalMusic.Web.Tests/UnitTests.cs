using System.Buffers.Binary;
using System.IO.Compression;
using PracticalMusic.Core.Models;
using PracticalMusic.Web.Data;
using PracticalMusic.Web.Security;
using PracticalMusic.Web.Services;
using Xunit;

namespace PracticalMusic.Web.Tests;

public sealed class UnitTests
{
    [Fact]
    public void Png_encoder_writes_valid_grayscale_and_color_images()
    {
        foreach (var color in new[] { false, true })
        {
            int w = 37, h = 11;
            var px = new byte[w * h * 4];
            for (var i = 0; i < w * h; i++)
            {
                var v = (byte)(i * 7);
                px[i * 4] = v; px[i * 4 + 1] = v; px[i * 4 + 2] = color ? (byte)(255 - v) : v; px[i * 4 + 3] = 255;
            }
            var png = PngEncoder.Encode(w, h, px);
            Assert.Equal(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }, png[..8]);
            Assert.Equal("IHDR", System.Text.Encoding.ASCII.GetString(png, 12, 4));
            Assert.Equal(w, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(16)));
            Assert.Equal(h, BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(20)));
            Assert.Equal(color ? 2 : 0, png[25]);
            // IDAT inflates to height × (1 filter byte + row)
            var idatLength = BinaryPrimitives.ReadInt32BigEndian(png.AsSpan(33));
            Assert.Equal("IDAT", System.Text.Encoding.ASCII.GetString(png, 37, 4));
            using var z = new ZLibStream(new MemoryStream(png, 41, idatLength), CompressionMode.Decompress);
            using var raw = new MemoryStream();
            z.CopyTo(raw);
            Assert.Equal(h * (1 + w * (color ? 3 : 1)), raw.Length);
            Assert.Equal("IEND", System.Text.Encoding.ASCII.GetString(png, png.Length - 8, 4));
        }
    }

    [Theory]
    [InlineData("someone@example.com", true)]
    [InlineData("  Someone@Example.COM ", true)]
    [InlineData("no-at-sign", false)]
    [InlineData("two@@example.com", false)]
    [InlineData("a@b", false)]
    [InlineData("spa ce@example.com", false)]
    public void Email_addresses_are_checked(string email, bool valid) => Assert.Equal(valid, Text.IsValidEmail(email));

    [Fact]
    public void Passwords_need_length_not_rules()
    {
        Assert.NotNull(PasswordHashing.Validate("short"));
        Assert.NotNull(PasswordHashing.Validate("password123"));
        Assert.NotNull(PasswordHashing.Validate("aaaaaaaaaa"));
        Assert.NotNull(PasswordHashing.Validate("me@example.com", "me@example.com"));
        Assert.Null(PasswordHashing.Validate("we sing on sunday"));
        var hashing = new PasswordHashing();
        var hash = hashing.Hash("we sing on sunday");
        Assert.True(hashing.Verify(hash, "we sing on sunday").Valid);
        Assert.False(hashing.Verify(hash, "we sing on monday").Valid);
        Assert.False(hashing.Verify(null, "anything").Valid);
        Assert.False(hashing.Verify("garbage", "anything").Valid);
    }

    [Fact]
    public void Tokens_are_random_and_only_hashes_are_kept()
    {
        var a = Tokens.NewToken();
        var b = Tokens.NewToken();
        Assert.NotEqual(a, b);
        Assert.True(a.Length >= 40);
        Assert.Equal(64, Tokens.Hash(a).Length);
        Assert.Equal(Tokens.Hash(a), Tokens.Hash(a));
    }

    [Fact]
    public void Uploads_are_recognized_by_content()
    {
        Assert.True(MediaStorage.IsPdf("%PDF-1.7"u8));
        Assert.True(MediaStorage.LooksLikeAudio("ID3\u0004\0\0"u8));
        Assert.True(MediaStorage.LooksLikeAudio(new byte[] { 0xFF, 0xFB, 0x90, 0x00 }));   // MPEG-1 Layer 3, 128 kbit/s, 44.1 kHz
        Assert.True(MediaStorage.LooksLikeAudio("OggS\0\0\0\0"u8));
        Assert.True(MediaStorage.LooksLikeAudio(new byte[] { 0, 0, 0, 0x20, (byte)'f', (byte)'t', (byte)'y', (byte)'p' }));
        Assert.False(MediaStorage.LooksLikeAudio("<html><body>"u8));
        // MP3 with zero padding before the first frame (seen in real files): accepted
        var padded = new byte[1000];
        new byte[] { 0xFF, 0xFB, 0x90, 0x64 }.CopyTo(padded, 417);
        Assert.True(MediaStorage.LooksLikeAudio(padded));
        // frame sync with reserved/invalid fields is not audio
        Assert.False(MediaStorage.LooksLikeAudio(new byte[] { 0xFF, 0xEF, 0xF0, 0x00 }));   // version "reserved"
        Assert.False(MediaStorage.LooksLikeAudio(new byte[] { 0xFF, 0xFB, 0xF0, 0x00 }));   // bit rate index 15
        Assert.False(MediaStorage.LooksLikeAudio(new byte[1000]));                          // only zeros
        Assert.True(MediaStorage.LooksLikeAudio(new byte[] { 0xFF, 0xF1, 0x50, 0x80 }));    // ADTS AAC
        Assert.Equal("evil.sh", MediaStorage.CleanFileName("../../etc/evil.sh"));
        Assert.Equal("c_d.mp3", MediaStorage.CleanFileName("a\\b\\c|d.mp3").Replace("|", "_"));
    }

    [Fact]
    public void Canonical_books_have_safe_unique_entry_names_and_clamped_values()
    {
        var pdf = new MediaRow { Id = 1, OrgId = 9, Kind = MediaKinds.Pdf, FileName = "a.pdf", SizeBytes = 10, PageCount = 4 };
        var mp3a = new MediaRow { Id = 2, OrgId = 9, Kind = MediaKinds.Audio, FileName = "x.mp3", SizeBytes = 20 };
        var mp3b = new MediaRow { Id = 3, OrgId = 9, Kind = MediaKinds.Audio, FileName = "x.mp3", SizeBytes = 30 };
        var book = new Book { Title = "  T  " };
        book.Documents.Add(new PdfDocumentRef { Id = "", Name = "", PageCount = 1, Source = new MediaSource { Kind = MediaSourceKind.Embedded, EntryName = "../../evil", FileName = "a.pdf", Path = @"C:\secret\a.pdf" } });
        var song = new Song { Title = "S", StartPage = 0, PageCount = 10, HighlightHeight = 99 };
        song.Recordings.Add(new Recording { Name = "1", Source = new MediaSource { Kind = MediaSourceKind.Embedded, EntryName = "e1", FileName = "x.mp3" }, Markers = { new TimeMarker { X = 5, Y = -1, TimeMs = -3, PageOffset = 0 } } });
        song.Recordings.Add(new Recording { Name = "2", Source = new MediaSource { Kind = MediaSourceKind.Embedded, EntryName = "e2", FileName = "x.mp3" } });
        book.Songs.Add(song);
        var refs = new Dictionary<string, long> { ["../../evil"] = 1, ["e1"] = 2, ["e2"] = 3 };
        var (canonical, entries, bytes) = BookService.Canonicalize(book, refs, new Dictionary<long, MediaRow> { [1] = pdf, [2] = mp3a, [3] = mp3b });

        Assert.Equal("T", canonical.Title);
        Assert.Equal(4, canonical.Documents[0].PageCount);          // from the stored PDF, not the client
        Assert.Equal(4, canonical.Songs[0].PageCount);              // clamped into the book
        Assert.Equal(0.5, canonical.Songs[0].HighlightHeight);
        Assert.Null(canonical.Documents[0].Source.Path);            // no local paths on the server
        Assert.All(entries.Keys, k => Assert.Matches("^media/[^/\\\\]+$", k));
        Assert.Equal(3, entries.Count);
        Assert.Equal(new[] { "media/a.pdf", "media/x.mp3", "media/x_2.mp3" }, entries.Keys.Order().ToArray());
        var m = canonical.Songs[0].Recordings[0].Markers[0];
        Assert.Equal((1.0, 0.0, 0.0), (m.X, m.Y, m.TimeMs));
        Assert.Equal(60, bytes);
        Assert.False(string.IsNullOrEmpty(canonical.Documents[0].Id));
    }

    [Fact]
    public void Books_must_not_point_at_files_of_the_wrong_kind_or_unknown_files()
    {
        var book = new Book();
        book.Documents.Add(new PdfDocumentRef { Source = new MediaSource { Kind = MediaSourceKind.Embedded, EntryName = "m", FileName = "a.mp3" } });
        var audio = new MediaRow { Id = 1, Kind = MediaKinds.Audio, FileName = "a.mp3" };
        Assert.Throws<UserFacingException>(() => BookService.Canonicalize(book, new Dictionary<string, long> { ["m"] = 1 }, new Dictionary<long, MediaRow> { [1] = audio }));
        Assert.Throws<UserFacingException>(() => BookService.Canonicalize(book, new Dictionary<string, long>(), new Dictionary<long, MediaRow>()));
    }
}
