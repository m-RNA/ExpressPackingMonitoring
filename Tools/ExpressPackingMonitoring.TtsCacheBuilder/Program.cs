using ExpressPackingMonitoring.Audio;
using ExpressPackingMonitoring.Config;
using ExpressPackingMonitoring.Localization;
using ExpressPackingMonitoring.Services;

if (args.Length != 1 || string.IsNullOrWhiteSpace(args[0]))
{
    Console.Error.WriteLine("Usage: ExpressPackingMonitoring.TtsCacheBuilder <output-directory>");
    return 2;
}

string outputDirectory = Path.GetFullPath(args[0]);
Directory.CreateDirectory(outputDirectory);
var config = new AppConfig();
AppConfig.NormalizeAfterLoad(config);
var languageProfiles = new[]
{
    new
    {
        Language = AppLanguage.Chinese,
        Voice = config.EdgeTtsVoiceZhHans,
        WarningVoice = config.EdgeTtsWarningVoiceZhHans
    },
    new
    {
        Language = AppLanguage.English,
        Voice = config.EdgeTtsVoiceEnUs,
        WarningVoice = config.EdgeTtsWarningVoiceEnUs
    }
};

int generated = 0;
var failures = new List<string>();
var expectedCachePaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
int expectedPromptCount = 0;
foreach (var profile in languageProfiles)
{
    AppLanguage.Initialize(profile.Language);
    IReadOnlyList<DefaultSpeechPrompt> prompts = DefaultSpeechCatalog.Prompts;
    expectedPromptCount += prompts.Count;
    using var speech = new SpeechService(outputDirectory)
    {
        EnableSoundPrompt = true,
        EnableAiTts = true,
        AiTtsEngine = config.AiTtsEngine,
        AiTtsSpeakerId = config.AiTtsSpeakerId,
        AiTtsWarningSpeakerId = config.AiTtsWarningSpeakerId,
        AiTtsSpeed = config.AiTtsSpeed,
        EdgeTtsVoice = profile.Voice,
        EdgeTtsWarningVoice = profile.WarningVoice,
        TtsCacheMaxSizeMB = 0
    };

    Console.WriteLine($"Generating {profile.Language} cache: normal={profile.Voice}, warning={profile.WarningVoice}");
    foreach (DefaultSpeechPrompt prompt in prompts)
    {
        bool isWarning = prompt.VoiceStyle == AlertVoiceStyle.Warning;
        if (speech.GenerateCacheNow(prompt.Text, isWarning, out string cachePath))
        {
            expectedCachePaths.Add(Path.GetFullPath(cachePath));
            generated++;
            Console.WriteLine($"[{generated}] [{profile.Language}] {prompt.Text}");
        }
        else
        {
            failures.Add($"[{profile.Language}] {prompt.Text}");
            Console.Error.WriteLine($"Failed: [{profile.Language}] {prompt.Text}");
        }
    }
}

foreach (string cachePath in Directory.GetFiles(outputDirectory, "*.*", SearchOption.TopDirectoryOnly))
{
    string extension = Path.GetExtension(cachePath);
    if ((string.Equals(extension, ".mp3", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(extension, ".wav", StringComparison.OrdinalIgnoreCase)) &&
        !expectedCachePaths.Contains(Path.GetFullPath(cachePath)))
    {
        File.Delete(cachePath);
        Console.WriteLine($"Removed stale cache: {Path.GetFileName(cachePath)}");
    }
}

if (failures.Count > 0)
{
    Console.Error.WriteLine($"TTS cache generation failed for {failures.Count} prompt(s).");
    return 1;
}

int cacheFileCount = Directory.GetFiles(outputDirectory, "*.*", SearchOption.TopDirectoryOnly)
    .Count(path => string.Equals(Path.GetExtension(path), ".mp3", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(Path.GetExtension(path), ".wav", StringComparison.OrdinalIgnoreCase));
if (cacheFileCount < expectedPromptCount)
{
    Console.Error.WriteLine($"Expected at least {expectedPromptCount} cache files, found {cacheFileCount}.");
    return 1;
}

Console.WriteLine($"Generated {cacheFileCount} default TTS cache files in {outputDirectory}");
return 0;
