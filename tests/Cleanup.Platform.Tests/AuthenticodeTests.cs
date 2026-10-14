using Cleanup.Platform.Windows;

namespace Cleanup.Platform.Tests;

public sealed class AuthenticodeTests
{
    /// <summary>The .NET runtime's own coreclr.dll carries an embedded Microsoft signature (Windows' files are catalog-signed instead).</summary>
    private static string SignedFile => Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location)!, "coreclr.dll");

    [Fact]
    public void NamesTheSignerOfASignedFile()
    {
        Assert.True(File.Exists(SignedFile), SignedFile);
        Assert.False(string.IsNullOrWhiteSpace(Authenticode.Signer(SignedFile)));
    }

    [Fact]
    public void UnsignedFilesHaveNoSigner()
    {
        // A real PE without a signature (this repo's own build), and a file that isn't a PE at all.
        Assert.Null(Authenticode.Signer(typeof(Authenticode).Assembly.Location));
        var path = Path.Combine(Path.GetTempPath(), $"cleanup-unsigned-{Guid.NewGuid():N}.exe");
        try
        {
            File.WriteAllBytes(path, [0x4D, 0x5A, 1, 2, 3, 4]);
            Assert.Null(Authenticode.Signer(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void ATamperedSignedFileHasNoSigner()
    {
        var path = Path.Combine(Path.GetTempPath(), $"cleanup-tampered-{Guid.NewGuid():N}.dll");
        try
        {
            var bytes = File.ReadAllBytes(SignedFile);
            bytes[bytes.Length / 2] ^= 0xFF;
            File.WriteAllBytes(path, bytes);
            Assert.Null(Authenticode.Signer(path));
        }
        finally { File.Delete(path); }
    }

    [Fact]
    public void AMissingFileHasNoSigner() => Assert.Null(Authenticode.Signer(Path.Combine(Path.GetTempPath(), $"cleanup-missing-{Guid.NewGuid():N}.exe")));
}
