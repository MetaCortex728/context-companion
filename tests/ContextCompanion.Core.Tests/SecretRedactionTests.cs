using System.Text;
using ContextCompanion.Core.Privacy;

namespace ContextCompanion.Core.Tests;

/// <summary>
/// Fake secrets for tests. None of them is a secret-shaped literal in the repository: each is assembled at run time
/// from fragments, and every body is an obviously fake filler, so host-side secret scanners have nothing to flag.
/// </summary>
internal static class FakeSecrets
{
    /// <summary>A fake body of <paramref name="length"/> characters from A-Z, a-z and 0-9.</summary>
    public static string Body(int length)
    {
        var text = new StringBuilder();
        while (text.Length < length) text.Append("FAKE0fake");
        return text.ToString(0, length);
    }

    private static string Base64Url(string json) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(json)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string KeyLine(string kind, string edge) => "-----" + edge + " " + kind + "PRIV" + "ATE KEY" + "-----";

    public static string PrivateKey(string kind = "RSA ") =>
        KeyLine(kind, "BEGIN") + "\n" + Body(40) + "\n" + Body(20) + "\n" + KeyLine(kind, "END");

    public static string UnterminatedPrivateKey() => KeyLine("", "BEGIN") + "\n" + Body(40) + "\n";

    public static string PgpPrivateKey() =>
        "-----BEGIN PGP " + "PRIV" + "ATE KEY BLOCK-----\n" + Body(30) + "\n-----END PGP " + "PRIV" + "ATE KEY BLOCK-----";

    public static string Jwt() =>
        Base64Url("""{"alg":"none","typ":"FAKE"}""") + "." + Base64Url("""{"sub":"fake-user"}""") + "." + "fakesignature";

    public static string AwsAccessKey() => "AK" + "IA" + "FAKEFAKEFAKE0000";
    public static string GitHubToken() => "gh" + "p_" + Body(36);
    public static string GitHubFineGrained() => "github" + "_pat_" + Body(40);
    public static string GitLabToken() => "gl" + "pat-" + Body(20);
    public static string SlackToken() => "xo" + "xb-" + "0000-" + Body(12);
    public static string StripeKey() => "sk" + "_live_" + Body(24);
    public static string GoogleApiKey() => "AI" + "za" + Body(35);
    public static string AnthropicKey() => "sk-" + "ant-" + "api03-" + Body(40);
    public static string OpenAiKey() => "sk-" + "proj-" + Body(40);
    public static string HuggingFaceToken() => "hf" + "_" + Body(34);
    public const string FakePassword = "Fake-Passw0rd";
}

public class SecretRedactionTests
{
    private static readonly SecretRedactor Redactor = SecretRedactor.FromSettings(new RedactionSettings());

    private static void AssertRedacts(string secret, string pattern)
    {
        var result = Redactor.Redact($"before {secret} after");
        Assert.Equal($"before [REDACTED:{pattern}] after", result.Text);
        Assert.Equal([new SecretPatternCount(pattern, 1)], result.Counts);
    }

    public static TheoryData<string, string> WholeMatches => new()
    {
        { FakeSecrets.PrivateKey(), "private-key" },
        { FakeSecrets.PrivateKey("OPENSSH "), "private-key" },
        { FakeSecrets.PrivateKey("EC "), "private-key" },
        { FakeSecrets.PrivateKey(""), "private-key" },
        { FakeSecrets.PgpPrivateKey(), "private-key" },
        { FakeSecrets.Jwt(), "jwt" },
        { FakeSecrets.AwsAccessKey(), "aws-access-key" },
        { FakeSecrets.GitHubToken(), "github-token" },
        { FakeSecrets.GitHubFineGrained(), "github-token" },
        { FakeSecrets.GitLabToken(), "gitlab-token" },
        { FakeSecrets.SlackToken(), "slack-token" },
        { FakeSecrets.StripeKey(), "stripe-key" },
        { FakeSecrets.GoogleApiKey(), "google-api-key" },
        { FakeSecrets.AnthropicKey(), "anthropic-api-key" },
        { FakeSecrets.OpenAiKey(), "openai-api-key" },
        { FakeSecrets.HuggingFaceToken(), "huggingface-token" },
    };

    [Theory]
    [MemberData(nameof(WholeMatches))]
    public void EachTokenPatternReplacesTheWholeToken(string secret, string pattern) => AssertRedacts(secret, pattern);

    [Fact]
    public void AnUnterminatedPrivateKeyIsRedactedToTheEnd()
    {
        var result = Redactor.Redact("key:\n" + FakeSecrets.UnterminatedPrivateKey() + "more lines\n");
        Assert.Equal("key:\n[REDACTED:private-key]", result.Text);
    }

    [Theory]
    [InlineData("Server=db.example;User Id=app;Password={0};Encrypt=true", "Server=db.example;User Id=app;Password=[REDACTED:connection-string];Encrypt=true")]
    [InlineData("Host=h; Pwd = {0}", "Host=h; Pwd = [REDACTED:connection-string]")]
    [InlineData("DefaultEndpointsProtocol=https;AccountName=fake;AccountKey={0};EndpointSuffix=core.windows.net", "DefaultEndpointsProtocol=https;AccountName=fake;AccountKey=[REDACTED:connection-string];EndpointSuffix=core.windows.net")]
    [InlineData("postgres://app:{0}@db.example/app", "postgres://app:[REDACTED:connection-string]@db.example/app")]
    public void ConnectionStringPasswordsAreRedacted(string template, string expected)
    {
        var result = Redactor.Redact(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, FakeSecrets.FakePassword));
        Assert.Equal(expected, result.Text);
        Assert.Equal([new SecretPatternCount("connection-string", 1)], result.Counts);
    }

    [Theory]
    [InlineData("DB_PASSWORD={0}", "DB_PASSWORD=[REDACTED:env-assignment]", "env-assignment")]
    [InlineData("export API_KEY=\"{0}\"", "export API_KEY=\"[REDACTED:env-assignment]\"", "env-assignment")]
    [InlineData("  SECRET_KEY = '{0}'", "  SECRET_KEY = '[REDACTED:env-assignment]'", "env-assignment")]
    [InlineData("password = \"{0}\"", "password = \"[REDACTED:secret-assignment]\"", "secret-assignment")]
    [InlineData("\"client_secret\": \"{0}\",", "\"client_secret\": \"[REDACTED:secret-assignment]\",", "secret-assignment")]
    [InlineData("auth_token: {0}", "auth_token: [REDACTED:secret-assignment]", "secret-assignment")]
    [InlineData("db.passwd='{0}'", "db.passwd='[REDACTED:secret-assignment]'", "secret-assignment")]
    [InlineData("apiKey := \"{0}\"", "apiKey := \"[REDACTED:secret-assignment]\"", "secret-assignment")]
    [InlineData("--token={0}", "--token=[REDACTED:secret-assignment]", "secret-assignment")]
    public void AssignmentsKeepTheNameAndLoseTheValue(string template, string expected, string pattern)
    {
        var result = Redactor.Redact(string.Format(System.Globalization.CultureInfo.InvariantCulture, template, FakeSecrets.FakePassword));
        Assert.Equal(expected, result.Text);
        Assert.Equal([new SecretPatternCount(pattern, 1)], result.Counts);
    }

    [Fact]
    public void OverlappingMatchesBecomeOneReplacementNamedAfterTheMostSpecificPattern()
    {
        // The token is matched by github-token, env-assignment and secret-assignment at the same place.
        var same = Redactor.Redact("GITHUB_TOKEN=" + FakeSecrets.GitHubToken());
        Assert.Equal("GITHUB_TOKEN=[REDACTED:github-token]", same.Text);
        Assert.Equal([new SecretPatternCount("github-token", 1)], same.Counts);

        // A token inside an unterminated key block: the longer match that starts first absorbs it.
        var nested = Redactor.Redact("x " + FakeSecrets.UnterminatedPrivateKey() + FakeSecrets.GitHubToken());
        Assert.Equal("x [REDACTED:private-key]", nested.Text);
        Assert.Equal([new SecretPatternCount("private-key", 1)], nested.Counts);

        // A JWT used as the password of a URL.
        var url = Redactor.Redact("https://user:" + FakeSecrets.Jwt() + "@api.example/");
        Assert.Equal("https://user:[REDACTED:jwt]@api.example/", url.Text);
    }

    [Fact]
    public void PartlyOverlappingMatchesAreMergedSoNothingOfEitherSurvives()
    {
        var redactor = new SecretRedactor([
            new SecretPattern { Name = "left", Regex = "AAAA-BB" },
            new SecretPattern { Name = "right", Regex = "BB-CCCC" },
        ]);
        var result = redactor.Redact("x AAAA-BB-CCCC y");
        Assert.Equal("x [REDACTED:left] y", result.Text);
        Assert.Equal([new SecretPatternCount("left", 1)], result.Counts);
    }

    [Fact]
    public void SeveralMatchesAreCountedPerPattern()
    {
        string text = string.Join("\n", FakeSecrets.GitHubToken(), FakeSecrets.Jwt(), FakeSecrets.GitHubFineGrained(), "plain");
        var result = Redactor.Redact(text);
        Assert.Equal("[REDACTED:github-token]\n[REDACTED:jwt]\n[REDACTED:github-token]\nplain", result.Text);
        Assert.Equal([new SecretPatternCount("jwt", 1), new SecretPatternCount("github-token", 2)], result.Counts);
    }

    [Fact]
    public void RedactingTwiceChangesNothingMore()
    {
        string text = string.Join("\n",
            FakeSecrets.PrivateKey(), FakeSecrets.Jwt(), FakeSecrets.AwsAccessKey(), FakeSecrets.GitHubToken(),
            FakeSecrets.GitLabToken(), FakeSecrets.SlackToken(), FakeSecrets.StripeKey(), FakeSecrets.GoogleApiKey(),
            FakeSecrets.AnthropicKey(), FakeSecrets.OpenAiKey(), FakeSecrets.HuggingFaceToken(),
            "Server=s;Password=" + FakeSecrets.FakePassword + ";", "postgres://u:" + FakeSecrets.FakePassword + "@h/d",
            "DB_PASSWORD=" + FakeSecrets.FakePassword, "password = \"" + FakeSecrets.FakePassword + "\"",
            "token: " + FakeSecrets.FakePassword, "secret='" + FakeSecrets.FakePassword + "'",
            "x " + FakeSecrets.UnterminatedPrivateKey());
        var once = Redactor.Redact(text);
        Assert.Equal(18, once.Total);
        Assert.DoesNotContain("FAKE0", once.Text, StringComparison.Ordinal);
        Assert.DoesNotContain(FakeSecrets.FakePassword, once.Text, StringComparison.Ordinal);

        var twice = Redactor.Redact(once.Text);
        Assert.Equal(once.Text, twice.Text);
        Assert.Empty(twice.Counts);
    }

    [Fact]
    public void TextAroundAnExistingMarkerIsStillRedacted()
    {
        // An unterminated key block that already contains a marker must still be removed around it.
        var result = Redactor.Redact(FakeSecrets.UnterminatedPrivateKey() + "[REDACTED:jwt]\n" + Body());
        Assert.Equal("[REDACTED:private-key][REDACTED:jwt][REDACTED:private-key]", result.Text);
        static string Body() => FakeSecrets.Body(30);
    }

    private const string ProseAndCode = """
        The password policy requires twelve characters, and tokens expire after an hour.
        Remember: secrets belong in a vault, not in code. See the token: section below.
        Our API key rotation runs monthly; ask-me-anything sessions are on Fridays.
        Install sk-learn? No, it is called scikit-learn. Visit https://example.com:8080/docs?page=2.
        Clone git@github.com:example/repo.git and read docs/password-reset.md.

        def login(user, password):
            if password == "":
                raise ValueError("password required")
            self.password = password
            token = generate_token(32)
            max_tokens = 4096
            api_key = os.environ["API_KEY"]
            secret = get_secret("name")
            password: Optional[str] = None
            return token

        const password = process.env.DB_PASSWORD;
        interface Props { password: string; onToken: (t: string) => void }
        if (a != b && c <= d) { items.map(x => x.token); }
        let passwordValid = password.length >= 12;
        SELECT password_hash FROM users WHERE id = 42;
        { "token_type": "bearer", "expires_in": 3600, "password_hint": "first pet" }
        password: ${DB_PASSWORD}
        api_key: "{{ vault_api_key }}"
        MAX_TOKENS=100000
        PWD=/home/user/project
        """;

    [Fact]
    public void OrdinaryProseAndCodeAreLeftAlone()
    {
        var result = Redactor.Redact(ProseAndCode);
        Assert.Empty(result.Counts);
        Assert.Equal(ProseAndCode, result.Text);
    }

    [Fact]
    public void TheBuiltInListIsVersionedDataAndEveryPatternIsTested()
    {
        var file = SecretPatternFile.BuiltIn();
        Assert.Equal(1, file.SchemaVersion);
        var tested = WholeMatches.Select(row => (string)row[1]).Concat(["connection-string", "env-assignment", "secret-assignment"]).ToHashSet();
        Assert.Equal(file.Patterns.Select(p => p.Name).Order(), tested.Order());
        Assert.All(file.Patterns, p => Assert.False(string.IsNullOrWhiteSpace(p.Description)));
    }

    [Fact]
    public void PatternsCanBeAddedAndDisabledInTheSettings()
    {
        var settings = PrivacySettings.Parse("""
            {"schema_version":1,"redaction":{"disabled_patterns":["jwt"],"extra_patterns":[{"name":"ticket","regex":"TICKET-[0-9]{4}"}]}}
            """);
        var redactor = SecretRedactor.FromSettings(settings.Redaction);
        var result = redactor.Redact("TICKET-1234 " + FakeSecrets.Jwt());
        Assert.Equal("[REDACTED:ticket] " + FakeSecrets.Jwt(), result.Text);
    }

    [Theory]
    [InlineData("""{"disabled_patterns":["no-such-pattern"]}""")]
    [InlineData("""{"extra_patterns":[{"name":"Bad Name","regex":"x"}]}""")]
    [InlineData("""{"extra_patterns":[{"name":"empty-match","regex":"x*"}]}""")]
    [InlineData("""{"extra_patterns":[{"name":"broken","regex":"("}]}""")]
    [InlineData("""{"extra_patterns":[{"name":"jwt","regex":"x"}]}""")]
    [InlineData("""{"extra_patterns":[{"name":"a","regex":"x"},{"name":"a","regex":"y"}]}""")]
    public void InvalidRedactionSettingsAreRefused(string redaction) =>
        Assert.Throws<PrivacySettingsException>(() => PrivacySettings.Parse($$"""{"schema_version":1,"redaction":{{redaction}}}"""));

    [Fact]
    public void APatternThatTimesOutIsReported()
    {
        var redactor = new SecretRedactor([new SecretPattern { Name = "slow", Regex = "^(a+)+$" }]);
        var ex = Assert.Throws<SecretScanTimeoutException>(() => redactor.Redact(new string('a', 64) + "!"));
        Assert.Equal("slow", ex.Pattern);
    }
}
