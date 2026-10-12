using Cleanup.Core.Rules;

namespace Cleanup.Core.Developer;

/// <summary>
/// Package-manager and IDE caches (design §5.2). On macOS these are Smart Scan rules; the shared Windows rules file
/// leaves them out, so Desenvolvedor › Caches scans them with the same <c>RuleScanTask</c>. Everything is downloaded
/// or rebuilt again on demand; stores that projects link to (NuGet packages, Maven, pnpm) need review.
/// </summary>
public static class DeveloperCaches
{
    public const string AndroidSystemImagesId = "android-system-images";

    private static CleanupRule Rule(string id, string name, RiskLevel risk, string description, params string[] paths) => new()
    {
        Id = id, Name = name, Category = ScanCategory.DeveloperJunk, Risk = risk, Paths = paths, Description = description,
    };

    public static IReadOnlyList<CleanupRule> Rules { get; } =
    [
        Rule("npm-cache", "Cache do npm", RiskLevel.Safe, "Cache de pacotes do npm.",
            @"%LOCALAPPDATA%\npm-cache\_cacache", @"%LOCALAPPDATA%\npm-cache\_npx"),
        Rule("pnpm-store", "pnpm store", RiskLevel.Review, "Armazenamento global do pnpm. Projetos baixam os pacotes de novo no próximo install.",
            @"%LOCALAPPDATA%\pnpm\store\*", @"%LOCALAPPDATA%\pnpm-cache\*"),
        Rule("yarn-cache", "Cache do Yarn", RiskLevel.Safe, "Pacotes baixados pelo Yarn.",
            @"%LOCALAPPDATA%\Yarn\Cache\*", @"%LOCALAPPDATA%\Yarn\Berry\cache"),
        Rule("pip-cache", "Cache do pip", RiskLevel.Safe, "Pacotes Python baixados pelo pip.",
            @"%LOCALAPPDATA%\pip\Cache\*"),
        Rule("nuget-http-cache", "Cache HTTP do NuGet", RiskLevel.Safe, "Downloads do NuGet; baixados de novo quando necessário.",
            @"%LOCALAPPDATA%\NuGet\v3-cache", @"%LOCALAPPDATA%\NuGet\plugins-cache"),
        Rule("nuget-packages", "Pacotes do NuGet", RiskLevel.Review, "Pacotes restaurados. O próximo dotnet restore baixa os que faltarem.",
            @"%USERPROFILE%\.nuget\packages\*"),
        Rule("gradle-caches", "Caches do Gradle", RiskLevel.Safe, "Dependências e caches de build baixados pelo Gradle.",
            @"%USERPROFILE%\.gradle\caches\*"),
        Rule("maven-repository", "Repositório do Maven", RiskLevel.Review, "Dependências do Maven (.m2). Baixadas de novo no próximo build.",
            @"%USERPROFILE%\.m2\repository\*"),
        Rule("visual-studio-caches", "Caches do Visual Studio", RiskLevel.Safe, "Cache de componentes do Visual Studio; recriado ao abrir.",
            @"%LOCALAPPDATA%\Microsoft\VisualStudio\*\ComponentModelCache"),
        Rule("vscode-caches", "Caches do VS Code", RiskLevel.Safe, "Caches do editor e extensões baixadas.",
            @"%APPDATA%\Code\Cache", @"%APPDATA%\Code\CachedData", @"%APPDATA%\Code\CachedExtensionVSIXs", @"%APPDATA%\Code\Code Cache"),
        Rule("jetbrains-caches", "Caches do JetBrains", RiskLevel.Safe, "Caches e índices das IDEs JetBrains; recriados ao abrir o projeto.",
            @"%LOCALAPPDATA%\JetBrains\*\caches"),
        // The macOS "simulator runtimes": each system image is gigabytes; the emulator needs it to boot its AVDs.
        Rule(AndroidSystemImagesId, "Imagens do emulador Android", RiskLevel.Review,
            "Imagens de sistema do emulador. AVDs que usam a imagem deixam de abrir até você baixá-la de novo no SDK Manager.",
            @"%LOCALAPPDATA%\Android\Sdk\system-images\*\*", @"%ANDROID_SDK_ROOT%\system-images\*\*", @"%ANDROID_HOME%\system-images\*\*"),
    ];
}
