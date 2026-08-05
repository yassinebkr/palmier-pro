using PalmierShell.Core;
using Xunit;

namespace PalmierShell.Tests;

public class LogShareTests {
    [Theory]
    [InlineData("key sk-ant-api03-AbCdEfGhIjKl saved", "key <redacted> saved")]
    [InlineData("openai sk-proj-AbCdEfGhIjKlMnOp", "openai <redacted>")]
    [InlineData("Bearer tok_abc123xyz", "<redacted>")]
    [InlineData("plain log line, no secrets", "plain log line, no secrets")]
    public void RedactStripsKeyShapedStrings(string input, string expected) {
        Assert.Equal(expected, LogShare.Redact(input));
    }

    [Fact]
    public void CollectLogsBundlesRecentLogsWithInfo() {
        string zip = Path.Combine(Path.GetTempPath(), $"logshare-test-{Guid.NewGuid():N}.zip");
        try {
            int count = LogShare.CollectLogs(zip, "test note");
            Assert.True(count >= 0);
            using var archive = System.IO.Compression.ZipFile.OpenRead(zip);
            var info = archive.GetEntry("info.txt");
            Assert.NotNull(info);
            using var reader = new StreamReader(info!.Open());
            string text = reader.ReadToEnd();
            Assert.Contains("diagnostic bundle", text);
            Assert.Contains("test note", text);
        } finally {
            File.Delete(zip);
        }
    }
}
