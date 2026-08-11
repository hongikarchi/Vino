using System.Net;
using GPTino.AgentHost.Data;

namespace GPTino.AgentHost.Tests;

public sealed class ImageUrlAttachmentFetcherTests
{
    [Theory]
    [InlineData("127.0.0.1")]
    [InlineData("10.0.0.5")]
    [InlineData("192.168.1.9")]
    [InlineData("172.16.4.4")]
    [InlineData("169.254.169.254")] // cloud metadata endpoint
    [InlineData("0.0.0.0")]
    [InlineData("::1")] // IPv6 loopback
    [InlineData("fe80::1")] // IPv6 link-local
    [InlineData("::ffff:127.0.0.1")] // IPv4-mapped loopback
    public void BlocksNonPublicAddressesResolvedAtConnectTime(string address)
    {
        // The connect-time guard rejects a RESOLVED address in this space, which is what closes DNS
        // rebinding and redirect-to-internal that the URL-literal filter alone cannot see.
        Assert.True(ImageUrlAttachmentFetcher.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData("172.15.0.1")] // just outside the 172.16-31 private range
    [InlineData("172.32.0.1")]
    [InlineData("2606:4700:4700::1111")] // public IPv6
    public void AllowsPublicAddresses(string address)
    {
        Assert.False(ImageUrlAttachmentFetcher.IsBlockedAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void RecognizesRealImageSignaturesAndRejectsOthers()
    {
        Assert.True(ImageUrlAttachmentFetcher.IsRecognizedImage(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }));
        Assert.True(ImageUrlAttachmentFetcher.IsRecognizedImage(new byte[] { 0xFF, 0xD8, 0xFF, 0xE0 }));
        Assert.True(ImageUrlAttachmentFetcher.IsRecognizedImage(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61 }));
        Assert.True(ImageUrlAttachmentFetcher.IsRecognizedImage(
            new byte[] { 0x52, 0x49, 0x46, 0x46, 0, 0, 0, 0, 0x57, 0x45, 0x42, 0x50 }));
        // HTML from an internal service that a .png link redirected to: not stored as an image.
        Assert.False(ImageUrlAttachmentFetcher.IsRecognizedImage(
            System.Text.Encoding.ASCII.GetBytes("<!doctype html><html>")));
        Assert.False(ImageUrlAttachmentFetcher.IsRecognizedImage(new byte[] { 0x00, 0x01 }));
    }

    [Fact]
    public void ExtractsDistinctImageUrlsAcrossFormatsAndToleratesQueryStrings()
    {
        var content = """
            Use these refs:
            https://cdn.example.com/a.png
            http://img.example.org/b.JPG?width=800&token=abc
            see also https://example.net/pics/c.webp and https://example.net/pics/d.gif
            duplicate: https://cdn.example.com/a.png
            """;

        var urls = ImageUrlAttachmentFetcher.ExtractImageUrls(content);

        Assert.Equal(
            new[]
            {
                "https://cdn.example.com/a.png",
                "http://img.example.org/b.JPG?width=800&token=abc",
                "https://example.net/pics/c.webp",
                "https://example.net/pics/d.gif",
            },
            urls);
    }

    [Fact]
    public void IgnoresNonImageLinksPlainTextAndUnsupportedSchemes()
    {
        var content = "docs at https://example.com/page and ftp://host/x.png and a bare word file.png";
        Assert.Empty(ImageUrlAttachmentFetcher.ExtractImageUrls(content));
    }

    [Fact]
    public void RejectsLoopbackAndPrivateAndLinkLocalHosts()
    {
        var content = """
            http://localhost/a.png
            http://127.0.0.1/b.png
            http://10.0.0.5/c.png
            http://192.168.1.9/d.png
            http://172.16.4.4/e.png
            http://169.254.169.254/latest/meta-data/f.png
            """;
        Assert.Empty(ImageUrlAttachmentFetcher.ExtractImageUrls(content));
    }

    [Fact]
    public void CapsAtThePerMessageAttachmentLimit()
    {
        var content = string.Join(
            "\n",
            Enumerable.Range(0, 10).Select(index => $"https://cdn.example.com/img-{index}.png"));
        Assert.Equal(AttachmentStore.MaxAttachmentsPerMessage, ImageUrlAttachmentFetcher.ExtractImageUrls(content).Count);
    }

    [Fact]
    public void ReturnsEmptyForNullOrBlankContent()
    {
        Assert.Empty(ImageUrlAttachmentFetcher.ExtractImageUrls(null));
        Assert.Empty(ImageUrlAttachmentFetcher.ExtractImageUrls("   "));
    }
}
